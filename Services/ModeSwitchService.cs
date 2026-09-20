using SimRigMode.Models;

namespace SimRigMode.Services;

public sealed class ModeSwitchService
{
    private readonly ConfigurationService _config; private readonly DisplayService _displays; private readonly UsbDeviceService _usb; private readonly AudioService _audio; private readonly HomeAssistantService _integration; private readonly LoggingService _log;
    private readonly SemaphoreSlim _transition = new(1, 1);
    public bool IsSwitching { get; private set; }
    public event Action<WorkspaceMode, string>? StateChanged;
    public event Action<TransitionStep>? StepCompleted;
    public ModeSwitchService(ConfigurationService config, DisplayService displays, UsbDeviceService usb, AudioService audio, HomeAssistantService integration, LoggingService log) => (_config, _displays, _usb, _audio, _integration, _log) = (config, displays, usb, audio, integration, log);

    public async Task<IReadOnlyList<TransitionStep>> SwitchAsync(WorkspaceMode target, CancellationToken token = default)
    {
        if (!await _transition.WaitAsync(0, token)) throw new InvalidOperationException("A mode transition is already running.");
        IsSwitching = true; var steps = new List<TransitionStep>(); StateChanged?.Invoke(target, $"Switching to {(target == WorkspaceMode.Desktop ? "Desktop" : "Sim Rig")} Mode…");
        _log.Info($"Starting transition to {target}");
        try
        {
            var mode = target == WorkspaceMode.Desktop ? _config.Current.Desktop : _config.Current.SimRig;
            if (target == WorkspaceMode.SimRig)
            {
                await Run("Smart plug enabled", async () => { await _integration.ExecuteAsync(_config.Current.Integration, true, token); if (_config.Current.Integration.Enabled) await Delay(_config.Current.General.HardwarePowerOnWaitSeconds, token); }, steps);
                await Run("USB devices enabled", () => ApplyUsbAsync(target, DeviceModeAction.Enabled, token), steps);
                if (_config.Current.UsbRules.Any(r => r.SimRig == DeviceModeAction.Enabled)) await Delay(_config.Current.General.UsbEnumerationWaitSeconds, token);
                await Run("Displays configured", () => ApplyDisplayAsync(mode), steps);
                await Run("Audio switched", () => ApplyAudioAsync(mode), steps);
                await Run("USB devices disabled", () => ApplyUsbAsync(target, DeviceModeAction.Disabled, token), steps);
            }
            else
            {
                await Run("Displays configured", () => ApplyDisplayAsync(mode), steps);
                await Run("Audio switched", () => ApplyAudioAsync(mode), steps);
                await Run("USB devices enabled", () => ApplyUsbAsync(target, DeviceModeAction.Enabled, token), steps);
                await Run("USB devices disabled", () => ApplyUsbAsync(target, DeviceModeAction.Disabled, token), steps);
                if (_config.Current.Integration.Enabled) await Delay(_config.Current.General.HardwarePowerOffDelaySeconds, token);
                await Run("Smart plug disabled", () => _integration.ExecuteAsync(_config.Current.Integration, false, token), steps);
            }
            token.ThrowIfCancellationRequested();
            _config.Current.CurrentMode = target; await _config.SaveAsync(); StateChanged?.Invoke(target, $"{(target == WorkspaceMode.Desktop ? "Desktop" : "Sim Rig")} Mode active");
            _log.Info($"Transition to {target} completed with {steps.Count(s => !s.Success)} warning(s)");
            return steps;
        }
        finally { IsSwitching = false; _transition.Release(); }
    }

    private async Task ApplyUsbAsync(WorkspaceMode target, DeviceModeAction requested, CancellationToken token)
    {
        if (!_config.Current.UsbRules.Any(rule => (target == WorkspaceMode.Desktop ? rule.Desktop : rule.SimRig) == requested)) return;
        var topology = new UsbTopologyService(_usb);
        var operations = topology.ResolveOperations(_config.Current.UsbRules, target, requested);
        _log.Info($"USB {requested} policy resolved to {operations.Count} physical device operation(s)");
        await _usb.ApplyBatchElevatedAsync(operations, token);
    }
    private Task ApplyDisplayAsync(ModeConfig mode) { if (mode.Displays is null) throw new InvalidOperationException($"No display layout has been captured for {mode.Name}."); _displays.Apply(mode.Displays); return Task.CompletedTask; }
    private Task ApplyAudioAsync(ModeConfig mode) { _audio.Apply(mode.Audio); return Task.CompletedTask; }
    private static Task Delay(int seconds, CancellationToken token) => seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 300)), token) : Task.CompletedTask;
    private async Task Run(string name, Func<Task> action, List<TransitionStep> steps)
    {
        try { await action(); Add(new(name, true, "Completed"), steps); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warning($"{name}: {ex.Message}"); Add(new(name, false, ex.Message), steps); }
    }
    private void Add(TransitionStep step, List<TransitionStep> steps) { steps.Add(step); StepCompleted?.Invoke(step); }
}
