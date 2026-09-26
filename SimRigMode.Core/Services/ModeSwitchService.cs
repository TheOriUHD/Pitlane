using SimRigMode.Models;
using SimRigMode.Services.Apps;
using SimRigMode.Services.Display;

namespace SimRigMode.Services;

/// <summary>Thrown when a step fails in a way that must abort the whole transition.</summary>
public sealed class TransitionAbortedException : Exception
{
    public TransitionAbortedException(string message) : base(message) { }
}

public sealed class ModeSwitchService
{
    private readonly ConfigurationService _config; private readonly DisplayService _displays; private readonly AppLauncherService _apps; private readonly UsbDeviceService _usb; private readonly AudioService _audio; private readonly HomeAssistantService _integration; private readonly LoggingService _log;
    private readonly SemaphoreSlim _transition = new(1, 1);

    public bool IsSwitching { get; private set; }
    public event Action<WorkspaceMode, string>? StateChanged;
    public event Action<TransitionStep>? StepCompleted;
    /// <summary>A switch's layout is in, before it is checked; with the monitors it parks.</summary>
    public event Action<IReadOnlyList<string>>? LayoutApplied;
    public ModeSwitchService(ConfigurationService config, DisplayService displays, AppLauncherService apps, UsbDeviceService usb, AudioService audio, HomeAssistantService integration, LoggingService log) => (_config, _displays, _apps, _usb, _audio, _integration, _log) = (config, displays, apps, usb, audio, integration, log);

    /// <param name="displays">False for the test switch in Settings: every step runs except the
    /// display step. The displays stay exactly as they are, and so does the mode on record - the
    /// displays are what decide which mode is active, so claiming the other one would be false.</param>
    public async Task<IReadOnlyList<TransitionStep>> SwitchAsync(WorkspaceMode target, CancellationToken token = default, bool displays = true)
    {
        if (!await _transition.WaitAsync(0, token)) throw new InvalidOperationException("A mode transition is already running.");
        IsSwitching = true; var steps = new List<TransitionStep>();
        if (displays) StateChanged?.Invoke(target, $"Switching to {(target == WorkspaceMode.Desktop ? "Desktop" : "Sim Rig")} Mode…");
        _log.Info(displays ? $"Starting transition to {target}" : $"Starting test transition to {target}; the displays are left as they are");
        try
        {
            var mode = target == WorkspaceMode.Desktop ? _config.Current.Desktop : _config.Current.SimRig;
            // Only steps with something to do are run and reported, so the progress a switch
            // shows is what it actually does. The display step always runs.
            var power = _config.Current.Integration.IsActive;
            var audio = HasAudio(mode.Audio);
            bool Usb(DeviceModeAction action) => _config.Current.UsbRules.Any(rule => (target == WorkspaceMode.Desktop ? rule.Desktop : rule.SimRig) == action);

            // The mode being left closes its apps first, so they let go of devices and windows
            // before anything is switched. The new mode's apps start last, once its displays,
            // audio and devices are in place.
            await CloseLinkedAppsStepAsync(target, steps, token);
            if (target == WorkspaceMode.SimRig)
            {
                // Powering the rig is a precondition for everything after it. If the smart plug
                // cannot be reached we must not go on to wait for hardware and enable USB
                // devices that may have no power - so this step is fatal.
                if (power) await Run("Smart plug enabled", () => SetRigPowerAsync(true, token), steps, fatal: true);
                if (Usb(DeviceModeAction.Enabled))
                {
                    await Run("USB devices enabled", () => ApplyUsbAsync(target, DeviceModeAction.Enabled, token), steps);
                    if (!DryRun) await Delay(_config.Current.General.UsbEnumerationWaitSeconds, token);
                }
                await DisplayStepAsync(mode, displays, steps, token);
                if (audio) await Run("Audio switched", () => ApplyAudioAsync(mode), steps);
                if (Usb(DeviceModeAction.Disabled)) await Run("USB devices disabled", () => ApplyUsbAsync(target, DeviceModeAction.Disabled, token), steps);
                await StartLinkedAppsStepAsync(target, steps);
            }
            else
            {
                // USB enable comes BEFORE displays here. The previous order asked Windows for a
                // display path that a USB-governed device might not have finished starting.
                if (Usb(DeviceModeAction.Enabled))
                {
                    await Run("USB devices enabled", () => ApplyUsbAsync(target, DeviceModeAction.Enabled, token), steps);
                    if (!DryRun) await Delay(_config.Current.General.UsbEnumerationWaitSeconds, token);
                }
                await DisplayStepAsync(mode, displays, steps, token);
                if (audio) await Run("Audio switched", () => ApplyAudioAsync(mode), steps);
                if (Usb(DeviceModeAction.Disabled)) await Run("USB devices disabled", () => ApplyUsbAsync(target, DeviceModeAction.Disabled, token), steps);
                if (power)
                {
                    if (!DryRun) await Delay(_config.Current.General.HardwarePowerOffDelaySeconds, token);
                    await Run("Smart plug disabled", () => SetRigPowerAsync(false, token), steps);
                }
                await StartLinkedAppsStepAsync(target, steps);
            }
            token.ThrowIfCancellationRequested();
            if (!displays)
            {
                // A test ran the other steps but left the displays, so the mode on record stays.
                _log.Info($"Test transition to {target} finished with {steps.Count(s => !s.Success)} warning(s); the displays and the mode on record were left as they are");
                return steps;
            }
            if (DryRun)
            {
                // Nothing was switched, so the mode on record stays what it is.
                StateChanged?.Invoke(_config.Current.CurrentMode, "Dry run finished: nothing was changed");
                _log.Info($"Dry run to {target} finished with {steps.Count(s => !s.Success)} warning(s); nothing was changed");
                return steps;
            }
            _config.Current.CurrentMode = target; await _config.SaveAsync(); StateChanged?.Invoke(target, $"{(target == WorkspaceMode.Desktop ? "Desktop" : "Sim Rig")} Mode active");
            _log.Info($"Transition to {target} completed with {steps.Count(s => !s.Success)} warning(s)");
            return steps;
        }
        finally { IsSwitching = false; _transition.Release(); }
    }

