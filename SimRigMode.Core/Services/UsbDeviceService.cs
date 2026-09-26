using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SimRigMode.Models;
using SimRigMode.Services.Elevation;
using N = SimRigMode.NativeInterop.SetupApiNative;

namespace SimRigMode.Services;

public sealed class UsbDeviceService
{
    public sealed record DeviceOperation(string InstanceId, bool Enable);
    public IReadOnlyList<UsbDeviceInfo> Enumerate()
    {
        var result = new List<UsbDeviceInfo>();
        var set = N.SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, N.DIGCF_PRESENT | N.DIGCF_ALLCLASSES);
        if (set == N.InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new N.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<N.SP_DEVINFO_DATA>() };
                if (!N.SetupDiEnumDeviceInfo(set, i, ref data)) { if (Marshal.GetLastWin32Error() == 259) break; continue; }
                var id = GetId(set, ref data);
                var cls = GetProperty(set, ref data, N.SPDRP_CLASS); var service = GetProperty(set, ref data, N.SPDRP_SERVICE);
                var hostController = cls.Equals("USB", StringComparison.OrdinalIgnoreCase) && (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || service.Contains("xHCI", StringComparison.OrdinalIgnoreCase));
                if (!(id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || hostController)) continue;
                N.CM_Get_DevNode_Status(out var status, out _, data.DevInst, 0);
                var name = GetProperty(set, ref data, N.SPDRP_FRIENDLYNAME); if (string.IsNullOrWhiteSpace(name)) name = GetProperty(set, ref data, N.SPDRP_DEVICEDESC);
                var busName = GetDevicePropertyString(set, ref data, new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4));
                var (vid, pid) = ParseVidPidParts(id); var location = FirstMultiString(GetProperty(set, ref data, N.SPDRP_LOCATION_PATHS));
                var parentId = ""; if (N.CM_Get_Parent(out var parent, data.DevInst, 0) == 0) parentId = GetDeviceId(parent);
                var rootHub = id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase); var hub = rootHub || service.Contains("USBHUB", StringComparison.OrdinalIgnoreCase) || name.Contains("USB Hub", StringComparison.OrdinalIgnoreCase);
                result.Add(new() { DevInst = data.DevInst, InstanceId = id, ParentInstanceId = parentId, FriendlyName = name, ProductName = string.IsNullOrWhiteSpace(busName) ? name : busName, Manufacturer = GetProperty(set, ref data, N.SPDRP_MFG), DeviceClass = cls, Service = service, DriverKey = GetProperty(set, ref data, N.SPDRP_DRIVER), LocationInfo = GetProperty(set, ref data, N.SPDRP_LOCATION_INFORMATION), LocationPath = location, ContainerId = GetDevicePropertyGuid(set, ref data, new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2)), Vid = vid, Pid = pid, VidPid = ParseVidPid(id), Serial = ParseSerial(id), IsEnabled = (status & N.DN_STARTED) != 0, IsPresent = (status & N.DN_PRESENT) != 0, IsHub = hub, IsRootHub = rootHub, IsHostController = hostController, IsComposite = service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase), IsSelfCritical = IsCritical(name, cls, id) || hostController || rootHub });
            }
        }
        finally { N.SetupDiDestroyDeviceInfoList(set); }
        PropagateCriticality(result);
        return result.OrderBy(d => d.FriendlyName).ToList();
    }

    /// <summary>
    /// Criticality must flow upward through the devnode parent chain. A USB composite keyboard
    /// exposes its physical devnode with class "USB"; only the HID child underneath it carries
    /// class "Keyboard", and the topology builder filters interface nodes out of the physical
    /// tree. Without this pass the physical node looks harmless and the resolver will disable
    /// it, taking system input with it.
    ///
    /// This walks only what <see cref="Enumerate"/> collected (USB, HID and host controllers),
    /// so non-input subtrees such as USBSTOR storage are not covered here. The elevated-side
    /// validator performs the exhaustive cfgmgr32 subtree walk.
    /// </summary>
    private static void PropagateCriticality(List<UsbDeviceInfo> inventory)
    {
        var byId = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory) byId[item.InstanceId] = item;
        foreach (var item in inventory.Where(entry => entry.IsSelfCritical))
        {
            // The visited set guards against a malformed parent chain looping forever.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parentId = item.ParentInstanceId;
            while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                parent.HasCriticalDescendant = true;
                parentId = parent.ParentInstanceId;
            }
        }
    }

    public static int PerformLocalOperation(string instanceId, bool enable)
    {
        var cr = N.CM_Locate_DevNode(out var node, instanceId, 0); if (cr != 0) return (int)cr;
        return (int)(enable ? N.CM_Enable_DevNode(node, 0) : N.CM_Disable_DevNode(node, 0));
    }

    public static bool IsKnownDevice(string instanceId) => !string.IsNullOrWhiteSpace(instanceId) && N.CM_Locate_DevNode(out _, instanceId, 0) == 0;

    public Task SetEnabledElevatedAsync(string instanceId, bool enabled, CancellationToken token) => ApplyBatchElevatedAsync([new(instanceId, enabled)], token);

    public async Task ApplyBatchElevatedAsync(IReadOnlyList<DeviceOperation> operations, CancellationToken token)
    {
        if (operations.Count == 0) return;

        // Fails closed unless this process really is the production SimRigMode image and is not
        // itself an elevated worker. This is the check that makes the recursion in
        // docs/report.md impossible: a test host can never satisfy it.
        ElevationGuard.AssertMayElevate();

        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(operations))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ElevationGuard.VerifiedSelfPath, never Environment.ProcessPath directly: under a test
        // host the latter points at the test binary, which is exactly what went wrong.
        var psi = new ProcessStartInfo(ElevationGuard.VerifiedSelfPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--elevated-device-batch"); psi.ArgumentList.Add(data);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("The elevated device helper could not be started.");
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException(DescribeWorkerExit(process.ExitCode));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException("Elevation was declined.", ex); }
    }

    /// <summary>
    /// Worker exit codes are distinct from Configuration Manager results, which the previous
    /// implementation collapsed together via Environment.Exit(Math.Min(result, 255)).
    /// </summary>
    private static string DescribeWorkerExit(int exitCode) => exitCode switch
    {
        ElevatedWorkerExitCode.MalformedArguments => "The elevated helper rejected its payload. No device was changed.",
        ElevatedWorkerExitCode.NotProductionImage => $"The elevated helper refused to run because it is not the {Product.Name} application. No device was changed.",
        ElevatedWorkerExitCode.SafetyDenied => "The elevated helper refused the request on safety grounds. No device was changed.",
        ElevatedWorkerExitCode.DeviceOperationFailed => "Windows could not change one or more devices. Some earlier devices in the batch may already have changed. A reboot may be required.",
        ElevatedWorkerExitCode.InternalError => "The elevated helper failed unexpectedly. No device is known to have changed.",
        _ => $"The elevated helper exited with an unexpected code ({exitCode})."
    };

    public static bool IsCritical(string name, string cls, string id) => cls.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || cls.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || cls.Contains("System", StringComparison.OrdinalIgnoreCase) || name.Contains("host controller", StringComparison.OrdinalIgnoreCase) || name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
    private static string ParseVidPid(string id)
    {
        var pieces = id.Split('\\')[1].Split('&').Where(p => p.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)); return string.Join(" / ", pieces);
    }
    private static (string vid, string pid) ParseVidPidParts(string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant()) : ("", "");
    }
    private static string ParseSerial(string id)
    {
        var part = id.Split('\\').LastOrDefault() ?? ""; return part.Contains('&') ? "" : part;
    }
    private static string FirstMultiString(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
    private static string GetDeviceId(uint devInst) { var buffer = new char[1024]; return N.CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) == 0 ? new string(buffer).TrimEnd('\0') : ""; }
    private static string GetId(IntPtr set, ref N.SP_DEVINFO_DATA data) { var buffer = new char[1024]; return N.SetupDiGetDeviceInstanceId(set, ref data, buffer, (uint)buffer.Length, out _) ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var x && x >= 0 ? x : buffer.Length) : ""; }
    private static string GetProperty(IntPtr set, ref N.SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[4096]; if (!N.SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var required)) return ""; return Normalize(Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, buffer.Length)).Split('\0')[0]);
    }
    private static string GetDevicePropertyString(IntPtr set, ref N.SP_DEVINFO_DATA data, N.DEVPROPKEY key) { var buffer = new byte[4096]; return N.SetupDiGetDeviceProperty(set, ref data, ref key, out _, buffer, (uint)buffer.Length, out var required, 0) ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, buffer.Length)).Split('\0')[0] : ""; }
    private static string GetDevicePropertyGuid(IntPtr set, ref N.SP_DEVINFO_DATA data, N.DEVPROPKEY key) { var buffer = new byte[16]; return N.SetupDiGetDeviceProperty(set, ref data, ref key, out _, buffer, 16, out _, 0) ? new Guid(buffer).ToString("D") : ""; }
    private static string Normalize(string value)
    {
        var segments = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[^1] : value;
    }
}
