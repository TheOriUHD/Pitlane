using System.Diagnostics;
using System.Runtime.InteropServices;
using static SimRigMode.NativeInterop.WindowNative;

namespace SimRigMode.Services.Display;

/// <summary>A rectangle on the desktop, in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Moves the windows on a parked monitor to the main screen, which is what Windows does for a
/// monitor that is detached. <see cref="ParkedScreenKeeper"/> calls it while a monitor is parked.
///
/// It never activates another program's window and never waits on one: every request is queued
/// to the window's own thread, so a program that is not responding cannot hold anything up.
/// </summary>
public static class ParkedWindowMover
{
    /// <summary>The desktop, the taskbars and the shell's own popups belong to the monitor.</summary>
    private static readonly string[] ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "tooltips_class32", "#32768"
    ];

    /// <returns>What was moved and what could not be, for the log.</returns>
    public static IReadOnlyList<string> MoveOff(IReadOnlyList<ParkedScreen> screens)
    {
        var log = new List<string>();
        if (screens.Count == 0) return log;
        var primary = MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
        if (!TryGetMonitor(primary, out var primaryBounds, out var primaryWork))
        {
            log.Add("the main screen could not be found, so no windows were moved");
            return log;
        }

        foreach (var screen in screens)
        {
            // Only the monitor that is exactly where the parked one was said to be. After a layout
            // change that may be another monitor, one in use.
            var monitor = MonitorFromPoint(new POINT(screen.X + screen.Width / 2, screen.Y + screen.Height / 2), MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero || monitor == primary || !TryGetMonitor(monitor, out var bounds, out var work) || bounds != screen.Bounds) continue;

            var moved = new List<string>();
            var stuck = new List<string>();
            foreach (var window in WindowsOn(monitor))
                (Move(window, bounds, work, primaryBounds, primaryWork) ? moved : stuck).Add(ProcessName(window));

            if (moved.Count > 0)
                log.Add($"moved {Windows(moved.Count)} off {screen.FriendlyName} to the main screen ({string.Join(", ", moved.Distinct())})");
            if (stuck.Count > 0)
                log.Add($"could not move {Windows(stuck.Count)} off {screen.FriendlyName} ({string.Join(", ", stuck.Distinct())}); " +
                        "a program running as administrator cannot be moved by another program");
        }
        return log;
    }

    /// <summary>
    /// Where a window on a parked screen goes: the same place, relatively, in the main screen's
    /// work area. One at the right edge stays at the right edge, one in the middle stays in the
    /// middle. It is never larger than the work area and never over its edge.
    /// </summary>
    public static PixelRect Rehome(PixelRect window, PixelRect screen, PixelRect workArea)
    {
        var width = Math.Clamp(window.Width, 1, Math.Max(1, workArea.Width));
        var height = Math.Clamp(window.Height, 1, Math.Max(1, workArea.Height));
        return new(
            workArea.X + Place(window.X - screen.X, screen.Width - window.Width, workArea.Width - width),
            workArea.Y + Place(window.Y - screen.Y, screen.Height - window.Height, workArea.Height - height),
            width, height);
    }

    /// <summary>The same share of the free room as before. No room before: centred.</summary>
    private static int Place(int offset, int room, int newRoom) =>
        room > 0 ? (int)Math.Round(Math.Clamp(offset / (double)room, 0, 1) * newRoom) : Math.Max(0, newRoom / 2);

    /// <summary>
    /// The user's windows on the monitor: visible, including minimized ones and ones on other
    /// virtual desktops. Not the shell's, not click-through overlays, and not tool windows,
    /// which is also what Pitlane's own flyout and cover are. Pitlane's full window is moved.
    /// </summary>
    private static List<IntPtr> WindowsOn(IntPtr monitor)
    {
        var found = new List<IntPtr>();
        var name = new char[64];
        EnumWindowsProc callback = (window, _) =>
        {
            if (IsWindowVisible(window) && MonitorFromWindow(window, MONITOR_DEFAULTTONULL) == monitor && IsTheUsersWindow(window, name)) found.Add(window);
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool IsTheUsersWindow(IntPtr window, char[] name)
    {
        if (!IsWindowVisible(window)) return false;
        var exStyle = (ulong)GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TRANSPARENT) != 0) return false;
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        var length = GetClassName(window, name, name.Length);
        if (length > 0 && ShellClasses.Contains(new string(name, 0, length))) return false;
        // Cloaked by the shell alone means another virtual desktop: still the user's window.
        // Cloaked by its app, or along with its owner, it is nothing the user can see.
        if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
            (cloaked & (DWM_CLOAKED_APP | DWM_CLOAKED_INHERITED)) != 0) return false;
        return IsIconic(window) || (GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top);
    }

    private static bool Move(IntPtr window, PixelRect from, PixelRect fromWork, PixelRect toBounds, PixelRect toWork)
    {
        const uint quietly = SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        if (!IsIconic(window) && !IsZoomed(window))
        {
            if (!GetWindowRect(window, out var rect)) return false;
            var target = Rehome(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), from, toWork);
            return SetWindowPos(window, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, quietly);
        }

        // Minimized or maximized: move the place it restores to. Those are workspace coordinates,
        // offset by the monitor's taskbar, except for a tool window.
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(window, ref placement)) return false;
        var tool = ((ulong)GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0;
        var (fromX, fromY) = tool ? (0, 0) : (fromWork.X - from.X, fromWork.Y - from.Y);
        var (toX, toY) = tool ? (0, 0) : (toWork.X - toBounds.X, toWork.Y - toBounds.Y);
        var normal = placement.rcNormalPosition;
        var restoreTo = Rehome(new(normal.Left + fromX, normal.Top + fromY, normal.Right - normal.Left, normal.Bottom - normal.Top), from, toWork);
        placement.rcNormalPosition = new RECT
        {
            Left = restoreTo.X - toX, Top = restoreTo.Y - toY,
            Right = restoreTo.X - toX + restoreTo.Width, Bottom = restoreTo.Y - toY + restoreTo.Height
        };
        placement.flags |= WPF_ASYNCWINDOWPLACEMENT;

        if (IsIconic(window))
        {
            placement.showCmd = SW_SHOWMINNOACTIVE;   // stays minimized; restores on the main screen
            return SetWindowPlacement(window, ref placement);
        }
        // Maximized: onto the main screen, then maximized there again, which also moves where it
        // restores to. Maximizing it in place would leave it on the parked monitor. Windows may
        // handle the two requests in either order; both orders end the same.
        var onto = SetWindowPos(window, IntPtr.Zero, toWork.X, toWork.Y, toWork.Width, toWork.Height, quietly);
        placement.showCmd = SW_SHOWMAXIMIZED;
        return SetWindowPlacement(window, ref placement) && onto;
    }

    private static bool TryGetMonitor(IntPtr monitor, out PixelRect bounds, out PixelRect work)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var found = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
        bounds = Of(info.rcMonitor);
        work = Of(info.rcWork);
        return found && work.Width > 0 && work.Height > 0;
    }

    private static PixelRect Of(RECT rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static string ProcessName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch { return "a program that has exited"; }
    }

    private static string Windows(int count) => count == 1 ? "1 window" : $"{count} windows";
}
