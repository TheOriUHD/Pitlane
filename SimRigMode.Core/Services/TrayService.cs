using System.Runtime.InteropServices;
using SimRigMode.Models;

namespace SimRigMode.Services;

public enum TrayBalloonIcon { Info = 1, Warning = 2, Error = 3 }

/// <summary>Where the tray icon is on screen, in physical pixels. When Windows cannot say, it is
/// the point that was clicked.</summary>
public readonly record struct TrayAnchor(int Left, int Top, int Right, int Bottom);

public sealed class TrayService : IDisposable
{
    private const uint WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_CLOSE = 0x0010, WM_CONTEXTMENU = 0x007B, WM_NULL = 0x0000, WM_USER = 0x0400;
    private const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8, MF_GRAYED = 1, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x0002;
    private const int OpenId = 1001, DesktopId = 1002, RigId = 1003, StartupId = 1004, ExitId = 1005;
    private const uint WS_OVERLAPPED = 0x00000000, IMAGE_ICON = 1;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    private readonly Thread _thread; private readonly ManualResetEventSlim _ready = new(); private readonly WndProcDelegate _wndProc;
    private readonly IReadOnlyDictionary<WorkspaceMode, byte[]> _iconFiles;
    private readonly Dictionary<WorkspaceMode, IntPtr> _modeIcons = [];
    private IntPtr _window, _appIcon; private uint _taskbarCreated; private volatile bool _startup, _switching; private volatile WorkspaceMode _mode;
    // Unique per instance, with a matching UnregisterClass on teardown. A fixed name leaves the
    // class registered against a dead instance's delegate thunk.
    private readonly string _className = "PitlaneTrayWindow_" + Guid.NewGuid().ToString("N");
    private volatile bool _disposed;

    /// <summary>Left click, or Enter on the icon: the flyout.</summary>
    public event Action<TrayAnchor>? Invoked;
    /// <summary>"Open Pitlane" in the menu: the full app.</summary>
    public event Action? OpenRequested;
    public event Func<WorkspaceMode, Task>? SwitchRequested; public event Action<bool>? StartupChanged; public event Action? ExitRequested;

