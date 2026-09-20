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
    private IntPtr _window;
    private int _nextId = 200;

    public HotkeyService()
    {
        _wndProc = WndProc;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "SimRigMode hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); _ready.Wait();
    }
    public bool Register(string text, Action action)
    {
        if (!TryParse(text, out var modifiers, out var key)) return false;
        var id = Interlocked.Increment(ref _nextId);
        if (!RegisterHotKey(_window, id, modifiers | 0x4000, key)) return false;
        _actions[id] = action; return true;
    }
    public void Clear() { foreach (var id in _actions.Keys) UnregisterHotKey(_window, id); _actions.Clear(); }
    public void Dispose() { Clear(); if (_window != IntPtr.Zero) PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); _ready.Dispose(); }
    private void MessageLoop()
    {
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), lpszClassName = "SimRigModeHotkeyWindow", hInstance = GetModuleHandle(null) };
        RegisterClass(ref wc); _window = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero); _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        if (_window != IntPtr.Zero) DestroyWindow(_window); _window = IntPtr.Zero;
    }
    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action)) action();
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string? className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
