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
                    ThrowIfFailed(collection.GetCount(out var count), "The audio endpoint count could not be read");
                    for (uint i = 0; i < count; i++)
                    {
                        // Every HRESULT below was previously ignored. Worse, a failed Item() left
                        // `device` null, so the NullReferenceException that followed was then
                        // masked by an ArgumentNullException from ReleaseComObject in the finally.
                        if (collection.Item(i, out var device) < 0 || device is null) continue;
                        try
                        {
                            if (device.GetId(out var id) < 0 || string.IsNullOrEmpty(id)) continue;

                            // Expected to fail for endpoints that are unplugged, disabled or not
                            // present - which DEVICE_STATE_ALL deliberately includes. Skip them
                            // rather than tearing down the whole enumeration.
                            if (device.OpenPropertyStore(N.STGM_READ, out var store) < 0 || store is null)
                            {
                                result.Add(new() { Id = id, Name = id, IsPlayback = flow == N.EDataFlow.Render });
                                continue;
                            }
                            try
                            {
                                var key = N.PKEY_Device_FriendlyName;
                                if (store.GetValue(ref key, out var value) < 0) continue;
                                try { result.Add(new() { Id = id, Name = value.GetString(), IsPlayback = flow == N.EDataFlow.Render }); }
                                finally { N.PropVariantClear(ref value); }
                            }
                            finally { Release(store); }
                        }
                        finally { Release(device); }
                    }
                }
                finally { Release(collection); }
            }
        }
        finally { Release(enumerator); }
        return result;
    }

    public void Apply(AudioSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.PlaybackDefaultId) && string.IsNullOrWhiteSpace(selection.PlaybackCommunicationsId) && string.IsNullOrWhiteSpace(selection.RecordingDefaultId) && string.IsNullOrWhiteSpace(selection.RecordingCommunicationsId)) return;
        var type = Type.GetTypeFromCLSID(N.CLSID_PolicyConfigClient, true)!;

        // IPolicyConfig is undocumented and unversioned. A QueryInterface failure here surfaces
        // as a bare InvalidCastException, which tells the user nothing actionable.
        N.IPolicyConfig policy;
        try { policy = (N.IPolicyConfig)Activator.CreateInstance(type)!; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"This version of Windows does not expose the audio policy interface {Product.Name} " +
                "uses to change default devices. Default audio devices were left unchanged.", ex);
        }

        try
        {
            Set(policy, selection.PlaybackDefaultId, N.ERole.Console); Set(policy, selection.PlaybackDefaultId, N.ERole.Multimedia);
            Set(policy, selection.PlaybackCommunicationsId, N.ERole.Communications);
            Set(policy, selection.RecordingDefaultId, N.ERole.Console); Set(policy, selection.RecordingDefaultId, N.ERole.Multimedia);
            Set(policy, selection.RecordingCommunicationsId, N.ERole.Communications);
        }
        finally { Release(policy); }
    }

    /// <summary>ReleaseComObject throws ArgumentNullException on null, which inside a finally
    /// silently replaces whatever exception was already in flight.</summary>
    private static void Release(object? comObject)
    {
        if (comObject is null) return;
        try { Marshal.ReleaseComObject(comObject); } catch { /* nothing useful to do while unwinding */ }
    }
    private static void Set(N.IPolicyConfig policy, string id, N.ERole role) { if (!string.IsNullOrWhiteSpace(id)) ThrowIfFailed(policy.SetDefaultEndpoint(id, role), $"Windows rejected audio endpoint '{id}'"); }
    private static N.IMMDeviceEnumerator CreateEnumerator() => (N.IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(N.CLSID_MMDeviceEnumerator, true)!)!;
    /// <summary>
    /// The message parameter used to be accepted and silently discarded, so every carefully
    /// written error surfaced to the user as a bare COMException. ThrowExceptionForHR is also
    /// called with errorInfo -1 so it cannot attach a stale IErrorInfo left by an unrelated call
    /// on this thread.
    /// </summary>
    private static void ThrowIfFailed(int hr, string message)
    {
        if (hr >= 0) return;
        Exception inner;
        try { Marshal.ThrowExceptionForHR(hr, new IntPtr(-1)); inner = new COMException(message, hr); }
        catch (Exception ex) { inner = ex; }
        throw new InvalidOperationException($"{message} (HRESULT 0x{hr:X8}).", inner);
    }
}
