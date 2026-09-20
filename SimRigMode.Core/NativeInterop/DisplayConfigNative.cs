using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

/// <summary>
/// Raw declarations for the Win32 DisplayConfig API. Declarations only - no buffer management,
/// no policy, no decisions. Struct sizes are asserted by tests in SimRigMode.Tests, because a
/// silent layout regression here corrupts every call to SetDisplayConfig.
/// </summary>
internal static class DisplayConfigNative
{
    // ---- QueryDisplayConfig flags -------------------------------------------------
    internal const uint QDC_ALL_PATHS = 0x1;
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    internal const uint QDC_DATABASE_CURRENT = 0x4;

    /// <summary>
    /// Declared for completeness and deliberately NOT used. Under virtual-mode-aware semantics
    /// Windows reinterprets both modeInfoIdx fields as packed 16/16 bitfields
    /// (cloneGroupId/sourceModeInfoIdx and desktopModeInfoIdx/targetModeInfoIdx), and starts
    /// returning DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE entries. Adopting it means changing
    /// the struct definitions below, not just passing a flag.
    /// </summary>
    internal const uint QDC_VIRTUAL_MODE_AWARE = 0x10;
    internal const uint QDC_INCLUDE_HMD = 0x20;
    internal const uint QDC_VIRTUAL_REFRESH_RATE_AWARE = 0x40;

    // ---- SetDisplayConfig flags ---------------------------------------------------
    internal const uint SDC_TOPOLOGY_INTERNAL = 0x1;
    internal const uint SDC_TOPOLOGY_CLONE = 0x2;
    internal const uint SDC_TOPOLOGY_EXTEND = 0x4;
    internal const uint SDC_TOPOLOGY_EXTERNAL = 0x8;
    internal const uint SDC_TOPOLOGY_SUPPLIED = 0x10;
    internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;

    /// <summary>Validate only. Mutually exclusive with <see cref="SDC_APPLY"/>.</summary>
    internal const uint SDC_VALIDATE = 0x40;

    internal const uint SDC_APPLY = 0x80;
    internal const uint SDC_NO_OPTIMIZATION = 0x100;

    /// <summary>
    /// Writes the resulting topology into Windows' persistent display database. Every mode switch
    /// used to pass this. It is now reachable only from the explicit "Set as Windows default"
    /// action - never from an automatic transition. See docs/report.md.
    /// </summary>
    internal const uint SDC_SAVE_TO_DATABASE = 0x200;

    internal const uint SDC_ALLOW_CHANGES = 0x400;

    /// <summary>Declared so it is recognisable in a review. Must not be used.</summary>
    internal const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x800;

    internal const uint SDC_FORCE_MODE_ENUMERATION = 0x1000;
    internal const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
    internal const uint SDC_VIRTUAL_MODE_AWARE = 0x8000;
    internal const uint SDC_VIRTUAL_REFRESH_RATE_AWARE = 0x20000;

    // ---- DisplayConfigGetDeviceInfo request types ---------------------------------
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    /// <summary>
    /// Returns the adapter's device path. This is the discriminator for indirect displays: a GPU
    /// adapter path starts with \\?\PCI#, an indirect display driver's does not. Output
    /// technology cannot be used for this - the DisplayLink-backed Elgato Prompter in the
    /// incident reported LVDS (6), not INDIRECT_WIRED (16).
    /// </summary>
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4;

    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_BASE_TYPE = 6;

    // ---- mode info types ----------------------------------------------------------
    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3;

    internal const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    // ---- path and status flags -----------------------------------------------------
    internal const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    internal const uint DISPLAYCONFIG_PATH_PREFERRED_UNSCALED = 0x4;
    internal const uint DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE = 0x8;
    internal const uint DISPLAYCONFIG_PATH_BOOST_REFRESH_RATE = 0x10;

    /// <summary>
    /// The only path flags that may be submitted. Everything else QueryDisplayConfig returns is
    /// output-only state; replaying SUPPORT_VIRTUAL_MODE in particular makes Windows read
    /// modeInfoIdx under the packed-bitfield convention and produces garbage indices.
    /// </summary>
    internal const uint SUBMITTABLE_PATH_FLAGS = DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_PREFERRED_UNSCALED;

