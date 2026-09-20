using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SimRigMode.Services;

public sealed class HotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312, WM_CLOSE = 0x0010;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentDictionary<int, Action> _actions = new();
    private readonly WndProcDelegate _wndProc;
    // Unique per instance. A fixed class name meant that reconfiguring hotkeys - which disposes
    // and recreates this service - hit ERROR_CLASS_ALREADY_EXISTS on RegisterClass. The return
    // value was discarded, CreateWindowEx still succeeded, and the class kept pointing at the
    // PREVIOUS instance's delegate thunk. Hotkeys silently stopped working, and once that
    // instance was collected the next WM_HOTKEY jumped into freed memory.
    private readonly string _className = "SimRigModeHotkeyWindow_" + Guid.NewGuid().ToString("N");
    private IntPtr _window;
    private int _nextId = 200;
    private volatile bool _disposed;

    /// <summary>Win32 error from the last failed RegisterHotKey, so callers can say why.</summary>
    public int LastRegisterError { get; private set; }

    public HotkeyService()
    {
        _wndProc = WndProc;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "SimRigMode hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); _ready.Wait();
    }
    public bool Register(string text, Action action)
    {
        LastRegisterError = 0;
        if (_disposed || _window == IntPtr.Zero) return false;
        if (!TryParse(text, out var modifiers, out var key)) return false;
        var id = Interlocked.Increment(ref _nextId);
        if (!RegisterHotKey(_window, id, modifiers | 0x4000, key))
        {
            // ERROR_HOTKEY_ALREADY_REGISTERED (1409) is the common case and was previously
            // unreachable, because the import did not set SetLastError.
            LastRegisterError = Marshal.GetLastWin32Error();
            return false;
        }
        _actions[id] = action; return true;
    }

    /// <summary>Describes the last registration failure, for the UI.</summary>
    public string DescribeLastRegisterError() => LastRegisterError switch
    {
        0 => "The shortcut text could not be understood.",
        1409 => "Another application has already registered this shortcut.",
        _ => $"Windows refused the shortcut (error {LastRegisterError})."
    };

    public void Clear() { foreach (var id in _actions.Keys) UnregisterHotKey(_window, id); _actions.Clear(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        if (_window != IntPtr.Zero) PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        // Join before returning. Without this, teardown raced the construction of the
        // replacement instance, which is what made the class-name collision reproducible.
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _ready.Dispose();
    }

    private void MessageLoop()
    {
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), lpszClassName = _className, hInstance = GetModuleHandle(null) };
        if (RegisterClass(ref wc) == 0) { _ready.Set(); return; }
        _window = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        _window = IntPtr.Zero;
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            // This runs inside a native callback frame. An exception escaping here does not
            // reach the WinUI unhandled-exception handler - it terminates the process.
            if (_actions.TryGetValue(wParam.ToInt32(), out var action))
                try { action(); } catch { /* the caller's own handler owns reporting */ }
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
    private static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 2;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 1;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 4;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 8;
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
            else if (part.StartsWith('F') && uint.TryParse(part.AsSpan(1), out var f) && f is >= 1 and <= 24) key = 0x6F + f;
            else return false;
        }
        return key != 0;
    }
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string? className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
