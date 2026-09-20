using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

internal static class DisplayConfigNative
{
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    internal const uint SDC_APPLY = 0x80;
    internal const uint SDC_SAVE_TO_DATABASE = 0x200;
    internal const uint SDC_ALLOW_CHANGES = 0x400;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")]
    internal static extern int SetDisplayConfig(uint pathCount, DISPLAYCONFIG_PATH_INFO[] paths, uint modeCount, DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; public readonly long Value => ((long)HighPart << 32) | LowPart; public static LUID From(long v) => new() { LowPart = (uint)v, HighPart = (int)(v >> 32) }; }
    [StructLayout(LayoutKind.Sequential)] internal struct RATIONAL { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINTL { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] internal struct SIZE2D { public uint cx; public uint cy; }
    [StructLayout(LayoutKind.Sequential)] internal struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] internal struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DISPLAYCONFIG_PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }
    [StructLayout(LayoutKind.Sequential)] internal struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate; public RATIONAL hSyncFreq; public RATIONAL vSyncFreq; public SIZE2D activeSize; public SIZE2D totalSize; public uint videoStandard; public uint scanLineOrdering;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public SOURCE_MODE sourceMode; [FieldOffset(16)] public VIDEO_SIGNAL_INFO targetMode;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
}
