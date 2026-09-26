using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimRigMode.Models;
using SimRigMode.Services;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;

namespace SimRigMode;

/// <summary>
/// What a left click on the tray icon opens: the active mode, the hold-to-switch toggle and the
/// way into the full app. It sits next to the tray like Windows' own flyouts and closes as soon as
/// it loses focus. Start with Windows lives in the tray icon's right-click menu.
/// </summary>
public sealed partial class TrayFlyout : Window
{
    private const double LogicalWidth = 360;

    private readonly App _host;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _open, _transitioning;
    private DateTime _closedAt = DateTime.MinValue;

    private enum Status { Active, Switching, Warning, Failed }

    public TrayFlyout(App host)
    {
        _host = host;
        InitializeComponent();
        Title = Product.Name;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        _appWindow.IsShownInSwitchers = false;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
        var corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _appWindow.Closing += (_, args) => { args.Cancel = true; Dismiss(); };
        Activated += OnActivated;
        ElementCompositionPreview.SetIsTranslationEnabled(Body, true);

        Switch.AttachAmbient(Ambient);
        Switch.HoldStarted += _ => Caption.Text = "Keep holding";
        Switch.HoldCancelled += () => { if (!_transitioning) Caption.Text = HoldHint(CurrentMode); };
        Switch.Confirmed += target => _ = _host.SwitchModeAsync(target);
    }

    internal bool IsOpen => _open;

