using SimRigMode.Models;
using SimRigMode.Services.Apps;

namespace SimRigMode.Tests.Fakes;

/// <summary>
/// In-memory processes. Nothing is ever started or ended for real: the launcher is tested
/// entirely against this.
/// </summary>
internal sealed class FakeProcessController : IProcessController
{
    private int _nextId = 1000;

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process id to program path.</summary>
    public Dictionary<int, string> Running { get; } = [];

    /// <summary>Processes that ignore a close request, like an app that hides in the tray.</summary>
    public HashSet<int> IgnoresClose { get; } = [];

    /// <summary>Processes that cannot be ended, like one running as administrator.</summary>
    public HashSet<int> Protected { get; } = [];

    public List<string> Started { get; } = [];
    public List<int> CloseRequests { get; } = [];
    public List<int> Ended { get; } = [];

    public int Run(string path)
    {
        var id = _nextId++;
        Running[id] = path;
        return id;
    }

    public bool FileExists(string path) => Files.Contains(path);

    public IReadOnlyList<int> FindRunning(string programPath) =>
        Running.Where(entry => entry.Value.Equals(programPath, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToList();

    public void Start(LinkedApp app)
    {
        Started.Add(app.Path);
        Run(app.Path);
    }

    public void RequestClose(int processId)
    {
        CloseRequests.Add(processId);
        if (!IgnoresClose.Contains(processId)) Running.Remove(processId);
    }

    public bool HasExited(int processId) => !Running.ContainsKey(processId);

    public string? End(int processId)
    {
        if (Protected.Contains(processId)) return $"it runs as administrator, so {Product.Name} is not allowed to end it";
        Ended.Add(processId);
        Running.Remove(processId);
        return null;
    }
}
