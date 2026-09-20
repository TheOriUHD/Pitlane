using Microsoft.Win32;
using SimRigMode.Services.Elevation;

namespace SimRigMode.Services;

public sealed class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunPath); return key?.GetValue("SimRigMode") is string; }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        // VerifiedSelfPath, not Environment.ProcessPath: run from a test host the latter would
        // register the TEST binary to launch at every sign-in. Same hazard as the elevation path.
        if (enabled) key.SetValue("SimRigMode", $"\"{ElevationGuard.VerifiedSelfPath}\" --startup"); else key.DeleteValue("SimRigMode", false);
    }
}
