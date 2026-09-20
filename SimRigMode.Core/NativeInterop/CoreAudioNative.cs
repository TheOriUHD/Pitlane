using System.Runtime.InteropServices;

namespace SimRigMode.NativeInterop;

internal static class CoreAudioNative
{
    internal enum EDataFlow { Render, Capture, All }
    internal enum ERole { Console, Multimedia, Communications }
    internal const uint DEVICE_STATE_ALL = 0xF;
    internal const uint STGM_READ = 0;
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    internal static readonly PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    [StructLayout(LayoutKind.Sequential)] internal struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    internal const ushort VT_LPWSTR = 31;

    /// <summary>
    /// PROPVARIANT is 24 bytes on x64: an 8-byte header (vt plus three reserved WORDs, padded)
    /// followed by a 16-byte union, whose largest arm is a counted array
    /// { ULONG cElems; void* pElems; }.
    ///
    /// Size was previously unset. With fields at offsets 0 and 8 the runtime computed
    /// sizeof == 16, so every IPropertyStore::GetValue overran the marshalled buffer by 8 bytes,
    /// on every endpoint, on every enumeration. It "worked" because the overrun usually landed
    /// in stack padding.
    ///
    /// This project publishes win-x64 only (see RuntimeIdentifier in SimRigMode.csproj); the
    /// value would be 16 on x86. A test asserts the size.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pointerValue;
        public readonly string GetString() => vt == VT_LPWSTR && pointerValue != IntPtr.Zero ? Marshal.PtrToStringUni(pointerValue) ?? "" : "";
    }

    /// <summary>
    /// Frees whatever a PROPVARIANT owns. Never called before, so every VT_LPWSTR friendly name
    /// leaked once per endpoint per enumeration - and enumeration uses DEVICE_STATE_ALL, so that
    /// is every render and capture endpoint in every state, on every settings refresh.
    /// </summary>
    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection { int GetCount(out uint count); int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, uint context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(uint access, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out uint count); int GetAt(uint index, out PROPERTYKEY key); int GetValue(ref PROPERTYKEY key, out PROPVARIANT value); int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value); int Commit();
    }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out IntPtr format);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, out PROPVARIANT value);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, ref PROPVARIANT value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