    /// <summary>The display step, or in a test switch a step that says the displays were left alone,
    /// so the progress shows it rather than silently leaving it out.</summary>
    private Task DisplayStepAsync(ModeConfig mode, bool displays, List<TransitionStep> steps, CancellationToken token) => displays
        ? Run("Displays configured", () => ApplyDisplayAsync(mode, token), steps, fatal: true)
        : Run(DisplaysLeftAlone, () => Task.CompletedTask, steps);

    /// <summary>The name of the step a test switch reports instead of configuring the displays.</summary>
    public const string DisplaysLeftAlone = "Displays left as they are";

    private async Task ApplyUsbAsync(WorkspaceMode target, DeviceModeAction requested, CancellationToken token)
    {
        var matching = _config.Current.UsbRules.Count(rule => (target == WorkspaceMode.Desktop ? rule.Desktop : rule.SimRig) == requested);
        if (matching == 0) return;

        // USB switching ships disarmed. Rules are kept and shown, but nothing is enabled or
        // disabled until the user arms the feature deliberately, having seen what it would do.
        if (!_config.Current.General.UsbSwitchingArmed)
        {
            _log.Warning($"USB switching is disarmed: {matching} {requested} rule(s) were not applied. Arm it in Settings to enable device switching.");
            return;
        }

        var topology = new UsbTopologyService(_usb);
        var operations = topology.ResolveOperations(_config.Current.UsbRules, target, requested);
        _log.Info($"USB {requested} policy resolved to {operations.Count} physical device operation(s)");
        if (DryRun) { _log.Info($"USB (dry run): would apply {operations.Count} operation(s)"); return; }
        await _usb.ApplyBatchElevatedAsync(operations, token);
    }

    /// <summary>A dry run validates and reports; it never reaches a device, the audio policy or
    /// the smart plug. It used to stop only the display and app steps.</summary>
    private bool DryRun => _config.Current.General.DryRunTransitions;

    private async Task SetRigPowerAsync(bool on, CancellationToken token)
    {
        if (!_config.Current.Integration.IsActive) return;
        if (DryRun) { _log.Info($"Smart plug (dry run): would switch the rig {(on ? "on" : "off")}"); return; }
        await _integration.ExecuteAsync(_config.Current.Integration, on, token);
        if (on) await Delay(_config.Current.General.HardwarePowerOnWaitSeconds, token);
    }

    /// <summary>
    /// Applies the mode's layout. A monitor on the park list that the layout leaves out is NOT
    /// detached: it is added back in a corner of the desktop, because detached it drops off the
    /// bus and reconnects every few seconds, and each reconnect stalls the whole machine.
    /// </summary>
    private async Task ApplyDisplayAsync(ModeConfig mode, CancellationToken token)
    {
        if (mode.Displays is null) throw new InvalidOperationException($"No display layout has been captured for {mode.Name}.");

        var gateway = new DisplayConfigGateway(_displays);
        var profile = mode.Displays;
        var parked = new List<string>();
        if (_config.Current.General.SwitchOffWhenUnused.Count > 0)
        {
            var plan = DisplayPowerPolicy.PlanParking(_config.Current, profile, gateway.CaptureActive(), gateway.CaptureConnected());
            if (plan.Count > 0 && Product.IsStandbyTest)
                _log.Warning("Display: standby test build: " + string.Join(", ", plan.Select(park => park.Path.FriendlyName)) +
                             " is left out of the layout, not parked, so it gets no input and can go into standby.");
            else if (plan.Count > 0)
            {
                profile = DisplayPowerPolicy.WithParked(profile, plan);
                parked = plan.Select(park => park.Path.MonitorDevicePath).ToList();
                _log.Info("Display: parking " + string.Join(", ", plan.Select(park => park.Path.FriendlyName)) +
                          " in a corner instead of detaching it; detached, it drops out and reconnects every few seconds.");
            }
        }

        var outcome = await new DisplayApplyEngine(gateway).ApplyAsync(profile, ApplyOptions() with { Applied = () => LayoutApplied?.Invoke(parked) }, token);

        // Every submitted path, flag set, native result and verification finding, rather than the
        // old bare "Completed".
        foreach (var line in outcome.Log) _log.Info("Display: " + line);

        if (outcome.IsFailure) throw new TransitionAbortedException(outcome.Summary);
        if (outcome.Status != DisplayApplyStatus.Committed) return;   // dry run: nothing changed

        _config.Current.General.ParkedDisplays = parked;
    }