    /// <summary>
    /// Lays the content out once at startup, so the first click opens the flyout at once. The
    /// window is cloaked meanwhile - the compositor draws none of it - and never activated, so
    /// nothing flashes on screen and no window loses the focus.
    /// </summary>
    internal void InitializeHidden()
    {
        Cloak(true);
        _appWindow.Move(new PointInt32(-32000, -32000));
        _appWindow.Show(false);
        void OnFirstLoad(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnFirstLoad;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_open) return;
                LearnInsets();
                _appWindow.Hide();
                Cloak(false);
            });
        }
        Root.Loaded += OnFirstLoad;
    }

    private void Cloak(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    /// <summary>Opens next to the tray icon, or closes if it is open.</summary>
    internal void Toggle(TrayAnchor anchor)
    {
        if (_open) { Dismiss(); return; }
        // A click on the icon while the flyout is open first takes the focus away, which closes
        // it; the click itself then arrives here and must not open it straight away again.
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 350) return;
        Open(anchor);
    }

    internal void Dismiss()
    {
        if (!_open) return;
        _open = false;
        _closedAt = DateTime.UtcNow;
        _appWindow.Hide();
    }

    private void Open(TrayAnchor anchor)
    {
        Refresh();
        Switch.FocusCues = false;
        Place(anchor, fromShell: true);
        Cloak(false);
        _appWindow.Show(true);
        // The first time at a given scaling, the frame can only be measured once shown.
        if (LearnInsets()) Place(anchor, fromShell: true);
        Activate();
        SetForegroundWindow(_hwnd);
        _open = true;
        Enter();
        // Focus starts on the switch, set from code so no keyboard focus ring is drawn around
        // anything; Space or Enter still hold the switch straight away.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Switch.Focus(FocusState.Programmatic));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) Dismiss();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Dismiss();
        e.Handled = true;
    }

    /// <summary>Focus is shown only once the keyboard is actually used to move it.</summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Tab or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) Switch.FocusCues = true;
    }

    // ===== content ====================================================================

    private WorkspaceMode CurrentMode => _host.Configuration.Current.CurrentMode == WorkspaceMode.SimRig ? WorkspaceMode.SimRig : WorkspaceMode.Desktop;

    private void Refresh()
    {
        var config = _host.Configuration.Current;
        if (!_transitioning)
        {
            var mode = CurrentMode;
            Switch.SetMode(mode, animate: false);
            ModeTitle.Text = ModeName(mode);
            SetStatus(Status.Active, config.CurrentMode == WorkspaceMode.Unknown ? "Not switched yet" : "Active");
            Caption.Text = HoldHint(mode);
        }
        DryRunBadge.Visibility = config.General.DryRunTransitions || Product.IsStandbyTest ? Visibility.Visible : Visibility.Collapsed;
        DryRunBadgeText.Text = config.General.DryRunTransitions ? "Dry run" : "Standby test";
    }

    private void SetStatus(Status status, string text)
    {
        var color = status switch
        {
            Status.Switching or Status.Warning => Color.FromArgb(255, 251, 191, 36),
            Status.Failed => Color.FromArgb(255, 248, 113, 113),
            _ => Color.FromArgb(255, 52, 211, 153)
        };
        StatusDot.Fill = new SolidColorBrush(color);
        StatusHalo.Fill = new SolidColorBrush(color);
        StatusText.Text = text;
        Breathe(status == Status.Switching ? 1100 : 2600);
    }

    /// <summary>The halo round the status dot swells and fades, faster while switching.</summary>
    private void Breathe(int periodMs)
    {
        var halo = ElementCompositionPreview.GetElementVisual(StatusHalo);
        var compositor = halo.Compositor;
        halo.CenterPoint = new Vector3(5, 5, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.6f, 0.6f, 1));
        scale.InsertKeyFrame(1, new Vector3(1.6f, 1.6f, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.55f);
        fade.InsertKeyFrame(1, 0);
        foreach (var animation in new KeyFrameAnimation[] { scale, fade })
        {
            animation.Duration = TimeSpan.FromMilliseconds(periodMs);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        halo.StartAnimation("Scale", scale);
        halo.StartAnimation("Opacity", fade);
    }

    // ===== switching ==================================================================

    internal void BeginTransition(WorkspaceMode target) => Enqueue(() =>
    {
        _transitioning = true;
        // Started from the menu, a shortcut or the full app: slide over now, like a hold would.
        if (Switch.Mode != target) Switch.SetMode(target);
        Switch.IsBusy = true;
        ModeTitle.Text = ModeName(target);
        SetStatus(Status.Switching, "Switching…");
        Caption.Text = $"Switching to {ModeName(target)}";
        Refit();
    });

    internal void EndTransition(IReadOnlyList<TransitionStep> results) => Enqueue(() =>
    {
        _transitioning = false;
        Switch.IsBusy = false;
        var mode = CurrentMode;
        Switch.SetMode(mode);
        ModeTitle.Text = ModeName(mode);
        var warnings = results.Where(step => !step.Success).ToList();
        if (_host.Configuration.Current.General.DryRunTransitions)
        {
            SetStatus(Status.Active, "Active");
            Caption.Text = "Dry run finished. Nothing was changed.";
        }
        else if (warnings.Count > 0)
        {
            SetStatus(Status.Warning, $"Active, {warnings.Count} warning{(warnings.Count == 1 ? "" : "s")}");
            Caption.Text = warnings.Count == 1
                ? $"Switched to {ModeName(mode)}. {warnings[0].Detail}"
                : $"Switched to {ModeName(mode)}. {warnings.Count} steps had problems; Open Pitlane shows them.";
        }
        else
        {
            SetStatus(Status.Active, "Active");
            Caption.Text = $"Switched to {ModeName(mode)}";
        }
        Refit();
    });

    internal void TransitionFailed(string message) => Enqueue(() =>
    {
        _transitioning = false;
        Switch.IsBusy = false;
        var mode = CurrentMode;
        Switch.SetMode(mode);
        ModeTitle.Text = ModeName(mode);
        SetStatus(Status.Failed, "Switch stopped");
        Caption.Text = message;
        Refit();
    });

    /// <summary>
    /// Fits the window to its content again, next to the tray. A switch moves the taskbar to
    /// another screen along with the displays, often one with other scaling. While the taskbar
    /// settles there the shell may not say where the icon is; on 2026-09-26 that left the flyout
    /// floating mid-screen at the old screen's size. Then the icon's last known place stands in.
    /// </summary>
    internal void Refit()
    {
        if (!_open) return;
        var fromShell = _host.TryGetTrayAnchor(out var anchor);
        if (!fromShell) anchor = TrayCorner();
        Place(anchor, fromShell);
        if (LearnInsets()) Place(anchor, fromShell);
    }

    /// <summary>How far the icon last sat from the right edge of its screen's work area, in
    /// logical pixels. The clock and the system icons keep it there on any screen.</summary>
    private double _iconFromRight;

    /// <summary>Where the tray icon should be on the main screen: in its taskbar, as far from
    /// the right edge as it was last seen.</summary>
    private TrayAnchor TrayCorner()
    {
        var monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 96) / 96.0;
        var x = info.rcWork.Right - (int)Math.Round(_iconFromRight * scale);
        var y = info.rcWork.Bottom < info.rcMonitor.Bottom ? info.rcMonitor.Bottom - 1 : info.rcWork.Bottom - 1;
        return new TrayAnchor(x - 1, y - 1, x, y);
    }

    // ===== placement ==================================================================

    /// <summary>Above the tray icon, inside the work area of its screen, sized for that screen's
    /// scaling - the way Windows places its own flyouts.</summary>
    /// <param name="fromShell">The shell said where the icon is, so it is remembered.</param>
    private void Place(TrayAnchor anchor, bool fromShell)
    {
        var point = new POINT { X = (anchor.Left + anchor.Right) / 2, Y = (anchor.Top + anchor.Bottom) / 2 };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : GetDpiForWindow(_hwnd)) / 96.0;

        Root.Measure(new Windows.Foundation.Size(LogicalWidth, double.PositiveInfinity));
        var width = (int)Math.Round(LogicalWidth * scale);
        var height = (int)Math.Round(Math.Ceiling(Root.DesiredSize.Height) * scale);
        var gap = (int)Math.Round(12 * scale);
        var work = info.rcWork;
        var screen = info.rcMonitor;
        if (fromShell) _iconFromRight = Math.Max(0, (work.Right - point.X) / scale);

        int x = Fit(point.X - width / 2, work.Left + gap, work.Right - gap - width), y;
        if (work.Bottom < screen.Bottom) y = work.Bottom - gap - height;
        else if (work.Top > screen.Top) y = work.Top + gap;
        else y = Fit(anchor.Top - gap - height, work.Top + gap, work.Bottom - gap - height);
        if (work.Right < screen.Right && anchor.Left >= work.Right) x = work.Right - gap - width;
        else if (work.Left > screen.Left && anchor.Right <= work.Left) x = work.Left + gap;

        var targetDpi = (uint)Math.Round(scale * 96);
        if (GetDpiForWindow(_hwnd) != targetDpi)
        {
            // Coming from a screen with other scaling. Land on the target screen first, so the
            // scale change - which resizes the window by the ratio of the two - happens now and
            // not after the window has its final size. Otherwise a window that briefly sat mostly
            // on a 100% screen came back 1.5 times too large on a 150% one.
            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        // (x, y, width, height) is what should be visible. The window reaches further than that
        // with an invisible frame, differently on each side; the content fills the visible part.
        var (left, top, right, bottom) = _insets.TryGetValue(targetDpi, out var inset) ? inset : default;
        _appWindow.MoveAndResize(new RectInt32(x - left, y - top, width + left + right, height + top + bottom));
    }

    /// <summary>How far the window reaches past what is visible, on each side, per scaling.</summary>
    private readonly Dictionary<uint, (int Left, int Top, int Right, int Bottom)> _insets = [];

    /// <summary>Learns the invisible frame from a shown window: its full rectangle against the
    /// part the compositor draws. True when it was not known yet for this scaling.</summary>
    private bool LearnInsets()
    {
        var dpi = GetDpiForWindow(_hwnd);
        if (_insets.ContainsKey(dpi) || !GetWindowRect(_hwnd, out var outer)) return false;
        if (DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) != 0 || visible.Right <= visible.Left) return false;
        _insets[dpi] = (visible.Left - outer.Left, visible.Top - outer.Top, outer.Right - visible.Right, outer.Bottom - visible.Bottom);
        return true;
    }

    private static int Fit(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private void Enter()
    {
        var body = ElementCompositionPreview.GetElementVisual(Body);
        var compositor = body.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 16, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        rise.Duration = TimeSpan.FromMilliseconds(340);
        body.StartAnimation("Opacity", fade);
        body.StartAnimation("Translation", rise);
    }

    // ===== buttons ====================================================================

    private void Open_Click(object sender, RoutedEventArgs e) { Dismiss(); _host.ShowSettings(); }

    // ===== helpers ====================================================================

    private static string ModeName(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? "Sim Rig" : "Desktop";
    private static WorkspaceMode Other(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? WorkspaceMode.Desktop : WorkspaceMode.SimRig;
    private static string HoldHint(WorkspaceMode mode) => $"Hold for 3 seconds to switch to {ModeName(Other(mode))}";

    private void Enqueue(Action action) => DispatcherQueue.TryEnqueue(() => action());

    private const int GWL_EXSTYLE = -20, DWMWA_CLOAK = 13, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
