using System.Runtime.InteropServices;
using SimRigMode.Models;
using N = SimRigMode.NativeInterop.CoreAudioNative;

namespace SimRigMode.Services;

public sealed class AudioService
{
    public IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        var result = new List<AudioEndpointInfo> { new() { Id = "", Name = "Do not change", IsPlayback = true }, new() { Id = "", Name = "Do not change", IsPlayback = false } };
        var enumerator = CreateEnumerator();
        try
        {
            foreach (var flow in new[] { N.EDataFlow.Render, N.EDataFlow.Capture })
            {
                ThrowIfFailed(enumerator.EnumAudioEndpoints(flow, N.DEVICE_STATE_ALL, out var collection), "Audio endpoints could not be enumerated");
                try
                {
                    collection.GetCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        collection.Item(i, out var device);
                        try
                        {
                            device.GetId(out var id); device.OpenPropertyStore(N.STGM_READ, out var store);
                            try { var key = N.PKEY_Device_FriendlyName; store.GetValue(ref key, out var value); result.Add(new() { Id = id, Name = value.GetString(), IsPlayback = flow == N.EDataFlow.Render }); }
                            finally { Marshal.ReleaseComObject(store); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(collection); }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result;
    }

    public void Apply(AudioSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.PlaybackDefaultId) && string.IsNullOrWhiteSpace(selection.PlaybackCommunicationsId) && string.IsNullOrWhiteSpace(selection.RecordingDefaultId) && string.IsNullOrWhiteSpace(selection.RecordingCommunicationsId)) return;
        var type = Type.GetTypeFromCLSID(N.CLSID_PolicyConfigClient, true)!;
        var policy = (N.IPolicyConfig)Activator.CreateInstance(type)!;
        try
        {
            Set(policy, selection.PlaybackDefaultId, N.ERole.Console); Set(policy, selection.PlaybackDefaultId, N.ERole.Multimedia);
            Set(policy, selection.PlaybackCommunicationsId, N.ERole.Communications);
            Set(policy, selection.RecordingDefaultId, N.ERole.Console); Set(policy, selection.RecordingDefaultId, N.ERole.Multimedia);
            Set(policy, selection.RecordingCommunicationsId, N.ERole.Communications);
        }
        finally { Marshal.ReleaseComObject(policy); }
    }
    private static void Set(N.IPolicyConfig policy, string id, N.ERole role) { if (!string.IsNullOrWhiteSpace(id)) ThrowIfFailed(policy.SetDefaultEndpoint(id, role), $"Windows rejected audio endpoint '{id}'"); }
    private static N.IMMDeviceEnumerator CreateEnumerator() => (N.IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(N.CLSID_MMDeviceEnumerator, true)!)!;
    private static void ThrowIfFailed(int hr, string message) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
}
