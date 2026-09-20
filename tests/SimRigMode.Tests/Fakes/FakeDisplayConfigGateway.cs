using System.Text.Json;
using SimRigMode.Models;
using SimRigMode.Services.Display;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the kernel. Records every submission and its flags, so tests can
/// assert not just the outcome but exactly what was asked of Windows and in what order.
/// </summary>
internal sealed class FakeDisplayConfigGateway : IDisplayConfigGateway
{
    public FakeDisplayConfigGateway(DisplaySnapshot current) => Current = current;

    /// <summary>What QueryDisplayConfig would return right now.</summary>
    public DisplaySnapshot Current { get; set; }

    public List<uint> SubmittedFlags { get; } = [];
    public List<DisplaySnapshot> Submitted { get; } = [];

    /// <summary>Return non-zero to simulate Windows rejecting a particular submission.</summary>
    public Func<DisplaySnapshot, uint, int>? Reject { get; set; }

    /// <summary>When set, the NEXT apply installs this instead of what was submitted - i.e.
    /// Windows did something other than what was asked. One-shot, so a subsequent rollback
    /// behaves normally.</summary>
    public DisplaySnapshot? ApplyInstalls { get; set; }

    public int CaptureCount { get; private set; }

    public IEnumerable<uint> AppliedFlags => SubmittedFlags.Where(flags => (flags & N.SDC_APPLY) != 0);
    public IEnumerable<uint> ValidatedFlags => SubmittedFlags.Where(flags => (flags & N.SDC_VALIDATE) != 0);
    public bool AnythingWasApplied => AppliedFlags.Any();

    public DisplaySnapshot CaptureActive()
    {
        CaptureCount++;
        return Clone(Current);
    }

    public int Submit(DisplaySnapshot snapshot, uint sdcFlags)
    {
        SubmittedFlags.Add(sdcFlags);
        Submitted.Add(Clone(snapshot));

        var rejection = Reject?.Invoke(snapshot, sdcFlags) ?? 0;
        if (rejection != 0) return rejection;

        if ((sdcFlags & N.SDC_APPLY) != 0)
        {
            Current = Clone(ApplyInstalls ?? snapshot);
            ApplyInstalls = null;
        }
        return 0;
    }

    private static DisplaySnapshot Clone(DisplaySnapshot snapshot) =>
        JsonSerializer.Deserialize<DisplaySnapshot>(JsonSerializer.Serialize(snapshot))!;
}
