using SimRigMode.Models;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services.Display;

public sealed record SubmissionIssue(string DisplayName, string Reason);

/// <param name="Submission">Ready to hand to SetDisplayConfig, or null when the profile could
/// not be realised against the live topology.</param>
/// <param name="Expected">What must be active afterwards. This is what verification compares
/// against - not the submitted bytes.</param>
public sealed record SubmissionResult(
    DisplaySnapshot? Submission,
    IReadOnlyList<DisplayPathRecord> Expected,
    IReadOnlyList<SubmissionIssue> Issues,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns a saved profile plus the live topology into a topology submission.
///
/// Two things here matter more than everything else in this file.
///
/// CARRY-THROUGH. SDC_USE_SUPPLIED_DISPLAY_CONFIG has no "leave this display alone" semantics:
/// any active path omitted from the submitted array becomes INACTIVE. So merely excluding an
/// indirect display from a profile does not protect it - it tears it down, which is exactly the
/// incident. Every live indirect display the user has not opted into managing is therefore
/// re-appended to the submission UNCHANGED, with its current ids, modes and geometry. The net
/// effect is that a mode switch never adds, removes or re-times a DisplayLink display in either
/// direction.
///
/// INDEX REGENERATION. Mode indices are never replayed from the profile. The submitted mode
/// array is rebuilt from empty and keyed by (adapter, id) per info type, so de-duplication and
/// clone-group sharing fall out of the dictionary rather than being reconstructed. A saved index
/// therefore cannot be stale, out of range, or pointed at the wrong info type.
/// </summary>
public static class DisplaySubmissionBuilder
{
    public static SubmissionResult Build(DisplaySnapshot profile, DisplaySnapshot liveActive)
    {
        var issues = new List<SubmissionIssue>();
        var notes = new List<string>();
        var submission = new DisplaySnapshot { CapturedUtc = DateTime.UtcNow };
        var expected = new List<DisplayPathRecord>();
        var matchedLive = new List<DisplayPathRecord>();

        if (profile.Paths.Count == 0)
        {
            issues.Add(new("(profile)", "This mode has no captured display layout."));
            return new(null, expected, issues, notes);
        }

        foreach (var saved in profile.Paths)
        {
            var match = DisplayIdentityMatcher.Resolve(saved, liveActive.Paths);
            if (match.Live is null)
            {
                issues.Add(new(saved.FriendlyName, match.Detail));
                continue;
            }

            matchedLive.Add(match.Live);
            notes.Add($"{saved.FriendlyName}: {match.Detail}");

            var path = Rebind(saved, match.Live);
            AddPath(profile, saved, path, submission, preferSavedTimings: !match.Live.IsIndirect);
            expected.Add(path);
        }

        if (issues.Count > 0) return new(null, expected, issues, notes);

        // Carry-through. Order matters only for readability; Windows does not care.
        foreach (var livePath in liveActive.Paths)
        {
            if (!livePath.IsIndirect || livePath.IndirectOptIn) continue;
            if (matchedLive.Any(matched => ReferenceEquals(matched, livePath))) continue;
            if (submission.Paths.Any(existing => DisplayIdentityMatcher.SameDisplay(existing, livePath))) continue;

            var carried = ClonePath(livePath);
            AddPath(liveActive, livePath, carried, submission, preferSavedTimings: true);
            expected.Add(carried);
            notes.Add($"{livePath.FriendlyName}: carried through unchanged (indirect display, not managed by this mode)");
        }

        NormalizePrimary(submission);

        var invariant = CheckInvariants(submission);
        if (invariant is not null)
        {
            issues.Add(new("(submission)", invariant));
            return new(null, expected, issues, notes);
        }

        return new(submission, expected, issues, notes);
    }

    /// <summary>
    /// Keeps the user's intent from the saved record (geometry, rotation, scaling, refresh) but
    /// takes every identifier from the live topology. This is what makes a profile survive a
    /// reboot: nothing per-boot is replayed.
    /// </summary>
    private static DisplayPathRecord Rebind(DisplayPathRecord saved, DisplayPathRecord live)
    {
        var path = ClonePath(saved);
        path.SourceAdapter = live.SourceAdapter;
        path.SourceId = live.SourceId;
        path.TargetAdapter = live.TargetAdapter;
        path.TargetId = live.TargetId;
        path.AdapterDevicePath = live.AdapterDevicePath;
        path.MonitorDevicePath = live.MonitorDevicePath;
        // Live, not saved: a stale output technology that disagrees with the live target is a
        // routine source of ERROR_INVALID_PARAMETER.
        path.OutputTechnology = live.OutputTechnology;
        path.IsIndirect = live.IsIndirect;
        path.IndirectReason = live.IndirectReason;
        return path;
    }

    private static void AddPath(DisplaySnapshot source, DisplayPathRecord sourcePath,
        DisplayPathRecord destinationPath, DisplaySnapshot destination, bool preferSavedTimings)
    {
        destinationPath.SourceModeInfoIdx = ImportMode(
            source, sourcePath.SourceModeInfoIdx, N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            destinationPath.SourceAdapter, destinationPath.SourceId, destination);

        // Target timings are optional. When they are omitted Windows derives the mode from the
        // path's refreshRate and scanLineOrdering, which is the safer choice for an indirect
        // display whose timings are synthesized by its driver rather than read from an EDID.
        destinationPath.TargetModeInfoIdx = preferSavedTimings
            ? ImportMode(source, sourcePath.TargetModeInfoIdx, N.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                destinationPath.TargetAdapter, destinationPath.TargetId, destination)
            : N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

        destinationPath.DesktopModeInfoIdx = N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        destination.Paths.Add(destinationPath);
    }

    /// <summary>
    /// Copies one mode into the submission, rebound to the live adapter and id, de-duplicating
    /// on (infoType, adapter, id). Two paths landing on the same source mode entry is what a
    /// clone group IS, so sharing falls out for free.
    /// </summary>
    private static uint ImportMode(DisplaySnapshot source, uint index, uint expectedInfoType,
        long adapter, uint id, DisplaySnapshot destination)
    {
        if (index == N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || index >= source.Modes.Count)
            return N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

        var mode = source.Modes[(int)index];
        // Guards against a profile whose indices point at the wrong kind of entry.
        if (mode.InfoType != expectedInfoType) return N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

        var existing = destination.Modes.FindIndex(candidate =>
            candidate.InfoType == expectedInfoType && candidate.Adapter == adapter && candidate.Id == id);
        if (existing >= 0) return (uint)existing;

        var clone = CloneMode(mode);
        clone.Adapter = adapter;
        clone.Id = id;
        destination.Modes.Add(clone);
        return (uint)(destination.Modes.Count - 1);
    }

    /// <summary>Everything that must hold before an array is handed to the kernel.</summary>
    private static string? CheckInvariants(DisplaySnapshot submission)
    {
        if (submission.Paths.Count == 0) return "the submission would activate no displays";

        foreach (var path in submission.Paths)
        {
            if (path.SourceModeInfoIdx == N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                return $"{path.FriendlyName} has no source mode";

            if (path.SourceModeInfoIdx >= submission.Modes.Count)
                return $"{path.FriendlyName} references source mode {path.SourceModeInfoIdx} of {submission.Modes.Count}";

            var sourceMode = submission.Modes[(int)path.SourceModeInfoIdx];
            if (sourceMode.InfoType != N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ||
                sourceMode.Adapter != path.SourceAdapter || sourceMode.Id != path.SourceId)
                return $"{path.FriendlyName} points at a source mode belonging to another display";

            if (path.TargetModeInfoIdx == N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) continue;

            if (path.TargetModeInfoIdx >= submission.Modes.Count)
                return $"{path.FriendlyName} references target mode {path.TargetModeInfoIdx} of {submission.Modes.Count}";

            var targetMode = submission.Modes[(int)path.TargetModeInfoIdx];
            if (targetMode.InfoType != N.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET ||
                targetMode.Adapter != path.TargetAdapter || targetMode.Id != path.TargetId)
                return $"{path.FriendlyName} points at a target mode belonging to another display";
        }

        if (submission.Modes.Any(mode => mode.InfoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE))
            return "the submission contains a desktop-image mode, which requires virtual-mode-aware semantics";

        return null;
    }

    private static void NormalizePrimary(DisplaySnapshot snapshot)
    {
        var sourceModes = snapshot.Paths
            .Select(path => path.SourceModeInfoIdx)
            .Where(index => index != N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && index < snapshot.Modes.Count)
            .Select(index => snapshot.Modes[(int)index])
            .Distinct().ToList();
        if (sourceModes.Count == 0 || sourceModes.Any(mode => mode is { PositionX: 0, PositionY: 0 })) return;

        var anchor = sourceModes.MinBy(mode => (long)mode.PositionX * mode.PositionX + (long)mode.PositionY * mode.PositionY)!;
        var offsetX = anchor.PositionX; var offsetY = anchor.PositionY;
        foreach (var mode in sourceModes) { mode.PositionX -= offsetX; mode.PositionY -= offsetY; }
    }

    internal static DisplayPathRecord ClonePath(DisplayPathRecord path) => new()
    {
        SourceAdapter = path.SourceAdapter, SourceId = path.SourceId, SourceModeInfoIdx = path.SourceModeInfoIdx,
        TargetAdapter = path.TargetAdapter, TargetId = path.TargetId, TargetModeInfoIdx = path.TargetModeInfoIdx,
        DesktopModeInfoIdx = path.DesktopModeInfoIdx, OutputTechnology = path.OutputTechnology, Rotation = path.Rotation,
        Scaling = path.Scaling, RefreshNumerator = path.RefreshNumerator, RefreshDenominator = path.RefreshDenominator,
        ScanLineOrdering = path.ScanLineOrdering, TargetAvailable = path.TargetAvailable, StatusFlags = path.StatusFlags,
        MonitorDevicePath = path.MonitorDevicePath, FriendlyName = path.FriendlyName, SourceName = path.SourceName,
        AdapterDevicePath = path.AdapterDevicePath, EdidManufactureId = path.EdidManufactureId,
        EdidProductCodeId = path.EdidProductCodeId, ConnectorInstance = path.ConnectorInstance,
        EdidIdsValid = path.EdidIdsValid, IsIndirect = path.IsIndirect,
        IndirectReason = path.IndirectReason, IndirectOptIn = path.IndirectOptIn
    };

    private static DisplayModeRecord CloneMode(DisplayModeRecord mode) => new()
    {
        InfoType = mode.InfoType, Id = mode.Id, Adapter = mode.Adapter, Width = mode.Width, Height = mode.Height,
        PixelFormat = mode.PixelFormat, PositionX = mode.PositionX, PositionY = mode.PositionY, PixelRate = mode.PixelRate,
        HSyncNumerator = mode.HSyncNumerator, HSyncDenominator = mode.HSyncDenominator,
        VSyncNumerator = mode.VSyncNumerator, VSyncDenominator = mode.VSyncDenominator,
        ActiveWidth = mode.ActiveWidth, ActiveHeight = mode.ActiveHeight, TotalWidth = mode.TotalWidth,
        TotalHeight = mode.TotalHeight, VideoStandard = mode.VideoStandard, ScanLineOrdering = mode.ScanLineOrdering
    };
}
