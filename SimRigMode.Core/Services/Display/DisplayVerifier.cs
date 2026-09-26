using SimRigMode.Models;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services.Display;

public enum VerificationSeverity { Warning, Failure }

public sealed record VerificationFinding(VerificationSeverity Severity, string Display, string Field, string Expected, string Actual)
{
    public override string ToString() => $"{Severity}: {Display} {Field} - expected {Expected}, got {Actual}";
}

public sealed record VerificationReport(IReadOnlyList<VerificationFinding> Findings)
{
    public bool Passed => Findings.All(finding => finding.Severity != VerificationSeverity.Failure);
    public IEnumerable<VerificationFinding> Failures => Findings.Where(f => f.Severity == VerificationSeverity.Failure);
}

/// <summary>
/// Compares the topology Windows actually ended up with against what was intended.
///
/// The old code treated a zero return from SetDisplayConfig as success. That proves synchronous
/// API acceptance and nothing else: it does not prove the resulting topology matched the
/// snapshot, and with SDC_ALLOW_CHANGES Windows is explicitly permitted to apply something
/// different. "Transition completed with 0 warning(s)" was therefore compatible with a display
/// having silently disappeared.
///
/// Verification compares INTENT, not submitted bytes. Mode indices, array order, adapter LUIDs
/// and status flags are meaningless to compare and are ignored entirely.
/// </summary>
public static class DisplayVerifier
{
    /// <summary>Windows routinely lands on a nearby supported refresh rate.</summary>
    public const double DefaultRefreshToleranceHz = 1.0;

    public static VerificationReport Verify(DisplaySnapshot expected, DisplaySnapshot actual,
        double refreshToleranceHz = DefaultRefreshToleranceHz)
    {
        var findings = new List<VerificationFinding>();

        // 1. Set equality of active displays. THIS is the assertion that would have caught the
        //    incident: "the Prompter left the active set" is a failure here, in both directions.
        //    The exception is an indirect display that switches with the modes, or turns up
        //    unasked: one that did not come on, or did not go off, is reported. An indirect display
        //    never blocks a switch, and a slow DisplayLink display must not undo the whole layout.
        var toleratedMissing = 0;
        foreach (var missing in expected.Paths.Where(path => !actual.Paths.Any(a => DisplayIdentityMatcher.SameDisplay(a, path))))
        {
            var tolerated = missing.IsIndirect && missing.IndirectOptIn;
            if (tolerated) toleratedMissing++;
            findings.Add(new(tolerated ? VerificationSeverity.Warning : VerificationSeverity.Failure, missing.FriendlyName, "active",
                "active", "not present in the resulting topology"));
        }

        var toleratedExtra = 0;
        foreach (var unexpected in actual.Paths.Where(path => !expected.Paths.Any(e => DisplayIdentityMatcher.SameDisplay(e, path))))
        {
            if (unexpected.IsIndirect) toleratedExtra++;
            findings.Add(new(unexpected.IsIndirect ? VerificationSeverity.Warning : VerificationSeverity.Failure, unexpected.FriendlyName, "active",
                "not active", "active in the resulting topology"));
        }

        if (expected.Paths.Count - toleratedMissing != actual.Paths.Count - toleratedExtra)
            findings.Add(new(VerificationSeverity.Failure, "(topology)", "display count",
                expected.Paths.Count.ToString(), actual.Paths.Count.ToString()));

        // 2. Per display.
        foreach (var expectedPath in expected.Paths)
        {
            var actualPath = actual.Paths.FirstOrDefault(a => DisplayIdentityMatcher.SameDisplay(a, expectedPath));
            if (actualPath is null) continue;

            var expectedMode = SourceMode(expected, expectedPath);
            var actualMode = SourceMode(actual, actualPath);
            var name = expectedPath.FriendlyName;

            if (expectedMode is not null && actualMode is not null)
            {
                if (expectedMode.Width != actualMode.Width || expectedMode.Height != actualMode.Height)
                    findings.Add(new(VerificationSeverity.Failure, name, "resolution",
                        $"{expectedMode.Width}x{expectedMode.Height}", $"{actualMode.Width}x{actualMode.Height}"));

                // Pixel format is driver-normalised; informational only.
                if (expectedMode.PixelFormat != actualMode.PixelFormat)
                    findings.Add(new(VerificationSeverity.Warning, name, "pixel format",
                        expectedMode.PixelFormat.ToString(), actualMode.PixelFormat.ToString()));
            }

            if (expectedPath.Rotation != actualPath.Rotation)
                findings.Add(new(VerificationSeverity.Failure, name, "rotation",
                    expectedPath.Rotation.ToString(), actualPath.Rotation.ToString()));

            var expectedHz = Hz(expectedPath);
            var actualHz = Hz(actualPath);
            if (expectedHz > 0 && actualHz > 0 && Math.Abs(expectedHz - actualHz) > refreshToleranceHz)
                findings.Add(new(VerificationSeverity.Warning, name, "refresh rate",
                    $"{expectedHz:0.###} Hz", $"{actualHz:0.###} Hz"));
        }

        // 3. Relative geometry. Absolute coordinates are NOT compared: Windows legitimately
        //    rebases the desktop origin, so only each display's offset from a shared anchor
        //    display is meaningful. A display Windows placed somewhere else is reported, not
        //    failed: Windows moves displays itself to settle overlaps, and a monitor a few pixels
        //    off is not a failed switch. It used to roll back a whole switch for 33 pixels.
        //    A missing display, a wrong resolution or a wrong rotation still fails it.
        findings.AddRange(CompareGeometry(expected, actual));

        return new(findings);
    }

