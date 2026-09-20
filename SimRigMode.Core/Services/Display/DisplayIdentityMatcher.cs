using SimRigMode.Models;

namespace SimRigMode.Services.Display;

/// <summary>How a saved display was matched to a live one. Lower is stronger.</summary>
public enum DisplayMatchTier
{
    /// <summary>Exact monitor device path.</summary>
    MonitorDevicePath = 1,

    /// <summary>EDID manufacturer + product + connector instance. Survives a monitor being
    /// moved to a different connector on the same card.</summary>
    EdidTriple = 2,

    /// <summary>Adapter device path + target id. The replacement for the per-boot LUID.</summary>
    AdapterAndTarget = 3,

    /// <summary>Target id alone, and only when the output technology agrees. Weak.</summary>
    TargetIdOnly = 4,

    /// <summary>No unique match.</summary>
    None = 99
}

/// <param name="Live">The matched live path, or null.</param>
/// <param name="Detail">Why, in prose, for the transition log and the UI.</param>
public sealed record DisplayMatch(DisplayPathRecord? Live, DisplayMatchTier Tier, string Detail);

/// <summary>
/// Re-resolves a saved display against the live topology.
///
/// Profiles used to be replayed by their captured adapter LUID, source id and target id.
/// Adapter LUIDs are allocated by the display kernel when an adapter starts, so they change on
/// reboot, on a driver restart or TDR, and when an indirect display adapter is hot-plugged.
/// They are also frequently re-assigned IDENTICALLY on unchanged hardware, which is exactly why
/// this class of bug survives manual testing: it looks fine until the one time it is not.
///
/// Every tier requires a UNIQUE live match. Ambiguity resolves to None rather than to a guess,
/// because guessing here means driving the wrong monitor.
/// </summary>
public static class DisplayIdentityMatcher
{
    public static DisplayMatch Resolve(DisplayPathRecord saved, IReadOnlyList<DisplayPathRecord> live)
    {
        var byPath = Unique(live, candidate =>
            !string.IsNullOrWhiteSpace(saved.MonitorDevicePath) &&
            saved.MonitorDevicePath.Equals(candidate.MonitorDevicePath, StringComparison.OrdinalIgnoreCase));
        if (byPath.count == 1) return new(byPath.match, DisplayMatchTier.MonitorDevicePath, "matched by monitor device path");
        if (byPath.count > 1) return Ambiguous(DisplayMatchTier.MonitorDevicePath, byPath.count);

        var byEdid = Unique(live, candidate =>
            saved.EdidIdsValid && candidate.EdidIdsValid &&
            saved.EdidManufactureId == candidate.EdidManufactureId &&
            saved.EdidProductCodeId == candidate.EdidProductCodeId &&
            saved.ConnectorInstance == candidate.ConnectorInstance);
        if (byEdid.count == 1) return new(byEdid.match, DisplayMatchTier.EdidTriple, "matched by EDID identity");
        if (byEdid.count > 1) return Ambiguous(DisplayMatchTier.EdidTriple, byEdid.count);

        var byAdapter = Unique(live, candidate =>
            !string.IsNullOrWhiteSpace(saved.AdapterDevicePath) &&
            saved.AdapterDevicePath.Equals(candidate.AdapterDevicePath, StringComparison.OrdinalIgnoreCase) &&
            saved.TargetId == candidate.TargetId);
        if (byAdapter.count == 1) return new(byAdapter.match, DisplayMatchTier.AdapterAndTarget, "matched by adapter device path and target");
        if (byAdapter.count > 1) return Ambiguous(DisplayMatchTier.AdapterAndTarget, byAdapter.count);

        // Last resort. Refused outright for indirect displays: a wrong match there is precisely
        // the kind of mistake that destabilises a DisplayLink stack.
        if (saved.IsIndirect)
            return new(null, DisplayMatchTier.None,
                "no reliable match; weak target-id matching is not permitted for indirect displays");

        var byTarget = Unique(live, candidate =>
            saved.TargetId == candidate.TargetId && saved.OutputTechnology == candidate.OutputTechnology);
        if (byTarget.count == 1) return new(byTarget.match, DisplayMatchTier.TargetIdOnly, "matched by target id and output technology only");
        if (byTarget.count > 1) return Ambiguous(DisplayMatchTier.TargetIdOnly, byTarget.count);

        return new(null, DisplayMatchTier.None, "this display is not currently connected");
    }

    /// <summary>True when the two records describe the same physical display. Replaces the three
    /// separate copies of this comparison that used to live in DisplayService and SettingsWindow.</summary>
    public static bool SameDisplay(DisplayPathRecord left, DisplayPathRecord right)
    {
        if (!string.IsNullOrWhiteSpace(left.MonitorDevicePath) || !string.IsNullOrWhiteSpace(right.MonitorDevicePath))
            return left.MonitorDevicePath.Equals(right.MonitorDevicePath, StringComparison.OrdinalIgnoreCase);
        return left.TargetAdapter == right.TargetAdapter && left.TargetId == right.TargetId;
    }

    private static (DisplayPathRecord? match, int count) Unique(
        IReadOnlyList<DisplayPathRecord> live, Func<DisplayPathRecord, bool> predicate)
    {
        DisplayPathRecord? found = null;
        var count = 0;
        foreach (var candidate in live)
        {
            if (!predicate(candidate)) continue;
            found ??= candidate;
            count++;
            if (count > 1) break;
        }
        return (count == 1 ? found : null, count);
    }

    private static DisplayMatch Ambiguous(DisplayMatchTier tier, int count) =>
        new(null, DisplayMatchTier.None, $"ambiguous: {count} connected displays match by {tier}. Re-capture this layout.");
}
