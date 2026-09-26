using Microsoft.Win32;
using SimRigMode.Services.Elevation;

namespace SimRigMode.Services;

public sealed class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = Product.Name;

    /// <summary>The Run value builds from before the rename wrote. Removed whenever the setting
    /// is changed, so an old entry cannot start a stale SimRigMode.exe next to Pitlane.</summary>
    private const string LegacyValueName = "SimRigMode";

    /// <summary>
    /// True only when the Run entry exists, points at THIS executable, and has not been disabled
    /// from Task Manager's Startup tab. Previously this returned true whenever any value existed,
    /// so the tray checkmark lied after the app was moved or updated, and again whenever the user
    /// had turned it off in Task Manager - which leaves the Run value in place.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            if (key?.GetValue(ValueName) is not string command) return false;
            if (!PointsAtThisExecutable(command)) return false;
            return !IsDisabledByStartupApproved();
        }
    }

    /// <summary>The Run value exists but names a different executable - typically a stale entry
    /// left behind by moving or updating the app.</summary>
    public bool HasStaleEntry
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue(ValueName) is string command && !PointsAtThisExecutable(command);
        }
    }

    private static bool PointsAtThisExecutable(string command)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        return command.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisabledByStartupApproved()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        // First byte 2 or 3 means the user disabled it from Task Manager. Anything else, or a
        // missing value, means enabled.
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 0xFE) == 2;
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        // VerifiedSelfPath, not Environment.ProcessPath: run from a test host the latter would
        // register the TEST binary to launch at every sign-in. Same hazard as the elevation path.
        if (enabled) key.SetValue(ValueName, $"\"{ElevationGuard.VerifiedSelfPath}\" --startup"); else key.DeleteValue(ValueName, false);
        key.DeleteValue(LegacyValueName, false);
    }
}
