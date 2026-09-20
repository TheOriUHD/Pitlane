using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using SimRigMode.Models;
using SimRigMode.Services;

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
    private bool _cleanedUp;
    private DispatcherQueue _dispatcher = null!;

    internal LoggingService Log { get; private set; } = null!;
    internal ConfigurationService Configuration { get; private set; } = null!;
    internal DisplayService Displays { get; } = new();
    internal UsbDeviceService Usb { get; } = new();
    internal AudioService Audio { get; } = new();
    internal ModeSwitchService Switcher { get; private set; } = null!;
    internal StartupService StartWithWindows { get; } = new();
    internal SettingsWindow? Settings { get; private set; }

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
        Switcher = new ModeSwitchService(Configuration, Displays, Usb, Audio, new HomeAssistantService(), Log);
        Log.Info("WinUI startup: backend initialized");
        try
        {
            Settings = new SettingsWindow(this);
            Settings.InitializeHidden();
        }
        catch (Exception ex)
        {
            Log.Error("WinUI settings window initialization failed", ex);
            throw;
        }

        _tray = new TrayService(StartWithWindows.IsEnabled);
        _tray.SettingsRequested += () => Enqueue(ShowSettings);
        _tray.SwitchRequested += mode => { Enqueue(async () => await SwitchModeAsync(mode)); return Task.CompletedTask; };
        _tray.StartupChanged += enabled => { try { StartWithWindows.SetEnabled(enabled); } catch (Exception ex) { Log.Error("Startup setting failed", ex); } };
        _tray.ExitRequested += () => Enqueue(ExitApplication);
        Switcher.StateChanged += (mode, _) => Enqueue(() => _tray?.SetState(mode, Switcher.IsSwitching));
        _tray.SetState(Configuration.Current.CurrentMode, false);
        ConfigureHotkeys();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        UnhandledException += (_, eventArgs) => { Log.Error("Unhandled UI error", eventArgs.Exception); _tray?.ShowBalloon("SimRigMode error", eventArgs.Exception.Message, TrayBalloonIcon.Error); eventArgs.Handled = true; };
        Log.Info("SimRigMode started with WinUI 3");
    }

    internal async Task SwitchModeAsync(WorkspaceMode mode)
    {
        if (Switcher.IsSwitching) { _tray?.ShowBalloon("SimRigMode", "A mode transition is already running."); return; }
        try
        {
            _transitionCancellation?.Dispose(); _transitionCancellation = new CancellationTokenSource();
            _tray?.SetState(Configuration.Current.CurrentMode, true); Settings?.BeginTransition(mode);
            var results = await Switcher.SwitchAsync(mode, _transitionCancellation.Token); var warnings = results.Where(x => !x.Success).ToList();
            _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.EndTransition(mode, results);
            if (warnings.Count > 0) _tray?.ShowBalloon("Mode switched with warnings", string.Join("\n", warnings.Select(w => w.Name + ": " + w.Detail)), TrayBalloonIcon.Warning);
            else _tray?.ShowBalloon("SimRigMode", $"{(mode == WorkspaceMode.Desktop ? "Desktop" : "Sim rig")} mode is active.");
        }
        catch (OperationCanceledException) { Log.Info("Mode transition cancelled"); _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.TransitionFailed("Cancelled by user."); }
        catch (Exception ex) { Log.Error("Mode transition failed", ex); _tray?.SetState(Configuration.Current.CurrentMode, false); Settings?.TransitionFailed(ex.Message); _tray?.ShowBalloon("Mode switch failed", ex.Message, TrayBalloonIcon.Error); }
    }

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
        if (_cleanedUp) return; _cleanedUp = true; _lifetime?.Cancel(); SystemEvents.PowerModeChanged -= OnPowerModeChanged; _hotkeys?.Dispose(); _tray?.Dispose(); _activateEvent?.Dispose(); if (_ownsMutex) _mutex?.ReleaseMutex(); _mutex?.Dispose(); Log?.Info("SimRigMode exited");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