    /// <param name="startup">Whether "Start with Windows" is ticked.</param>
    /// <param name="modeIcons">.ico files for the tray, one per mode. Without one, the icon
    /// embedded in the executable is shown.</param>
    public TrayService(bool startup, IReadOnlyDictionary<WorkspaceMode, byte[]>? modeIcons = null)
    {
        _startup = startup; _iconFiles = modeIcons ?? new Dictionary<WorkspaceMode, byte[]>(); _wndProc = WndProc;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = Product.Name + " tray" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); _ready.Wait();
    }

    public void SetState(WorkspaceMode mode, bool switching)
    {
        _mode = mode; _switching = switching;
        if (_window == IntPtr.Zero) return;
        var data = CreateData(); data.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void SetStartup(bool enabled) => _startup = enabled;

    public void ShowBalloon(string title, string message, TrayBalloonIcon icon = TrayBalloonIcon.Info) { var data = CreateData(); data.uFlags = NIF_INFO; data.szInfoTitle = title; data.szInfo = message; data.dwInfoFlags = (uint)icon; Shell_NotifyIcon(NIM_MODIFY, ref data); }

    /// <summary>The icon's rectangle on screen. False while the shell cannot say, for example
    /// while the icon is hidden in the overflow area and that area is closed.</summary>
    public bool TryGetIconRect(out TrayAnchor anchor)
    {
        anchor = default;
        if (_window == IntPtr.Zero) return false;
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _window, uID = 1 };
        if (Shell_NotifyIconGetRect(ref id, out var rect) != 0 || rect.Right <= rect.Left) return false;
        anchor = new(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

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
        _window = CreateWindowEx(0, _className, Product.Name, WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        LoadIcons();

        // NIM_ADD commonly fails when the app starts before the shell is ready, which is exactly
        // the "start with Windows" path. Retry briefly rather than silently having no icon.
        for (var attempt = 0; attempt < 10 && !AddIcon(); attempt++) Thread.Sleep(500);
        _ready.Set();

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }

        var data = CreateData(); Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        _window = IntPtr.Zero;
        foreach (var icon in _modeIcons.Values) DestroyIcon(icon);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    /// <summary>
    /// Adds the icon and opts into NOTIFYICON_VERSION_4: clicks then arrive as NIN_SELECT,
    /// NIN_KEYSELECT and WM_CONTEXTMENU, with the position they belong to, instead of raw mouse
    /// messages - so a double click is not two opens, and the keyboard works too.
    /// </summary>
    private bool AddIcon()
    {
        var data = CreateData(); data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        return true;
    }

    private void LoadIcons()
    {
        var dpi = GetDpiForWindow(_window);
        var cx = dpi > 0 ? GetSystemMetricsForDpi(SM_CXSMICON, dpi) : GetSystemMetrics(SM_CXSMICON);
        var cy = dpi > 0 ? GetSystemMetricsForDpi(SM_CYSMICON, dpi) : GetSystemMetrics(SM_CYSMICON);
        _appIcon = LoadAppIcon(cx, cy);
        foreach (var (mode, file) in _iconFiles)
        {
            var icon = IconFromIco(file, cx, cy);
            if (icon != IntPtr.Zero) _modeIcons[mode] = icon;
        }
    }

    /// <summary>
    /// The application icon from this executable's own resources, at the shell's small-icon size.
    /// An earlier version asked the EXE for resource id 32512, which is the SYSTEM IDI_APPLICATION
    /// id and is only valid with a null instance handle - so it returned NULL and fell back to the
    /// generic Windows icon.
    /// </summary>
    private static IntPtr LoadAppIcon(int cx, int cy)
    {
        var module = GetModuleHandle(null);
        foreach (var id in new[] { 32512, 1, 2 })
        {
            var handle = LoadImage(module, new IntPtr(id), IMAGE_ICON, cx, cy, 0);
            if (handle != IntPtr.Zero) return handle;
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    /// <summary>The image in an .ico file that best fits <paramref name="cx"/>: the smallest one
    /// at least that large, else the largest. Entries may be PNG, which Windows reads directly.</summary>
    private static IntPtr IconFromIco(byte[] file, int cx, int cy)
    {
        try
        {
            if (file.Length < 6 || BitConverter.ToUInt16(file, 2) != 1) return IntPtr.Zero;
            var count = BitConverter.ToUInt16(file, 4);
            var entries = Enumerable.Range(0, count).Select(i =>
            {
                var at = 6 + 16 * i;
                var width = file[at] == 0 ? 256 : file[at];
                return (width, size: BitConverter.ToInt32(file, at + 8), offset: BitConverter.ToInt32(file, at + 12));
            }).Where(entry => entry.offset > 0 && entry.offset + entry.size <= file.Length).ToList();
            if (entries.Count == 0) return IntPtr.Zero;

            var best = entries.Where(entry => entry.width >= cx).OrderBy(entry => entry.width).FirstOrDefault();
            if (best.size == 0) best = entries.OrderByDescending(entry => entry.width).First();
            var image = file.AsSpan(best.offset, best.size).ToArray();
            return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, cx, cy, 0);
        }
        catch { return IntPtr.Zero; }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0) { AddIcon(); return IntPtr.Zero; }
        if (msg == WM_TRAY)
        {
            // Inside a native callback frame: an escaping exception terminates the process and is
            // invisible to the WinUI unhandled-exception handler.
            try
            {
                var kind = (uint)(lParam.ToInt64() & 0xFFFF);
                var x = (short)(wParam.ToInt64() & 0xFFFF);
                var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (kind is NIN_SELECT or NIN_KEYSELECT)
                    Invoked?.Invoke(TryGetIconRect(out var rect) ? rect : new TrayAnchor(x, y, x + 1, y + 1));
                else if (kind == WM_CONTEXTMENU) ShowMenu(x, y);
            }
            catch { /* the handlers own their own reporting */ }
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, OpenId, "Open " + Product.Name);
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | (_switching ? MF_GRAYED : 0) | (_mode == WorkspaceMode.Desktop ? MF_CHECKED : 0), DesktopId, "Desktop mode");
        AppendMenu(menu, MF_STRING | (_switching ? MF_GRAYED : 0) | (_mode == WorkspaceMode.SimRig ? MF_CHECKED : 0), RigId, "Sim Rig mode");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | (_startup ? MF_CHECKED : 0), StartupId, "Start with Windows");
        AppendMenu(menu, MF_STRING, ExitId, "Quit " + Product.Name);
        // The documented notification-area pattern: foreground the owner first so the menu takes
        // focus, and post a dummy message afterwards so it dismisses when the user clicks away.
        SetForegroundWindow(_window);
        var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, 0, _window, IntPtr.Zero);
        PostMessage(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        Execute((int)command);
    }

    private void Execute(int id)
    {
        if (id == DesktopId && !_switching && _mode != WorkspaceMode.Desktop) Observe(SwitchRequested?.Invoke(WorkspaceMode.Desktop));
        else if (id == RigId && !_switching && _mode != WorkspaceMode.SimRig) Observe(SwitchRequested?.Invoke(WorkspaceMode.SimRig));
        else if (id == OpenId) OpenRequested?.Invoke();
        else if (id == StartupId) { _startup = !_startup; StartupChanged?.Invoke(_startup); }
        else if (id == ExitId) ExitRequested?.Invoke();
    }

    /// <summary>Keeps a fire-and-forget task's faults from becoming unobserved exceptions.</summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(static completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private string Tip => (_switching
        ? $"{Product.Name} · switching…"
        : $"{Product.Name} · {(_mode == WorkspaceMode.Desktop ? "Desktop" : _mode == WorkspaceMode.SimRig ? "Sim Rig" : "no mode yet")}")
        + (Product.IsStandbyTest ? " · standby test" : "");

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _window, uID = 1, uCallbackMessage = WM_TRAY,
        hIcon = _modeIcons.TryGetValue(_mode, out var icon) ? icon : _appIcon, szTip = Tip, szInfo = "", szInfoTitle = ""
    };

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NOTIFYICONDATA { public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip; public uint dwState, dwStateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo; public uint uTimeoutOrVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
