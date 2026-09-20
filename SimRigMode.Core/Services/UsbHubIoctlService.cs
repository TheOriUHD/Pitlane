using System.Runtime.InteropServices;
using System.Text;
using SimRigMode.NativeInterop;

namespace SimRigMode.Services;

/// <summary>Reads the hub driver's real connection table and maps each occupied port by driver key.</summary>
internal sealed class UsbHubIoctlService
{
    internal sealed record PortFacts(int Port, string Speed, bool IsHub, string ProductString);

    public IReadOnlyDictionary<string, PortFacts> Probe()
    {
        var result = new Dictionary<string, PortFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateHubPaths()) ProbeHub(path, result);
        return result;
    }

    private static IEnumerable<string> EnumerateHubPaths()
    {
        var guid = UsbIoctlNative.HubInterface;
        var set = UsbIoctlNative.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, UsbIoctlNative.DigcfPresent | UsbIoctlNative.DigcfDeviceInterface);
        if (set == SetupApiNative.InvalidHandle) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new UsbIoctlNative.DeviceInterfaceData { Size = (uint)Marshal.SizeOf<UsbIoctlNative.DeviceInterfaceData>() };
                if (!UsbIoctlNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) yield break;
                _ = UsbIoctlNative.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (UsbIoctlNative.SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                        if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupApiNative.SetupDiDestroyDeviceInfoList(set); }
    }

    private static void ProbeHub(string path, IDictionary<string, PortFacts> result)
    {
        using var handle = UsbIoctlNative.CreateFile(path, UsbIoctlNative.GenericWrite, UsbIoctlNative.FileShareRead | UsbIoctlNative.FileShareWrite, IntPtr.Zero, UsbIoctlNative.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return;
        var node = Marshal.AllocHGlobal(80);
        try
        {
            Span<byte> clear = stackalloc byte[80]; Marshal.Copy(clear.ToArray(), 0, node, 80);
            if (!UsbIoctlNative.DeviceIoControl(handle, UsbIoctlNative.IoctlGetNodeInformation, node, 80, node, 80, out _, IntPtr.Zero)) return;
            var ports = Marshal.ReadByte(node, 6);
            for (var port = 1; port <= ports; port++) ProbePort(handle, port, result);
        }
        finally { Marshal.FreeHGlobal(node); }
    }

    private static void ProbePort(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int port, IDictionary<string, PortFacts> result)
    {
        var info = Marshal.AllocHGlobal(64);
        try
        {
            for (var offset = 0; offset < 64; offset += 4) Marshal.WriteInt32(info, offset, 0);
            Marshal.WriteInt32(info, port);
            if (!UsbIoctlNative.DeviceIoControl(handle, UsbIoctlNative.IoctlGetNodeConnectionInformationEx, info, 64, info, 64, out _, IntPtr.Zero)) return;
            var speed = Marshal.ReadByte(info, 23) switch { 0 => "Low speed", 1 => "Full speed", 2 => "High speed", 3 => "SuperSpeed", _ => "USB" };
            var isHub = Marshal.ReadByte(info, 24) != 0;
            var productIndex = Marshal.ReadByte(info, 19);
            var key = GetDriverKey(handle, port);
            var product = productIndex == 0 ? "" : GetStringDescriptor(handle, port, productIndex);
            if (!string.IsNullOrWhiteSpace(key)) result[key] = new(port, speed, isHub, product);
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private static string GetDriverKey(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int port)
    {
        const int size = 2048;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var offset = 0; offset < size; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
            Marshal.WriteInt32(buffer, port);
            if (!UsbIoctlNative.DeviceIoControl(handle, UsbIoctlNative.IoctlGetNodeConnectionDriverKeyName, buffer, size, buffer, size, out _, IntPtr.Zero)) return "";
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 8))?.TrimEnd('\0') ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string GetStringDescriptor(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int port, byte descriptorIndex)
    {
        const int size = 512;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var offset = 0; offset < size; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
            Marshal.WriteInt32(buffer, port);
            Marshal.WriteByte(buffer, 4, 0x80);
            Marshal.WriteByte(buffer, 5, 0x06);
            Marshal.WriteInt16(buffer, 6, (short)((3 << 8) | descriptorIndex));
            Marshal.WriteInt16(buffer, 8, 0x0409);
            Marshal.WriteInt16(buffer, 10, size - 12);
            if (!UsbIoctlNative.DeviceIoControl(handle, UsbIoctlNative.IoctlGetDescriptorFromNodeConnection, buffer, size, buffer, size, out _, IntPtr.Zero)) return "";
            var length = Marshal.ReadByte(buffer, 12);
            if (length < 2 || Marshal.ReadByte(buffer, 13) != 3) return "";
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 14), (length - 2) / 2)?.Trim('\0', ' ') ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
