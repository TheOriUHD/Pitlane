namespace SimRigMode.Services.Display;

/// <param name="MonitorDevicePath">The monitor that keeps dropping out.</param>
/// <param name="Dropouts">How many dropouts fell inside the detection window.</param>
/// <param name="Span">Time from the first to the last of those dropouts.</param>
public sealed record DisplayBounce(string MonitorDevicePath, int Dropouts, TimeSpan Span);

/// <summary>
/// Recognises a monitor that keeps disconnecting and reconnecting by itself.
///
/// This is what the 2026-09-13 and 2026-09-24 stutters actually were: the PG27AQDP, left
/// connected and powered but out of the layout, dropped off the bus every 7.6 s, and each return
/// stalled Windows' input and window handling for everything on the machine. Nothing reported
/// it, so it was blamed on the Prompter. See docs/report.md.
///
/// A single dropout is normal - a monitor switched off, a cable pulled, a mode switch. Three in
/// under a minute is not. Pure and thread-safe: the native watcher feeds it from the thread pool.
/// </summary>
public sealed class DisplayBounceDetector
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly TimeSpan _cooldown;
    private readonly Dictionary<string, List<DateTime>> _dropouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public DisplayBounceDetector(int threshold = 3, TimeSpan? window = null, TimeSpan? cooldown = null)
    {
        _threshold = Math.Max(2, threshold);
        _window = window ?? TimeSpan.FromSeconds(45);
        _cooldown = cooldown ?? TimeSpan.FromMinutes(10);
    }

    /// <returns>An alert the first time a monitor crosses the threshold, then nothing for the
    /// cooldown period, so a loop that keeps running does not flood the user.</returns>
    public DisplayBounce? RecordDropout(string monitorDevicePath, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;
        lock (_gate)
        {
            if (!_dropouts.TryGetValue(monitorDevicePath, out var times)) _dropouts[monitorDevicePath] = times = [];
            times.Add(utcNow);
            times.RemoveAll(time => utcNow - time > _window);
            if (times.Count < _threshold) return null;
            if (_lastAlert.TryGetValue(monitorDevicePath, out var last) && utcNow - last < _cooldown) return null;
            _lastAlert[monitorDevicePath] = utcNow;
            return new(monitorDevicePath, times.Count, utcNow - times.Min());
        }
    }
}
