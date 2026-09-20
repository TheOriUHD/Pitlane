namespace SimRigMode.Services.Display;

using SimRigMode.Models;

/// <summary>
/// The real gateway: the only thing in the display pipeline that touches the kernel. Everything
/// above it - matching, building, verifying, the rollback state machine - is pure and tested
/// against a fake.
/// </summary>
public sealed class DisplayConfigGateway : IDisplayConfigGateway
{
    private readonly DisplayService _displays;

    public DisplayConfigGateway(DisplayService displays) => _displays = displays;

    public DisplaySnapshot CaptureActive() => _displays.CaptureCurrent();

    public int Submit(DisplaySnapshot snapshot, uint sdcFlags) => _displays.Submit(snapshot, sdcFlags);
}
