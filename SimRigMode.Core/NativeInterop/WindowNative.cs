using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

/// <summary>
/// Top-level windows and monitors: finding the windows on a monitor, moving them, and the black
/// cover on a parked monitor. Declarations only.
/// </summary>
internal static class WindowNative
{
    internal const int GWL_EXSTYLE = -20;
    internal const uint WS_POPUP = 0x80000000, WS_OVERLAPPED = 0x00000000;
    internal const uint WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000, WS_EX_NOACTIVATE = 0x08000000;
    internal const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;
    internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
    internal const uint SW_HIDE = 0, SW_SHOWMAXIMIZED = 3, SW_SHOWMINNOACTIVE = 7;
    internal const uint WPF_ASYNCWINDOWPLACEMENT = 0x0004;
    internal const uint WM_CLOSE = 0x0010, WM_MOUSEACTIVATE = 0x0021, WM_WINDOWPOSCHANGING = 0x0046, WM_DISPLAYCHANGE = 0x007E,
        WM_TIMER = 0x0113, WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const int MA_NOACTIVATE = 3;
    internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    internal const int DWM_CLOAKED_APP = 1, DWM_CLOAKED_INHERITED = 4;
    internal const int BLACK_BRUSH = 4;
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] internal struct WINDOWPLACEMENT { public uint length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential)] internal struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; }

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    internal delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal delegate void WinEventProcedure(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, [Out] char[] name, int capacity);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClass(ref WNDCLASS windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern int GetMessage(out MSG message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] internal static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProcedure callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? moduleName);
}
