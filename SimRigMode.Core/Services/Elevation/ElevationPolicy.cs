namespace SimRigMode.Services.Elevation;

/// <summary>Whether this process is permitted to request elevation at all.</summary>
public enum ElevationMode
{
    /// <summary>Elevation may be requested, subject to <see cref="ElevationGuard"/>.</summary>
    Normal,

    /// <summary>Elevation is permanently refused for the lifetime of this process.</summary>
    Disabled
}

/// <summary>
/// A one-way, process-wide latch on the ability to request elevation.
///
/// Once disabled it cannot be re-enabled. That is deliberate: the elevated child latches itself
/// off before it touches a single device, so a child can never spawn a grandchild no matter what
/// its payload says or how its code is later edited. Test hosts latch themselves off in a module
/// initializer, before any test code runs.
///
/// This is the in-process half of the recursion guard. It cannot be the whole story, because
/// ProcessStartInfo.Environment is unusable with UseShellExecute=true (which "runas" requires)
/// and ShellExecuteEx does not propagate the parent's environment block to the elevated child.
/// The cross-process half is <see cref="ElevationGuard"/>: the parent proves it is the production
/// binary before it launches anything, and the worker entry point always terminates the process.
/// </summary>
public static class ElevationPolicy
{
    private static readonly object Gate = new();
    private static ElevationMode _mode = ElevationMode.Normal;
    private static string? _disabledReason;

    public static ElevationMode Mode { get { lock (Gate) return _mode; } }

    public static string? DisabledReason { get { lock (Gate) return _disabledReason; } }

    /// <summary>Permanently refuse elevation for this process. Idempotent; the first reason wins.</summary>
    public static void Disable(string reason)
    {
        lock (Gate)
        {
            if (_mode == ElevationMode.Disabled) return;
            _mode = ElevationMode.Disabled;
            _disabledReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        }
    }

    /// <summary>
    /// Latches elevation off when this process was itself started as an elevated worker, or when
    /// a depth marker is present. Call once from the entry point, before anything else.
    /// </summary>
    public static void Initialize(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, ElevationGuard.ElevatedBatchArgument, StringComparison.Ordinal)))
            Disable("process was started as an elevated device worker");

        var depth = Environment.GetEnvironmentVariable(ElevationGuard.DepthEnvironmentVariable);
        if (int.TryParse(depth, out var value) && value >= 1)
            Disable($"elevation depth marker present ({value})");
    }
}