    private DisplayApplyOptions ApplyOptions()
    {
        var general = _config.Current.General;
        return new DisplayApplyOptions(
            DryRun: general.DryRunTransitions,
            SettleMilliseconds: general.DisplaySettleMilliseconds,
            IndirectSettleMilliseconds: general.IndirectDisplaySettleMilliseconds,
            VerifyRetries: general.DisplayVerifyRetries,
            SwitchedIndirect: general.SwitchedIndirectDisplays);
    }

    /// <summary>Only shown when the mode being left has apps to close. Never fatal: an app that
    /// will not close is reported, and the switch carries on.</summary>
    private async Task CloseLinkedAppsStepAsync(WorkspaceMode target, List<TransitionStep> steps, CancellationToken token)
    {
        var apps = AppTransitionPlanner.ToClose(_config.Current, target);
        if (apps.Count == 0) return;
        await Run("Apps closed", async () =>
        {
            if (_config.Current.General.DryRunTransitions) { _log.Info("Apps (dry run): would close " + Names(apps)); return; }
            ReportApps(await _apps.CloseAsync(apps, token));
        }, steps);
    }

    /// <summary>Only shown when the mode being entered has apps to start. Never fatal.</summary>
    private async Task StartLinkedAppsStepAsync(WorkspaceMode target, List<TransitionStep> steps)
    {
        var apps = AppTransitionPlanner.ToStart(_config.Current, target);
        if (apps.Count == 0) return;
        await Run("Apps started", async () =>
        {
            if (_config.Current.General.DryRunTransitions) { _log.Info("Apps (dry run): would start " + Names(apps)); return; }
            // Off the UI thread. Starting a program that asks for administrator rights, such as
            // RaceHub's elevator, waits until the UAC prompt is answered, and Pitlane froze for
            // exactly that long.
            ReportApps(await Task.Run(() => _apps.Start(apps)));
        }, steps);
    }

    private void ReportApps(IReadOnlyList<AppActionResult> results)
    {
        foreach (var result in results)
        {
            if (result.Success) _log.Info("Apps: " + result.Detail);
            else _log.Warning("Apps: " + result.Detail);
        }
        var failures = results.Where(result => !result.Success).Select(result => result.Detail).ToList();
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" ", failures));
    }

    private static string Names(IEnumerable<LinkedApp> apps) => string.Join(", ", apps.Select(app => app.Name));

    private Task ApplyAudioAsync(ModeConfig mode)
    {
        if (!DryRun) _audio.Apply(mode.Audio);
        else _log.Info($"Audio (dry run): would set the default devices for {mode.Name}");
        return Task.CompletedTask;
    }

    private static bool HasAudio(AudioSelection audio) =>
        new[] { audio.PlaybackDefaultId, audio.PlaybackCommunicationsId, audio.RecordingDefaultId, audio.RecordingCommunicationsId }.Any(id => !string.IsNullOrWhiteSpace(id));
    private static Task Delay(int seconds, CancellationToken token) => seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 300)), token) : Task.CompletedTask;
    /// <summary>
    /// Runs one transition step. A fatal step aborts the whole transition instead of being
    /// downgraded to a warning: the previous behaviour meant a failed display apply was followed
    /// by every remaining step anyway, and "completed with 0 warning(s)" only ever meant "nothing
    /// threw synchronously".
    /// </summary>
    private async Task Run(string name, Func<Task> action, List<TransitionStep> steps, bool fatal = false)
    {
        try { await action(); Add(new(name, true, "Completed"), steps); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning($"{name}: {ex.Message}");
            Add(new(name, false, ex.Message), steps);
            if (fatal) throw new TransitionAbortedException($"{name}: {ex.Message}");
        }
    }
    private void Add(TransitionStep step, List<TransitionStep> steps) { steps.Add(step); StepCompleted?.Invoke(step); }
}
