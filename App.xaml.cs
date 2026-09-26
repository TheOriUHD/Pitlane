using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using SimRigMode.Models;
using SimRigMode.Services;
using SimRigMode.Services.Apps;
using SimRigMode.Services.Display;

namespace SimRigMode;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _transitionCancellation;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private MonitorHotplugWatcher? _hotplug;
    private ParkedScreenKeeper? _keeper;
    private IReadOnlyList<string>? _parkingNow;
    private int _keeperSync;
    private string _kept = "";
    private readonly DisplayBounceDetector _bounces = new();
    private bool _cleanedUp;
    private DispatcherQueue _dispatcher = null!;

    internal LoggingService Log { get; private set; } = null!;
    internal ConfigurationService Configuration { get; private set; } = null!;
    internal DisplayService Displays { get; } = new();
    internal AppLauncherService Apps { get; } = new(new SystemProcessController());
    internal UsbDeviceService Usb { get; } = new();
    internal AudioService Audio { get; } = new();
    internal HomeAssistantService HomeAssistant { get; } = new();
    internal ModeSwitchService Switcher { get; private set; } = null!;
    internal StartupService StartWithWindows { get; } = new();
    internal SettingsWindow? Settings { get; private set; }

    /// <summary>The small window a left click on the tray icon opens. Null if it could not be
    /// created; the tray then opens the full app instead.</summary>
    internal TrayFlyout? Flyout { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // The elevated device worker is handled in Program.Main, before WinUI starts and before
        // this type exists. It must never run here: see Services/Elevation/ElevatedWorker.cs.
        _mutex = new Mutex(true, "Local\\SimRigMode.Singleton.2E894921", out var created);
        _ownsMutex = created;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SimRigMode.Activate.2E894921");
        if (!created) { AllowSetForegroundWindow(-1); _activateEvent.Set(); Exit(); return; }

        _lifetime = new CancellationTokenSource();
        _ = WatchActivationAsync(_lifetime.Token);
        Log = new LoggingService();
        Log.Info("WinUI startup: application launched");
        Configuration = new ConfigurationService(Log);
        Task.Run(Configuration.LoadAsync).GetAwaiter().GetResult();
        Switcher = new ModeSwitchService(Configuration, Displays, Apps, Usb, Audio, HomeAssistant, Log);
        Log.Info("WinUI startup: backend initialized");
        // The full app's window is created the first time it is opened, not here: preparing it
        // at startup meant showing it offscreen for a moment, and that flashed.
        try
        {
            Flyout = new TrayFlyout(this);
            Flyout.InitializeHidden();
        }
        catch (Exception ex)
        {
            Log.Error("The tray flyout could not be created; the tray icon opens the full app instead", ex);
            Flyout = null;
        }

        _tray = new TrayService(StartWithWindows.IsEnabled, LoadTrayIcons());
        _tray.Invoked += anchor => Enqueue(() => { if (Flyout is { } flyout) flyout.Toggle(anchor); else ShowSettings(); });
        _tray.OpenRequested += () => Enqueue(ShowSettings);
        _tray.SwitchRequested += mode => { Enqueue(async () => await SwitchModeAsync(mode)); return Task.CompletedTask; };
        _tray.StartupChanged += enabled => { try { StartWithWindows.SetEnabled(enabled); } catch (Exception ex) { Log.Error("Startup setting failed", ex); } };
        _tray.ExitRequested += () => Enqueue(ExitApplication);
        Switcher.StateChanged += (mode, _) => Enqueue(() => _tray?.SetState(mode, Switcher.IsSwitching));
        _tray.SetState(Configuration.Current.CurrentMode, false);
        ConfigureHotkeys();
        StartDisplayWatchdog();
        StartParkedScreenKeeper();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        UnhandledException += (_, eventArgs) => { Log.Error("Unhandled UI error", eventArgs.Exception); _tray?.ShowBalloon($"{Product.Name} error", eventArgs.Exception.Message, TrayBalloonIcon.Error); eventArgs.Handled = true; };
        Log.Info($"{Product.Name} started");
        if (Product.IsStandbyTest)
            Log.Warning("Standby test build: a monitor on the park list is left out of the layout instead of parked, " +
                        "so it can go into standby. Every monitor dropout and return is logged.");
    }

    /// <summary>The tray icon for each mode, embedded from Assets\Tray.</summary>
    private static Dictionary<WorkspaceMode, byte[]> LoadTrayIcons()
    {
        var icons = new Dictionary<WorkspaceMode, byte[]>();
        foreach (var (mode, name) in new[] { (WorkspaceMode.Desktop, "Tray.Desktop.ico"), (WorkspaceMode.SimRig, "Tray.SimRig.ico") })
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            icons[mode] = copy.ToArray();
        }
        return icons;
    }

    /// <summary>What a switch did: its steps, or why it stopped or never started.</summary>
    internal sealed record SwitchOutcome(IReadOnlyList<TransitionStep> Steps, string? Error);

    /// <param name="displays">False for the test switch in Settings: everything but the displays.
    /// It is reported there only. The tray, the flyout and the overview keep showing the mode that
    /// is really active, because a test does not change it.</param>
    internal async Task<SwitchOutcome> SwitchModeAsync(WorkspaceMode mode, bool displays = true)
    {
        if (Switcher.IsSwitching)
        {
            if (displays) _tray?.ShowBalloon(Product.Name, "A mode switch is already running.");
            return new([], "A mode switch is already running.");
        }
        try
        {
            _transitionCancellation?.Dispose(); _transitionCancellation = new CancellationTokenSource();
            if (!displays) return new(await Switcher.SwitchAsync(mode, _transitionCancellation.Token, displays: false), null);
            _tray?.SetState(Configuration.Current.CurrentMode, true); Settings?.BeginTransition(mode); Flyout?.BeginTransition(mode);
            if (!Configuration.Current.General.DryRunTransitions && !Product.IsCaptureDemo) { _parkingNow = null; _keeper?.Clear(); }
            var results = await Switcher.SwitchAsync(mode, _transitionCancellation.Token); var warnings = results.Where(x => !x.Success).ToList();
            _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.EndTransition(mode, results); Flyout?.EndTransition(results);
            // The flyout shows the outcome itself; a notification on top of it would be noise.
            if (Flyout?.IsOpen == true) return new(results, null);
            if (warnings.Count > 0) _tray?.ShowBalloon("Mode switched with warnings", string.Join("\n", warnings.Select(w => w.Name + ": " + w.Detail)), TrayBalloonIcon.Warning);
            else if (Configuration.Current.General.DryRunTransitions) _tray?.ShowBalloon(Product.Name, "Dry run finished. Nothing was changed.");
            else _tray?.ShowBalloon(Product.Name, $"{(mode == WorkspaceMode.Desktop ? "Desktop" : "Sim Rig")} mode is active.");
            return new(results, null);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Mode transition cancelled");
            if (displays) { _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.TransitionFailed("Cancelled by user."); Flyout?.TransitionFailed("Cancelled."); }
            return new([], "Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("Mode transition failed", ex);
            if (displays)
            {
                _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.TransitionFailed(ex.Message); Flyout?.TransitionFailed(ex.Message);
                if (Flyout?.IsOpen != true) _tray?.ShowBalloon("Mode switch failed", ex.Message, TrayBalloonIcon.Error);
            }
            return new([], ex.Message);
        }
        finally
        {
            // Whatever the switch did, or undid: the parked monitors as they are now.
            if (displays) { _parkingNow = null; SyncParkedScreens(); }
        }
    }

    /// <summary>
    /// Makes a parked monitor behave like one that is off: black, with no windows on it. It
    /// starts as soon as a switch has applied its layout, not when the whole switch has ended.
    /// </summary>
    private void StartParkedScreenKeeper()
    {
        try
        {
            _keeper = new ParkedScreenKeeper();
            _keeper.WindowsMoved += line => Enqueue(() => Log.Info("Display: " + line));
            _keeper.DisplaysChanged += () => Enqueue(async () =>
            {
                // Display changes come in bursts; look again once they are over.
                var sync = ++_keeperSync;
                await Task.Delay(400);
                if (sync != _keeperSync) return;
                SyncParkedScreens();
                // An open flyout follows the taskbar to its new screen and scaling, and once more
                // when the taskbar has settled there.
                Flyout?.Refit();
                await Task.Delay(1600);
                if (sync == _keeperSync) Flyout?.Refit();
            });
            Switcher.LayoutApplied += parked => Enqueue(() => { _parkingNow = parked; SyncParkedScreens(); });
            SyncParkedScreens();
        }
        catch (Exception ex)
        {
            Log.Error("A parked monitor cannot be kept black and clear", ex);
            _keeper = null;
        }
    }

    private void SyncParkedScreens()
    {
        if (_keeper is null) return;
        // Mid-switch, the parked list on record is the one being left, until the new layout is in.
        var parked = _parkingNow ?? (Switcher.IsSwitching ? null : Configuration.Current.General.ParkedDisplays);
        if (parked is null) return;
        try
        {
            var screens = DisplayPowerPolicy.LocateParked(new DisplayConfigGateway(Displays).CaptureActive(), parked);
            _keeper.Keep(screens);
            var kept = string.Join(", ", screens.Select(screen => screen.FriendlyName));
            if (kept != _kept && kept.Length > 0) Log.Info($"Display: {kept} is parked; keeping it black, and any window on it goes to the main screen");
            _kept = kept;
        }
        catch (Exception ex) { Log.Warning("Parked monitor: " + ex.Message); }
    }

    internal bool TryGetTrayAnchor(out TrayAnchor anchor)
    {
        anchor = default;
        return _tray is not null && _tray.TryGetIconRect(out anchor);
    }

    /// <summary>
    /// Watches for a monitor that keeps dropping off the bus and coming back. That loop was the
    /// real cause of the 2026-09-13 and 2026-09-24 stutters, and nothing reported it. It is
    /// event-driven and report-only: it never asks anything of a device and never changes the
    /// display layout. Displays change only in a mode switch the user starts.
    /// </summary>
    private void StartDisplayWatchdog()
    {
        _hotplug = new MonitorHotplugWatcher();
        _hotplug.Changed += (path, arrived) =>
        {
            if (Product.IsStandbyTest)
                Enqueue(() => Log.Info($"Display watchdog: {DisplayPowerPolicy.Describe(Configuration.Current, path)} {(arrived ? "came back" : "dropped out")}"));
            if (arrived) return;
            var bounce = _bounces.RecordDropout(path, DateTime.UtcNow);
            if (bounce is not null) Enqueue(async () => await OnDisplayBounceAsync(bounce));
        };
        if (_hotplug.Start()) Log.Info("Display watchdog: watching for monitors that keep dropping out");
        else Log.Warning("Display watchdog could not be started; a monitor that keeps dropping out will not be reported");
    }

    private async Task OnDisplayBounceAsync(DisplayBounce bounce)
    {
        var config = Configuration.Current;
        var name = DisplayPowerPolicy.Describe(config, bounce.MonitorDevicePath);
        Log.Warning($"Display watchdog: {name} dropped off the bus {bounce.Dropouts} times in {bounce.Span.TotalSeconds:0} s " +
                    $"({bounce.MonitorDevicePath}). Every return stalls Windows' input and window handling.");

        string title, message;
        if (DisplayPowerPolicy.IsInCurrentLayout(config, bounce.MonitorDevicePath))
        {
            title = $"{name} keeps disconnecting";
            message = $"It is part of the current layout but dropped out {bounce.Dropouts} times in {bounce.Span.TotalSeconds:0} seconds, " +
                      "and the PC stutters each time. Check its cable and connector, or try another port.";
        }
        else
        {
            // Detached, this monitor loops. The watchdog does not attach it: the layout changes
            // only in a switch the user starts. It only makes sure SimRigMode's own next switch
            // parks the monitor instead of leaving it out again.
            if (DisplayPowerPolicy.Remember(config.General, bounce.MonitorDevicePath, name, "watchdog"))
            {
                try { await Configuration.SaveAsync(); }
                catch (Exception ex) { Log.Error("Display watchdog: the park setting could not be saved", ex); }
                Log.Info($"Display watchdog: {name} added to the park list. {Product.Name}'s next mode switch keeps it attached in a corner instead of leaving it out.");
            }
            title = $"{name} keeps dropping out";
            message = Product.IsStandbyTest
                ? $"Standby test: without input it dropped out {bounce.Dropouts} times in {bounce.Span.TotalSeconds:0} seconds, " +
                  "so the PC stutters. Switch to Sim Rig to stop it."
                : "It is connected but left out of the layout, and it reconnects every few seconds, so the PC stutters. " +
                  "In Settings > System > Display, choose 'Extend desktop to this display' for it, or unplug its video cable.";
        }

        _tray?.ShowBalloon(Clip(title, 63), Clip(message, 255), TrayBalloonIcon.Warning);
        Settings?.ShowDisplayWatchdogWarning(title, message);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    internal void CancelTransition() => _transitionCancellation?.Cancel();
    internal void SetStartWithWindows(bool enabled) { StartWithWindows.SetEnabled(enabled); _tray?.SetStartup(enabled); }
    internal void ConfigureHotkeys()
    {
        _hotkeys?.Dispose(); _hotkeys = new HotkeyService(); if (!Configuration.Current.General.HotkeysEnabled) return;
        if (!_hotkeys.Register(Configuration.Current.General.DesktopHotkey, () => Enqueue(async () => await SwitchModeAsync(WorkspaceMode.Desktop)))) Log.Warning("Desktop hotkey could not be registered");
        if (!_hotkeys.Register(Configuration.Current.General.SimRigHotkey, () => Enqueue(async () => await SwitchModeAsync(WorkspaceMode.SimRig)))) Log.Warning("Sim rig hotkey could not be registered");
    }
    internal void ShowSettings() => Enqueue(() => { Settings ??= new SettingsWindow(this); Settings.ShowAndActivate(); });
    private void Enqueue(Action action) => _dispatcher.TryEnqueue(() => action());
    private void Enqueue(Func<Task> action) => _dispatcher.TryEnqueue(async () => await action());
    private async Task WatchActivationAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) { try { if (await Task.Run(() => _activateEvent!.WaitOne(1000), token)) _dispatcher.TryEnqueue(ShowSettings); } catch (OperationCanceledException) { break; } }
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) { Log.Info("Windows resumed from sleep"); _tray?.SetState(Configuration.Current.CurrentMode, false); } }
    private void ExitApplication() { Cleanup(); Exit(); }
    private void Cleanup()
    {
        if (_cleanedUp) return; _cleanedUp = true; _lifetime?.Cancel(); SystemEvents.PowerModeChanged -= OnPowerModeChanged; _hotplug?.Dispose(); _keeper?.Dispose(); _hotkeys?.Dispose(); _tray?.Dispose(); _activateEvent?.Dispose(); if (_ownsMutex) _mutex?.ReleaseMutex(); _mutex?.Dispose(); Log?.Info($"{Product.Name} exited");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
