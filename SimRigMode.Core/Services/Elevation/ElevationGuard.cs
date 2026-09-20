using System.Reflection;

namespace SimRigMode.Services.Elevation;

/// <summary>Thrown when a process that is not the production application tries to elevate.</summary>
public sealed class ElevationRefusedException : InvalidOperationException
{
    public ElevationRefusedException(string reason)
        : base("SimRigMode refused to request elevation: " + reason) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Proves that this process really is the production SimRigMode application before it is allowed
/// to relaunch itself with the "runas" verb, and before anything registers it to start with
/// Windows.
///
/// This is the check that makes the 2026-09-13 incident structurally impossible.
/// <c>UsbDeviceService.ApplyBatchElevatedAsync</c> relaunched <c>Environment.ProcessPath</c>
/// with <c>Verb = "runas"</c>. Under the smoke-test host that path was
/// <c>SimRigMode.SmokeTests.exe</c>, whose entry point ignored the worker argument and simply
/// re-ran the whole suite - which reached the same call again, unboundedly. See docs/report.md.
///
/// Under xUnit the entry assembly is <c>testhost</c>; under the old smoke test it was
/// <c>SimRigMode.SmokeTests</c>. Neither can satisfy <see cref="IsProductionAppImage"/>, so
/// neither can elevate, whatever its code does.
/// </summary>
public static class ElevationGuard
{
    /// <summary>Command-line switch that marks a process as the elevated device worker.</summary>
    public const string ElevatedBatchArgument = "--elevated-device-batch";

    /// <summary>
    /// In-process depth marker. Note this is NOT inherited by the elevated child:
    /// ShellExecuteEx does not propagate the parent's environment block, and
    /// ProcessStartInfo.Environment cannot be used with UseShellExecute=true at all.
    /// Depth therefore travels on the command line; this variable is a local latch and a
    /// test-harness switch only.
    /// </summary>
    public const string DepthEnvironmentVariable = "SIMRIGMODE_ELEVATION_DEPTH";

    /// <summary>Assembly metadata key stamped into the production app by SimRigMode.csproj.</summary>
    public const string ProductIdentityKey = "SimRigMode.ProductIdentity";

    /// <summary>Expected metadata value. A rename cannot forge this; it is compiled in.</summary>
    public const string ProductIdentityValue = "SimRigMode/elevation-protocol-1";

    private const string ProductAssemblyName = "SimRigMode";
    private const string ProductProcessFileName = "SimRigMode.exe";

    /// <summary>
    /// True when this process is the production application image. <paramref name="reason"/>
    /// explains the first failed check, for logging and for the worker's exit diagnostics.
    /// </summary>
    public static bool IsProductionAppImage(out string reason)
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null)
        {
            reason = "no entry assembly (hosted or unmanaged startup)";
            return false;
        }

        var entryName = entry.GetName().Name;
        if (!string.Equals(entryName, ProductAssemblyName, StringComparison.Ordinal))
        {
            reason = $"entry assembly is '{entryName}', not '{ProductAssemblyName}'";
            return false;
        }

        var stamped = entry.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => string.Equals(attribute.Key, ProductIdentityKey, StringComparison.Ordinal)
                           && string.Equals(attribute.Value, ProductIdentityValue, StringComparison.Ordinal));
        if (!stamped)
        {
            reason = $"entry assembly is not stamped with {ProductIdentityKey}={ProductIdentityValue}";
            return false;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Environment.ProcessPath is unavailable";
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (!string.Equals(fileName, ProductProcessFileName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"process image is '{fileName}', not '{ProductProcessFileName}'";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// The executable to relaunch for elevation, or to register for startup. Never use
    /// <see cref="Environment.ProcessPath"/> directly for either purpose: under a test host it
    /// points at the test binary.
    /// </summary>
    /// <exception cref="ElevationRefusedException">This process is not the production app.</exception>
    public static string VerifiedSelfPath
    {
        get
        {
            if (!IsProductionAppImage(out var reason)) throw new ElevationRefusedException(reason);
            return Environment.ProcessPath!;
        }
    }

    /// <summary>
    /// Fails closed unless this process may request elevation right now.
    /// </summary>
    /// <exception cref="ElevationRefusedException">Elevation is not permitted.</exception>
    public static void AssertMayElevate()
    {
        if (ElevationPolicy.Mode == ElevationMode.Disabled)
            throw new ElevationRefusedException(
                "elevation is disabled for this process (" + (ElevationPolicy.DisabledReason ?? "no reason recorded") + ")");

        if (!IsProductionAppImage(out var reason))
            throw new ElevationRefusedException(reason);

        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, ElevatedBatchArgument, StringComparison.Ordinal)))
            throw new ElevationRefusedException("this process is already an elevated worker");

        var depth = Environment.GetEnvironmentVariable(DepthEnvironmentVariable);
        if (int.TryParse(depth, out var value) && value >= 1)
            throw new ElevationRefusedException($"elevation depth marker present ({value})");
    }
}
