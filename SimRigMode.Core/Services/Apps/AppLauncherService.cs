using SimRigMode.Models;

namespace SimRigMode.Services.Apps;

/// <summary>The seam over starting and ending processes, so the launcher can be tested without
/// touching a single real program.</summary>
public interface IProcessController
{
    bool FileExists(string path);

    /// <summary>Process ids of running copies of exactly this program file.</summary>
    IReadOnlyList<int> FindRunning(string programPath);

    void Start(LinkedApp app);

    /// <summary>Asks the process to close, as clicking its window's X would.</summary>
    void RequestClose(int processId);

    bool HasExited(int processId);

    /// <returns>Null when it was ended, otherwise why not.</returns>
    string? End(int processId);
}

public sealed record AppActionResult(string App, bool Success, string Detail);

/// <summary>Starts the apps linked to a mode and closes them again when it ends.</summary>
public sealed class AppLauncherService
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IProcessController _processes;
    private readonly TimeSpan _grace;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AppLauncherService(IProcessController processes, TimeSpan? gracePeriod = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _processes = processes;
        _grace = gracePeriod ?? DefaultGracePeriod;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    /// <summary>Starts each app that is not already running. A missing program is reported, not thrown.</summary>
    public IReadOnlyList<AppActionResult> Start(IEnumerable<LinkedApp> apps)
    {
        var results = new List<AppActionResult>();
        foreach (var app in apps)
        {
            if (!_processes.FileExists(app.Path))
            {
                results.Add(new(app.Name, false, $"{app.Name} was not found at {app.Path}."));
                continue;
            }
            if (_processes.FindRunning(app.Path).Count > 0)
            {
                results.Add(new(app.Name, true, $"{app.Name} was already running."));
                continue;
            }
            try
            {
                _processes.Start(app);
                results.Add(new(app.Name, true, $"{app.Name} started."));
            }
            catch (Exception ex)
            {
                results.Add(new(app.Name, false, $"{app.Name} could not be started: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>
    /// Asks every running copy of every app to close, all at once, then waits one grace period for
    /// all of them together. Whatever is still running afterwards is ended if its app allows it.
    /// </summary>
    public async Task<IReadOnlyList<AppActionResult>> CloseAsync(IEnumerable<LinkedApp> apps, CancellationToken token)
    {
        var results = new List<AppActionResult>();
        var running = new List<(LinkedApp App, IReadOnlyList<int> Processes)>();
        foreach (var app in apps)
        {
            var processes = _processes.FindRunning(app.Path);
            if (processes.Count == 0) results.Add(new(app.Name, true, $"{app.Name} was not running."));
            else running.Add((app, processes));
        }
        if (running.Count == 0) return results;

        foreach (var (_, processes) in running)
            foreach (var process in processes) _processes.RequestClose(process);

        var waited = TimeSpan.Zero;
        while (waited < _grace && running.Any(entry => entry.Processes.Any(process => !_processes.HasExited(process))))
        {
            await _delay(PollInterval, token);
            waited += PollInterval;
        }

        foreach (var (app, processes) in running)
        {
            var left = processes.Where(process => !_processes.HasExited(process)).ToList();
            if (left.Count == 0)
            {
                results.Add(new(app.Name, true, $"{app.Name} closed."));
                continue;
            }
            if (!app.ForceClose)
            {
                results.Add(new(app.Name, false,
                    $"{app.Name} did not close within {_grace.TotalSeconds:0} seconds; it may have gone to the tray. Turn on \"End it if it won't close\" for it."));
                continue;
            }
            var errors = left.Select(_processes.End).OfType<string>().ToList();
            results.Add(errors.Count == 0
                ? new(app.Name, true, $"{app.Name} did not close by itself, so it was ended.")
                : new(app.Name, false, $"{app.Name} could not be ended: {errors[0]}."));
        }
        return results;
    }
}
