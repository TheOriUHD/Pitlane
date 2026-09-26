using System.Text;
using System.Text.Json;

namespace SimRigMode.Services.Elevation;

/// <summary>Process exit codes returned by <see cref="ElevatedWorker"/>.</summary>
public static class ElevatedWorkerExitCode
{
    public const int Success = 0;
    public const int MalformedArguments = 10;
    public const int NotProductionImage = 11;
    public const int SafetyDenied = 20;
    public const int DeviceOperationFailed = 40;
    public const int InternalError = 50;

    /// <summary>
    /// Configuration Manager results are reported separately and never collapsed into the exit
    /// code. The previous implementation did <c>Environment.Exit(Math.Min(result, 255))</c>,
    /// which collides with every other meaning an exit code can carry.
    /// </summary>
    public static bool IsWorkerCode(int code) => code is Success or MalformedArguments
        or NotProductionImage or SafetyDenied or DeviceOperationFailed or InternalError;
}

/// <summary>
/// The elevated device worker.
///
/// <see cref="TryRun"/> must be the first thing the entry point does, before WinUI starts and
/// before any application state exists. Two reasons, both learned from the incident:
///
/// 1. A worker that returns an exit code cannot fall through into the tray app. The previous
///    implementation lived inside <c>App.OnLaunched</c> and only ever left via
///    <c>Environment.Exit</c>; its blanket <c>catch { Environment.Exit(1); }</c> was the only
///    thing preventing execution continuing into application startup. Any edit narrowing that
///    catch would have produced an invisible HIGH-INTEGRITY tray app that takes the singleton
///    mutex, at which point the user's normal app silently exits believing another instance is
///    running.
///
/// 2. A WinUI initialization failure must not abort a device batch, or report itself to the
///    parent as a Configuration Manager error.
///
/// The worker never constructs a window, a dispatcher, a message loop or a configuration store.
/// </summary>
public static class ElevatedWorker
{
    /// <summary>
    /// Returns <c>null</c> when these are not worker arguments, so normal startup continues.
    /// Otherwise returns the process exit code. Never throws.
    /// </summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], ElevationGuard.ElevatedBatchArgument, StringComparison.Ordinal))
            return null;

        // Latch elevation off before anything else. From here this process physically cannot
        // spawn another elevated process, whatever the payload contains.
        ElevationPolicy.Disable("elevated device worker");
        try { Environment.SetEnvironmentVariable(ElevationGuard.DepthEnvironmentVariable, "1"); }
        catch { /* Informational only; the latch above is what enforces this. */ }

        try
        {
            if (!ElevationGuard.IsProductionAppImage(out var reason))
                return Fail(ElevatedWorkerExitCode.NotProductionImage, "not the production image: " + reason);

            if (args.Length != 2)
                return Fail(ElevatedWorkerExitCode.MalformedArguments, $"expected 2 arguments, got {args.Length}");

            List<UsbDeviceService.DeviceOperation>? operations;
            try
            {
                var value = args[1].Replace('-', '+').Replace('_', '/');
                value += new string('=', (4 - value.Length % 4) % 4);
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                operations = JsonSerializer.Deserialize<List<UsbDeviceService.DeviceOperation>>(json);
            }
            catch (Exception ex)
            {
                return Fail(ElevatedWorkerExitCode.MalformedArguments, "payload could not be decoded: " + ex.Message);
            }

            if (operations is null || operations.Count == 0)
                return Fail(ElevatedWorkerExitCode.MalformedArguments, "payload contained no operations");

            // Validate the WHOLE batch before applying any of it. The previous implementation
            // applied operations one at a time and exited on the first failure, leaving earlier
            // devices changed with no record of which. Fail closed instead.
            var denials = operations
                .Select(operation => (operation, verdict: Validate(operation)))
                .Where(entry => entry.verdict is not null)
                .Select(entry => $"{entry.operation.InstanceId}: {entry.verdict}")
                .ToList();

            if (denials.Count > 0)
                return Fail(ElevatedWorkerExitCode.SafetyDenied,
                    "refused, nothing applied:" + Environment.NewLine + string.Join(Environment.NewLine, denials));

            foreach (var operation in operations)
            {
                var result = UsbDeviceService.PerformLocalOperation(operation.InstanceId, operation.Enable);
                if (result == 0) continue;
                return Fail(ElevatedWorkerExitCode.DeviceOperationFailed,
                    $"Configuration Manager result {result} for {operation.InstanceId} " +
                    $"({(operation.Enable ? "enable" : "disable")})");
            }

            return ElevatedWorkerExitCode.Success;
        }
        catch (Exception ex)
        {
            return Fail(ElevatedWorkerExitCode.InternalError, ex.ToString());
        }
    }

    /// <summary>
    /// Independent, fail-closed validation of a single operation, performed inside the elevated
    /// process. The caller's UI checks are not trusted here: the command line is user-visible
    /// and user-constructible, and the previous worker applied whatever it was handed.
    ///
    /// This is the minimal shape gate. The exhaustive version - devnode subtree walk for
    /// critical descendants, identity re-verification against expected VID/PID/container, and a
    /// machine-wide last-input census - lands with the full elevation protocol.
    /// </summary>
    /// <returns>A denial reason, or <c>null</c> when the operation is allowed.</returns>
    internal static string? Validate(UsbDeviceService.DeviceOperation operation)
    {
        var id = operation.InstanceId;

        if (string.IsNullOrWhiteSpace(id)) return "empty instance id";
        if (id.Length > 512) return "instance id is implausibly long";
        if (id.Any(char.IsControl)) return "instance id contains control characters";

        // Only USB devnodes are operable, in either direction. This single rule means a forged
        // or corrupted payload can never reach a PCI host controller, a PS/2 keyboard
        // (ACPI\PNP0303), a storage volume, or a root-enumerated device.
        if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
            return "not a USB devnode; only USB\\ instance ids may be operated";

        if (id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
            return "root hubs are protected";

        return null;
    }

    private static int Fail(int code, string detail)
    {
        try { Console.Error.WriteLine($"{Product.Name} elevated worker ({code}): {detail}"); }
        catch { /* No console when launched hidden via ShellExecute; the exit code carries. */ }
        return code;
    }
}
