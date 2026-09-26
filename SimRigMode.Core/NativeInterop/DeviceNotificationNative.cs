using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

/// <summary>
/// CM_Register_Notification, for hearing about monitors arriving and leaving without polling
/// and without a window. Declarations only; sizes are asserted by tests.
/// </summary>
internal static class DeviceNotificationNative
{
    /// <summary>GUID_DEVINTERFACE_MONITOR. Its interface paths have exactly the form of the
    /// monitor device paths DisplayConfigGetDeviceInfo reports, so the two compare directly.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_MONITOR = new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

    internal const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    internal const uint CR_SUCCESS = 0;

    /// <summary>CM_NOTIFY_EVENT_DATA: FilterType and Reserved (8 bytes), then the interface
    /// class GUID (16 bytes), then the NUL-terminated symbolic link.</summary>
    internal const int EVENT_DATA_SYMBOLIC_LINK_OFFSET = 24;

    /// <summary>416 bytes: four DWORDs, then a union whose largest arm is
    /// WCHAR InstanceId[MAX_DEVICE_ID_LEN = 200]. cbSize must be exactly this.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint CM_NOTIFY_CALLBACK(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Register_Notification(ref CM_NOTIFY_FILTER filter, IntPtr context, CM_NOTIFY_CALLBACK callback, out IntPtr notifyContext);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Unregister_Notification(IntPtr notifyContext);
}
