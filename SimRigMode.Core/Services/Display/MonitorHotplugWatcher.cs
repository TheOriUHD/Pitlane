using System.Runtime.InteropServices;
using N = SimRigMode.NativeInterop.DeviceNotificationNative;

namespace SimRigMode.Services.Display;

/// <summary>
/// Reports every monitor arriving or leaving, event-driven: no polling, no window, and nothing
/// is ever asked of the device. Feeds <see cref="DisplayBounceDetector"/>.
/// </summary>
public sealed class MonitorHotplugWatcher : IDisposable
{
    // Held for the lifetime of the registration: the native side keeps only the function pointer.
    private readonly N.CM_NOTIFY_CALLBACK _callback;
    private IntPtr _registration;

    /// <summary>(monitor device path, arrived). Raised on a thread-pool thread.</summary>
    public event Action<string, bool>? Changed;

    public MonitorHotplugWatcher() => _callback = OnNotification;

    public bool Start()
    {
        if (_registration != IntPtr.Zero) return true;
        var filter = new N.CM_NOTIFY_FILTER
        {
            cbSize = (uint)Marshal.SizeOf<N.CM_NOTIFY_FILTER>(),
            FilterType = N.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = N.GUID_DEVINTERFACE_MONITOR
        };
        var result = N.CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out var registration);
        if (result != N.CR_SUCCESS || registration == IntPtr.Zero) return false;
        _registration = registration;
        return true;
    }

    private uint OnNotification(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        // A native callback frame: nothing may escape it.
        try
        {
            if ((action == N.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL || action == N.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) &&
                eventData != IntPtr.Zero && eventDataSize > N.EVENT_DATA_SYMBOLIC_LINK_OFFSET)
            {
                var path = Marshal.PtrToStringUni(eventData + N.EVENT_DATA_SYMBOLIC_LINK_OFFSET) ?? "";
                if (path.Length > 0) Changed?.Invoke(path, action == N.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL);
            }
        }
        catch { /* the subscribers own their own reporting */ }
        return 0;
    }

    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _registration, IntPtr.Zero);
        // Blocks until in-flight callbacks finish, so it must never be called from one.
        if (registration != IntPtr.Zero) N.CM_Unregister_Notification(registration);
    }
}