    internal const uint DISPLAYCONFIG_SOURCE_IN_USE = 0x1;
    internal const uint DISPLAYCONFIG_TARGET_IN_USE = 0x1;
    internal const uint DISPLAYCONFIG_TARGET_FORCIBLE = 0x2;

    // ---- DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS ------------------------------------
    internal const uint TARGET_NAME_FLAG_FRIENDLY_FROM_EDID = 0x1;
    internal const uint TARGET_NAME_FLAG_FRIENDLY_FORCED = 0x2;
    internal const uint TARGET_NAME_FLAG_EDID_IDS_VALID = 0x4;

    // ---- output technology ----------------------------------------------------------
    // Secondary signals only. See DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME above.
    internal const uint OUTPUT_TECHNOLOGY_HDMI = 5;
    internal const uint OUTPUT_TECHNOLOGY_LVDS = 6;
    internal const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10;
    internal const uint OUTPUT_TECHNOLOGY_MIRACAST = 15;
    internal const uint OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 16;
    internal const uint OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL = 17;
    internal const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL = 18;
    internal const uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    // ---- Win32 results ---------------------------------------------------------------
    // These APIs return the error code directly and do NOT set last-error.
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_GEN_FAILURE = 31;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_BAD_CONFIGURATION = 1610;

    // ---- entry points -----------------------------------------------------------------
    // ExactSpelling: DisplayConfigGetDeviceInfo has no A/W variants, so without it the runtime
    // probes for a "W" export on first resolution and only succeeds by fallback.
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [In, Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount, [In, Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    /// <summary>
    /// Arrays are [In, Out]: with SDC_VALIDATE | SDC_ALLOW_CHANGES, Windows may write back the
    /// configuration it would accept. That is worth logging, but it is never the source of truth
    /// for whether an apply worked - only a post-apply QueryDisplayConfig is.
    /// </summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int SetDisplayConfig(uint pathCount, [In, Out] DISPLAYCONFIG_PATH_INFO[]? paths, uint modeCount, [In, Out] DISPLAYCONFIG_MODE_INFO[]? modes, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME request);

    // ---- structs -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; public readonly long Value => ((long)HighPart << 32) | LowPart; public static LUID From(long v) => new() { LowPart = (uint)v, HighPart = (int)(v >> 32) }; }
    [StructLayout(LayoutKind.Sequential)] internal struct RATIONAL { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINTL { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECTL { public int left; public int top; public int right; public int bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct SIZE2D { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PATH_SOURCE_INFO
    {
        public LUID adapterId; public uint id;
        /// <summary>Aliases { UINT16 cloneGroupId; UINT16 sourceModeInfoIdx; } under
        /// SDC_VIRTUAL_MODE_AWARE. Flat uint is correct only because we never set that flag.</summary>
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id;
        /// <summary>Aliases { UINT16 desktopModeInfoIdx; UINT16 targetModeInfoIdx; } under
        /// SDC_VIRTUAL_MODE_AWARE. Flat uint is correct only because we never set that flag.</summary>
        public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering;
        /// <summary>BOOL. Declared as uint rather than [MarshalAs(Bool)] bool so that
        /// DISPLAYCONFIG_PATH_INFO stays blittable and arrays pin instead of being copied.
        /// Output-only: always submitted as 0.</summary>
        public uint targetAvailable;
        public uint statusFlags;

        internal readonly bool IsTargetAvailable => targetAvailable != 0;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DISPLAYCONFIG_PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate; public RATIONAL hSyncFreq; public RATIONAL vSyncFreq; public SIZE2D activeSize; public SIZE2D totalSize; public uint videoStandard; public uint scanLineOrdering;
    }

    /// <summary>The third union arm, for DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion; public RECTL DesktopImageClip; }

    /// <summary>
    /// Size = 64 is load-bearing: VIDEO_SIGNAL_INFO carries a UINT64, forcing the native union to
    /// 8-byte alignment at offset 16.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] public VIDEO_SIGNAL_INFO targetMode;
        [FieldOffset(16)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }
}
