using System.Runtime.InteropServices;
using static SimRigMode.NativeInterop.WindowNative;

namespace SimRigMode.Services.Display;

/// <summary>
/// Makes a parked monitor behave like one that is off: black, with nothing on it.
///
/// A parked monitor stays attached, because detached the PG27AQDP drops off the bus every 7.6 s
/// and each time stalls the whole machine. Attached, it is still part of the desktop, and on
/// 2026-09-26 that showed twice: it stayed lit and live, and windows ended up on it, out of the
/// mouse's reach. Moving them once, during the switch, was not enough. Windows 11 puts each
/// window back where it was the last time the same monitors were connected, seconds after the
/// switch, and the user's Claude window had been on the parked monitor then.
///
/// So while a monitor is parked, this covers it with a black window (on its OLED panel, black
/// pixels are off), and moves any window that lands on it to the main screen: at once when one
/// is brought to the front, and every two seconds for the ones Windows or a program puts there
/// quietly. Nothing else is touched: no other monitor, and no window anywhere else.
///
/// The cover is Pitlane's own window. It never takes focus, has no taskbar button and is not in
/// Alt+Tab. Windows cannot move it; were the monitor to go, Windows would otherwise carry it onto
/// a screen that is in use. On every display change the cover hides and the moving stops, until
/// the owner says again which monitors are parked and where they are.
/// </summary>
public sealed class ParkedScreenKeeper : IDisposable
{
    private const uint WM_APPLY = WM_APP + 1;
    private const uint SweepMilliseconds = 2000;
    private static readonly UIntPtr SweepTimer = new(1);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WindowProcedure _windowProcedure;
    private readonly WinEventProcedure _onForeground;
    // Unique per instance, like the tray's: a fixed name can leave the class pointing at a
    // disposed instance's delegate.
    private readonly string _className = Product.Name + "ParkedScreen_" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private List<ParkedScreen> _wanted = [];
    // The keeper thread's alone:
    private IReadOnlyList<ParkedScreen> _screens = [];
    private readonly List<IntPtr> _covers = [];
    private IntPtr _listener, _hook;
    private bool _placing;
    private volatile bool _disposed;

    /// <summary>The display layout changed. Raised on the keeper's own thread.</summary>
    public event Action? DisplaysChanged;

    /// <summary>What was moved off a parked monitor, for the log. Raised on the keeper's own thread.</summary>
    public event Action<string>? WindowsMoved;

    public ParkedScreenKeeper()
    {
        _windowProcedure = WindowProc;
        _onForeground = (_, _, _, _, _, _, _) => Sweep();
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = Product.Name + " parked screens" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Keeps exactly these screens black and clear, and lets every other go.</summary>
    public void Keep(IReadOnlyList<ParkedScreen> screens)
    {
        lock (_gate) _wanted = screens.ToList();
        if (_listener != IntPtr.Zero) PostMessage(_listener, WM_APPLY, IntPtr.Zero, IntPtr.Zero);
    }

    public void Clear() => Keep([]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_listener != IntPtr.Zero) PostMessage(_listener, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _ready.Dispose();
    }

    private void MessageLoop()
    {
        var instance = GetModuleHandle(null);
        var windowClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            lpszClassName = _className,
            hInstance = instance,
            hbrBackground = GetStockObject(BLACK_BRUSH)
        };
        if (RegisterClass(ref windowClass) == 0) { _ready.Set(); return; }

        // A real top-level window that is never shown: message-only windows get no WM_DISPLAYCHANGE.
        _listener = CreateWindowEx(WS_EX_TOOLWINDOW, _className, null, WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _ready.Set();

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }

        StopKeeping();
        foreach (var cover in _covers) DestroyWindow(cover);
        _covers.Clear();
        if (_listener != IntPtr.Zero) DestroyWindow(_listener);
        _listener = IntPtr.Zero;
        UnregisterClass(_className, instance);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // A native callback frame: an exception escaping it would end the process.
        try
        {
            if (window == _listener)
            {
                switch (message)
                {
                    case WM_APPLY: Apply(); return IntPtr.Zero;
                    case WM_TIMER when wParam == (IntPtr)(long)SweepTimer.ToUInt64(): Sweep(); return IntPtr.Zero;
                    case WM_DISPLAYCHANGE:
                        // Where the parked monitors are may have changed, or one may be gone:
                        // out of the way, and hands off every window, until the owner has looked.
                        foreach (var cover in _covers) ShowWindow(cover, SW_HIDE);
                        StopKeeping();
                        DisplaysChanged?.Invoke();
                        return IntPtr.Zero;
                    case WM_CLOSE: PostQuitMessage(0); return IntPtr.Zero;
                }
            }
            else
            {
                switch (message)
                {
                    case WM_WINDOWPOSCHANGING when !_placing:
                        // Only Pitlane places a cover. Hiding, showing and the Z order still pass.
                        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                        position.flags |= SWP_NOMOVE | SWP_NOSIZE;
                        Marshal.StructureToPtr(position, lParam, false);
                        return IntPtr.Zero;
                    case WM_MOUSEACTIVATE: return new IntPtr(MA_NOACTIVATE);
                    // Placed in physical pixels; the suggested rectangle would resize it.
                    case WM_DPICHANGED: return IntPtr.Zero;
                    // Only Pitlane removes a cover.
                    case WM_CLOSE: return IntPtr.Zero;
                }
            }
        }
        catch { /* the next display change or switch puts it right */ }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void Apply()
    {
        List<ParkedScreen> wanted;
        lock (_gate) wanted = _wanted;
        Cover(wanted);
        if (wanted.Count == 0) { StopKeeping(); return; }

        _screens = wanted;
        if (_hook == IntPtr.Zero)
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        SetTimer(_listener, SweepTimer, SweepMilliseconds, IntPtr.Zero);
        Sweep();
    }

    private void StopKeeping()
    {
        _screens = [];
        if (_listener != IntPtr.Zero) KillTimer(_listener, SweepTimer);
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void Sweep()
    {
        if (_screens.Count == 0) return;
        try
        {
            foreach (var line in ParkedWindowMover.MoveOff(_screens)) WindowsMoved?.Invoke(line);
        }
        catch { /* the next sweep tries again */ }
    }

    private void Cover(List<ParkedScreen> wanted)
    {
        while (_covers.Count > wanted.Count)
        {
            DestroyWindow(_covers[^1]);
            _covers.RemoveAt(_covers.Count - 1);
        }

        _placing = true;
        try
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                var screen = wanted[i];
                if (i < _covers.Count)
                {
                    SetWindowPos(_covers[i], IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
                    continue;
                }
                var cover = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, _className, Product.Name, WS_POPUP,
                    screen.X, screen.Y, screen.Width, screen.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (cover == IntPtr.Zero) break;
                var off = 1;
                DwmSetWindowAttribute(cover, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, sizeof(int));
                var square = DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(cover, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
                _covers.Add(cover);
                // On top of the normal windows once, when it appears. Anything brought to the front
                // on the parked monitor later shows above it, and is moved off it at once.
                SetWindowPos(cover, HWND_TOP, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
        finally { _placing = false; }
    }
}
