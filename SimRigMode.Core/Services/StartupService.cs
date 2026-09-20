using Microsoft.Win32;

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
        if (enabled) key.SetValue("SimRigMode", $"\"{Environment.ProcessPath}\" --startup"); else key.DeleteValue("SimRigMode", false);
    }
}
