using System.Runtime.InteropServices;
using SimRigMode.Models;

namespace SimRigMode.Services;

public enum TrayBalloonIcon { Info = 1, Warning = 2, Error = 3 }

public sealed class TrayService : IDisposable
{
    private const uint WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_CLOSE = 0x0010, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8, MF_GRAYED = 1, TPM_RETURNCMD = 0x100;
    private const int DesktopId = 1001, RigId = 1002, SettingsId = 1003, StartupId = 1004, ExitId = 1005;
    private const uint WS_OVERLAPPED = 0x00000000, WM_NULL = 0x0000, TPM_RIGHTBUTTON = 0x0002, IMAGE_ICON = 1;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private readonly Thread _thread; private readonly ManualResetEventSlim _ready = new(); private readonly WndProcDelegate _wndProc;
    private IntPtr _window, _icon; private uint _taskbarCreated; private bool _startup, _switching; private WorkspaceMode _mode;
    // Unique per instance, with a matching UnregisterClass on teardown. A fixed name leaves the
    // class registered against a dead instance's delegate thunk.
    private readonly string _className = "SimRigModeTrayWindow_" + Guid.NewGuid().ToString("N");
    private volatile bool _disposed;
    public event Func<WorkspaceMode, Task>? SwitchRequested; public event Action? SettingsRequested; public event Action<bool>? StartupChanged; public event Action? ExitRequested;
    public TrayService(bool startup) { _startup = startup; _wndProc = WndProc; _thread = new Thread(MessageLoop) { IsBackground = true, Name = "SimRigMode tray" }; _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); _ready.Wait(); }
    public void SetState(WorkspaceMode mode, bool switching) { _mode = mode; _switching = switching; UpdateIcon(); }
    public void SetStartup(bool enabled) => _startup = enabled;
    public void ShowBalloon(string title, string message, TrayBalloonIcon icon = TrayBalloonIcon.Info) { var data = CreateData(); data.uFlags = NIF_INFO; data.szInfoTitle = title; data.szInfo = message; data.dwInfoFlags = (uint)icon; Shell_NotifyIcon(NIM_MODIFY, ref data); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != IntPtr.Zero) PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        // Wait for NIM_DELETE to actually run. Without this the process can exit first, leaving
        // a ghost icon in the notification area until the user hovers over it.
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _ready.Dispose();
    }
    private void MessageLoop()
    {
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), lpszClassName = _className, hInstance = GetModuleHandle(null) };
        if (RegisterClass(ref wc) == 0) { _ready.Set(); return; }

        // A REAL top-level window, never shown - not HWND_MESSAGE. Message-only windows do not
        // receive broadcast messages, which silently killed the TaskbarCreated recovery below,
        // and they cannot be foregrounded, which broke the popup menu (see ShowMenu).
        _window = CreateWindowEx(0, _className, "SimRigMode", WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadAppIcon();

        var data = CreateData(); data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        // NIM_ADD commonly fails when the app starts before the shell is ready, which is exactly
        // the "start with Windows" path. Retry briefly rather than silently having no icon.
        for (var attempt = 0; attempt < 10 && !Shell_NotifyIcon(NIM_ADD, ref data); attempt++) Thread.Sleep(500);
        _ready.Set();

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }

        data = CreateData(); Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        _window = IntPtr.Zero;
        UnregisterClass(_className, GetModuleHandle(null));
    }

    /// <summary>
    /// The application icon from this executable's own resources, at the shell's small-icon size.
    /// The previous call asked the EXE for resource id 32512, which is the SYSTEM IDI_APPLICATION
    /// id and is only valid with a null instance handle - so it returned NULL and fell back to the
    /// generic Windows icon. Assets/SimRigMode.ico was never actually used in the tray.
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        var module = GetModuleHandle(null);
        var cx = GetSystemMetrics(SM_CXSMICON);
        var cy = GetSystemMetrics(SM_CYSMICON);
        foreach (var id in new[] { 32512, 1, 2 })
        {
            var handle = LoadImage(module, new IntPtr(id), IMAGE_ICON, cx, cy, 0);
            if (handle != IntPtr.Zero) return handle;
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }
    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0) { var restored = CreateData(); restored.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP; Shell_NotifyIcon(NIM_ADD, ref restored); return IntPtr.Zero; }
        if (msg == WM_TRAY)
        {
            // Inside a native callback frame: an escaping exception terminates the process and is
            // invisible to the WinUI unhandled-exception handler.
            try
            {
                var mouse = (uint)lParam.ToInt64();
                if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) SettingsRequested?.Invoke();
                else if (mouse == WM_RBUTTONUP) ShowMenu();
            }
            catch { /* the handlers own their own reporting */ }
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING | (_switching ? MF_GRAYED : 0) | (_mode == WorkspaceMode.Desktop ? MF_CHECKED : 0), DesktopId, "Switch to Desktop mode");
        AppendMenu(menu, MF_STRING | (_switching ? MF_GRAYED : 0) | (_mode == WorkspaceMode.SimRig ? MF_CHECKED : 0), RigId, "Switch to Sim rig mode");
        AppendMenu(menu, MF_SEPARATOR, 0, null); AppendMenu(menu, MF_STRING, SettingsId, "Settings"); AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | (_startup ? MF_CHECKED : 0), StartupId, "Start with Windows"); AppendMenu(menu, MF_STRING, ExitId, "Exit");
        GetCursorPos(out var point);
        // The documented notification-area pattern: foreground the owner first so the menu takes
        // focus, and post a dummy message afterwards so it dismisses when the user clicks away.
        // Neither worked before, because the owner was a message-only window and the trailing
        // post was missing entirely.
        SetForegroundWindow(_window);
        var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, _window, IntPtr.Zero);
        PostMessage(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        Execute((int)command);
    }
    private void Execute(int id)
    {
        if (id == DesktopId && !_switching) Observe(SwitchRequested?.Invoke(WorkspaceMode.Desktop));
        else if (id == RigId && !_switching) Observe(SwitchRequested?.Invoke(WorkspaceMode.SimRig));
        else if (id == SettingsId) SettingsRequested?.Invoke(); else if (id == StartupId) { _startup = !_startup; StartupChanged?.Invoke(_startup); } else if (id == ExitId) ExitRequested?.Invoke();
    }
    /// <summary>Keeps a fire-and-forget task's faults from becoming unobserved exceptions.</summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(static completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private void UpdateIcon() { if (_window == IntPtr.Zero) return; var data = CreateData(); data.uFlags = NIF_TIP; data.szTip = _switching ? "SimRigMode — switching…" : $"SimRigMode — {(_mode == WorkspaceMode.Desktop ? "Desktop mode" : _mode == WorkspaceMode.SimRig ? "Sim rig mode" : "mode unknown")}"; Shell_NotifyIcon(NIM_MODIFY, ref data); }
    private NOTIFYICONDATA CreateData() => new() { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _window, uID = 1, uCallbackMessage = WM_TRAY, hIcon = _icon, szTip = "SimRigMode", szInfo = "", szInfoTitle = "" };
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NOTIFYICONDATA { public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip; public uint dwState, dwStateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo; public uint uTimeoutOrVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string? className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