    private static IEnumerable<VerificationFinding> CompareGeometry(DisplaySnapshot expected, DisplaySnapshot actual)
    {
        // Anchor on a display present in BOTH snapshots, and measure every other display's
        // offset from it. Anchoring on "whichever display sits at (0,0)" is wrong: Windows
        // rebases the desktop origin, so that can be a DIFFERENT display before and after, which
        // makes an unchanged arrangement look like every monitor moved.
        var anchor = expected.Paths
            .Select(e => (expected: e, actual: actual.Paths.FirstOrDefault(a => DisplayIdentityMatcher.SameDisplay(a, e))))
            .FirstOrDefault(pair => pair.actual is not null);
        if (anchor.actual is null) yield break;

        var expectedAnchorMode = SourceMode(expected, anchor.expected);
        var actualAnchorMode = SourceMode(actual, anchor.actual);
        if (expectedAnchorMode is null || actualAnchorMode is null) yield break;

        foreach (var expectedPath in expected.Paths)
        {
            var actualPath = actual.Paths.FirstOrDefault(a => DisplayIdentityMatcher.SameDisplay(a, expectedPath));
            if (actualPath is null) continue;

            var expectedMode = SourceMode(expected, expectedPath);
            var actualMode = SourceMode(actual, actualPath);
            if (expectedMode is null || actualMode is null) continue;

            var expectedOffset = (expectedMode.PositionX - expectedAnchorMode.PositionX,
                                  expectedMode.PositionY - expectedAnchorMode.PositionY);
            var actualOffset = (actualMode.PositionX - actualAnchorMode.PositionX,
                                actualMode.PositionY - actualAnchorMode.PositionY);

            if (expectedOffset != actualOffset)
                yield return new(VerificationSeverity.Warning, expectedPath.FriendlyName, "position",
                    $"{expectedOffset.Item1},{expectedOffset.Item2} relative to {anchor.expected.FriendlyName}",
                    $"{actualOffset.Item1},{actualOffset.Item2}");
        }
    }

    private static DisplayModeRecord? SourceMode(DisplaySnapshot snapshot, DisplayPathRecord path)
    {
        if (path.SourceModeInfoIdx != N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && path.SourceModeInfoIdx < snapshot.Modes.Count)
        {
            var candidate = snapshot.Modes[(int)path.SourceModeInfoIdx];
            if (candidate.InfoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) return candidate;
        }
        return snapshot.Modes.FirstOrDefault(mode =>
            mode.InfoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE &&
            mode.Adapter == path.SourceAdapter && mode.Id == path.SourceId);
    }

    private static double Hz(DisplayPathRecord path) =>
        path.RefreshDenominator == 0 ? 0 : (double)path.RefreshNumerator / path.RefreshDenominator;
}
