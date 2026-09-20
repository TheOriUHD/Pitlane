using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SimRigMode.NativeInterop;

internal static class UsbIoctlNative
{
    internal static readonly Guid HubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    internal const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    internal const uint GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    internal const uint IoctlGetNodeInformation = 0x220408;
    internal const uint IoctlGetDescriptorFromNodeConnection = 0x220410;
    internal const uint IoctlGetNodeConnectionDriverKeyName = 0x220420;
    internal const uint IoctlGetNodeConnectionInformationEx = 0x220448;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData { internal uint Size; internal Guid InterfaceClassGuid; internal uint Flags; internal UIntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceGuid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
