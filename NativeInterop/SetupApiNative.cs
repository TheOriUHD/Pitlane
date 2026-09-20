using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

internal static class SetupApiNative
{
    internal const uint DIGCF_PRESENT = 2, DIGCF_ALLCLASSES = 4;
    internal const uint SPDRP_DEVICEDESC = 0, SPDRP_HARDWAREID = 1, SPDRP_SERVICE = 4, SPDRP_CLASS = 7, SPDRP_DRIVER = 9, SPDRP_MFG = 11, SPDRP_FRIENDLYNAME = 12, SPDRP_LOCATION_INFORMATION = 13, SPDRP_ENUMERATOR_NAME = 22, SPDRP_ADDRESS = 28, SPDRP_LOCATION_PATHS = 35, SPDRP_BASE_CONTAINERID = 36;
    internal const uint DN_STARTED = 0x8, DN_PRESENT = 0x2;
    internal static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)] internal struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(Guid format, uint id) { fmtid = format; pid = id; } }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, char[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint propertyType, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Enable_DevNode(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Disable_DevNode(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_ID(uint devInst, char[] buffer, uint length, uint flags);
}
