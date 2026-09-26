using System.ComponentModel;
using System.Diagnostics;
using SimRigMode.Models;
using SimRigMode.NativeInterop;

namespace SimRigMode.Services.Apps;

/// <summary>
/// The real process controller. Starts programs the way double-clicking them would, so an app
/// that needs administrator rights shows its own UAC prompt; SimRigMode never elevates anything
/// here. Never constructed by a test (NoHardwareAccessFromTestsTests forbids it).
/// </summary>
public sealed class SystemProcessController : IProcessController
{
    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<int> FindRunning(string programPath)
    {
        var found = new List<int>();
        string wanted;
        try { wanted = Path.GetFullPath(programPath); } catch { return found; }

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(programPath)))
        {
            using (process)
            {
                var path = ProcessNative.TryGetImagePath(process.Id);
                if (path is not null && string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) found.Add(process.Id);
            }
        }
        return found;
    }

    public void Start(LinkedApp app)
    {
        var info = new ProcessStartInfo(app.Path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(app.Path) ?? "",
            Arguments = app.Arguments ?? ""
        };
        Process.Start(info)?.Dispose();
    }

    public void RequestClose(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.CloseMainWindow();
        }
        catch { /* gone already, or not ours to ask */ }
    }

    public bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }   // no such process any more
        catch { return false; }                       // exists, but we may not look at it
    }

    public string? End(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (ArgumentException) { return null; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return $"it runs as administrator, so {Product.Name} is not allowed to end it";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
