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
///
/// SWITCHING ON. A monitor that is connected but switched out of the layout does not appear in
/// QDC_ONLY_ACTIVE_PATHS at all. The builder used to resolve profiles against that list only, so
/// any profile that had to switch a monitor ON was refused as "not connected" - switching from
/// Sim Rig back to Desktop could never have succeeded. Such monitors now come from QDC_ALL_PATHS
/// (<see cref="ConnectedTarget"/>) and get a free source on their own adapter.
/// </summary>
public static class DisplaySubmissionBuilder
{
    /// <param name="connected">Every monitor Windows can drive, including ones switched out of
    /// the layout. Without it only displays that are already on can be used.</param>
    /// <param name="switchedIndirect">Monitor paths of the indirect displays the user lets the
    /// modes switch (<see cref="Models.GeneralConfig.SwitchedIndirectDisplays"/>). Those follow the
    /// profile like any other display; every other indirect display is carried through.</param>
    public static SubmissionResult Build(DisplaySnapshot profile, DisplaySnapshot liveActive,
        IReadOnlyList<ConnectedTarget>? connected = null, IReadOnlyCollection<string>? switchedIndirect = null)
    {
        bool Switched(DisplayPathRecord path) => path.IndirectOptIn ||
            (switchedIndirect?.Any(entry => string.Equals(entry, path.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)) ?? false);

        var issues = new List<SubmissionIssue>();
        var notes = new List<string>();
        var submission = new DisplaySnapshot { CapturedUtc = DateTime.UtcNow };
        var expected = new List<DisplayPathRecord>();

        if (profile.Paths.Count == 0)
        {
            issues.Add(new("(profile)", "This mode has no captured display layout."));
            return new(null, expected, issues, notes);
        }

        // Monitors that are connected but switched out of the layout on screen.
        var switchedOut = (connected ?? [])
            .Where(target => !target.IsActive && !liveActive.Paths.Any(target.Is))
            .ToList();
        var switchedOutCandidates = switchedOut.Select(target => target.AsCandidate()).ToList();

        // ---- 1. Resolve every saved display: on now, switched out, or missing --------------
        var kept = new Dictionary<DisplayPathRecord, DisplayPathRecord>(ReferenceEqualityComparer.Instance);
        var toSwitchOn = new List<(DisplayPathRecord Saved, ConnectedTarget Target)>();
        foreach (var saved in profile.Paths)
        {
            var match = DisplayIdentityMatcher.Resolve(saved, liveActive.Paths);
            if (match.Live is not null)
            {
                kept[saved] = match.Live;
                notes.Add($"{saved.FriendlyName}: {match.Detail}");
                continue;
            }

            var offMatch = DisplayIdentityMatcher.Resolve(saved, switchedOutCandidates);
            if (offMatch.Live is not null)
            {
                var target = switchedOut[switchedOutCandidates.IndexOf(offMatch.Live)];
                if (target.IsIndirect && !Switched(saved))
                {
                    // A mode never switches an indirect display on or off: see CARRY-THROUGH.
                    notes.Add($"{saved.FriendlyName}: connected but not in the layout; indirect displays are left as they are");
                    continue;
                }
                toSwitchOn.Add((saved, target));
                notes.Add($"{saved.FriendlyName}: {offMatch.Detail}; connected but not in the layout, so it is switched on");
                continue;
            }

            if (saved.IsIndirect)
            {
                // A missing indirect display never blocks a switch, whether or not it switches with the modes.
                notes.Add(Switched(saved)
                    ? $"{saved.FriendlyName}: not connected, so it stays off"
                    : $"{saved.FriendlyName}: not connected; indirect displays are left as they are");
                continue;
            }

            issues.Add(new(saved.FriendlyName,
                offMatch.Detail.StartsWith("ambiguous", StringComparison.Ordinal) ? offMatch.Detail : match.Detail));
        }

        if (issues.Count > 0) return new(null, expected, issues, notes);

        // ---- 2. Give every display being switched on a free source -------------------------
        var switchOn = AssignSources(toSwitchOn, kept, liveActive, issues, Switched);
        if (issues.Count > 0) return new(null, expected, issues, notes);

        // ---- 3. Build, in profile order ------------------------------------------------------
        var carriedPaths = new List<DisplayPathRecord>();
        foreach (var saved in profile.Paths)
        {
            if (kept.TryGetValue(saved, out var live))
            {
                // An indirect display the user has not opted into managing is carried through with
                // its LIVE state, even when the saved profile captured it as a managed display.
                // This matters for profiles captured before indirect displays were understood: the
                // incident's Desktop profile holds the DisplayLink Prompter as an ordinary path, and
                // without this branch switching to Desktop would actively re-add it.
                if (live.IsIndirect && !Switched(live) && !Switched(saved))
                {
                    var carriedMatch = ClonePath(live);
                    AddPath(liveActive, live, carriedMatch, submission, preferSavedTimings: true);
                    expected.Add(carriedMatch);
                    carriedPaths.Add(carriedMatch);
                    notes.Add($"{live.FriendlyName}: carried through unchanged; this mode's saved layout for it is ignored");
                    continue;
                }

                var path = Rebind(saved, live);
                if (path.IsIndirect) path.IndirectOptIn = true;   // it is here because it switches with the modes
                AddPath(profile, saved, path, submission, preferSavedTimings: !live.IsIndirect);
                expected.Add(path);
                continue;
            }

            if (switchOn.TryGetValue(saved, out var assignment))
            {
                var candidate = assignment.Target.AsCandidate();
                candidate.SourceId = assignment.SourceId;
                var path = Rebind(saved, candidate);
                if (path.IsIndirect) path.IndirectOptIn = true;
                AddPath(profile, saved, path, submission, preferSavedTimings: !assignment.Target.IsIndirect);
                expected.Add(path);
            }
        }

        // Carry-through. Order matters only for readability; Windows does not care.
        foreach (var livePath in liveActive.Paths)
        {
            if (!livePath.IsIndirect || Switched(livePath)) continue;
            if (kept.Values.Any(matched => ReferenceEquals(matched, livePath))) continue;
            if (submission.Paths.Any(existing => DisplayIdentityMatcher.SameDisplay(existing, livePath))) continue;

            var carried = ClonePath(livePath);
            AddPath(liveActive, livePath, carried, submission, preferSavedTimings: true);
            expected.Add(carried);
            carriedPaths.Add(carried);
            notes.Add($"{livePath.FriendlyName}: carried through unchanged (indirect display, not managed by this mode)");
        }

        SeparateCarried(submission, carriedPaths, notes);
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
    /// Picks a source for each monitor being switched on. Sources stay taken by displays that
    /// remain on and by carried-through indirect displays; sources of displays leaving the layout
    /// are free again. The source a display had when the layout was captured is preferred, which
    /// keeps Windows' display numbering stable. Saved displays that shared a source were a clone
    /// group and share one again.
    /// </summary>
    private static Dictionary<DisplayPathRecord, (ConnectedTarget Target, uint SourceId)> AssignSources(
        List<(DisplayPathRecord Saved, ConnectedTarget Target)> toSwitchOn,
        Dictionary<DisplayPathRecord, DisplayPathRecord> kept, DisplaySnapshot liveActive, List<SubmissionIssue> issues,
        Func<DisplayPathRecord, bool> switched)
    {
        var assigned = new Dictionary<DisplayPathRecord, (ConnectedTarget Target, uint SourceId)>(ReferenceEqualityComparer.Instance);
        if (toSwitchOn.Count == 0) return assigned;

        var used = new HashSet<(long Adapter, uint Id)>();
        foreach (var live in kept.Values) used.Add((live.SourceAdapter, live.SourceId));
        foreach (var live in liveActive.Paths.Where(path => path.IsIndirect && !switched(path)))
            used.Add((live.SourceAdapter, live.SourceId));

        var groups = new Dictionary<(long Adapter, uint Id), (long Adapter, uint Id)>();
        foreach (var (saved, live) in kept) groups.TryAdd((saved.SourceAdapter, saved.SourceId), (live.SourceAdapter, live.SourceId));

        foreach (var (saved, target) in toSwitchOn)
        {
            var group = (saved.SourceAdapter, saved.SourceId);
            uint? source = null;
            if (groups.TryGetValue(group, out var shared) && shared.Adapter == target.Adapter && target.SourceIds.Contains(shared.Id))
            {
                source = shared.Id;
            }
            else
            {
                if (target.SourceIds.Contains(saved.SourceId) && !used.Contains((target.Adapter, saved.SourceId)))
                    source = saved.SourceId;
                else
                    foreach (var candidate in target.SourceIds.OrderBy(id => id))
                        if (!used.Contains((target.Adapter, candidate))) { source = candidate; break; }

                if (source is uint picked)
                {
                    used.Add((target.Adapter, picked));
                    groups.TryAdd(group, (target.Adapter, picked));
                }
            }

            if (source is null)
            {
                issues.Add(new(saved.FriendlyName, "the graphics card has no free output left to drive this display"));
                continue;
            }
            assigned[saved] = (target, source.Value);
        }
        return assigned;
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
        // Everything submitted is meant to be on. A path without this flag is ignored by
        // SetDisplayConfig, which would silently drop a monitor that is being switched on.
        destinationPath.StatusFlags |= N.DISPLAYCONFIG_PATH_ACTIVE;
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

    /// <summary>
    /// A display carried through keeps its live position. That was right for the layout being
    /// left, but it can overlap a display the new layout places, and Windows does not allow
    /// overlaps: it resolves them itself, moving whichever display it chooses. On 2026-09-26 the
    /// Desktop layout, captured before the Prompter was plugged back in, overlapped the carried
    /// Prompter by 33 pixels; Windows moved other monitors instead, and the switch was rolled back.
    /// So the carried display is moved the shortest distance that clears everything, as Windows
    /// would, but predictably and before anything is applied.
    /// </summary>
    internal static void SeparateCarried(DisplaySnapshot submission, IReadOnlyList<DisplayPathRecord> carried, List<string> notes)
    {
        foreach (var path in carried)
        {
            var mode = SourceModeOf(submission, path);
            if (mode is null) continue;
            var others = submission.Paths
                .Where(other => !ReferenceEquals(other, path))
                .Select(other => SourceModeOf(submission, other))
                .Where(other => other is not null && !ReferenceEquals(other, mode))
                .Select(other => other!)
                .Distinct()
                .ToList();

            var (movedX, movedY) = (0, 0);
            for (var round = 0; round < 4; round++)
            {
                var hits = others.Where(other => Overlaps(Rect(mode), Rect(other))).ToList();
                if (hits.Count == 0) break;
                // For each display it overlaps, the four ways out: beside it, above it, below it.
                var moves = hits.SelectMany(other => new[]
                    {
                        (X: other.PositionX - (mode.PositionX + (int)mode.Width), Y: 0),
                        (X: other.PositionX + (int)other.Width - mode.PositionX, Y: 0),
                        (X: 0, Y: other.PositionY - (mode.PositionY + (int)mode.Height)),
                        (X: 0, Y: other.PositionY + (int)other.Height - mode.PositionY),
                    })
                    .OrderBy(move => Math.Abs(move.X) + Math.Abs(move.Y))
                    .ToList();
                // The shortest move that clears everything; failing that, the shortest move, and
                // the next round deals with what it runs into.
                var clear = moves.Where(move => others.All(other => !Overlaps(Rect(mode, move.X, move.Y), Rect(other)))).ToList();
                var move = clear.Count > 0 ? clear[0] : moves[0];
                mode.PositionX += move.X;
                mode.PositionY += move.Y;
                movedX += move.X;
                movedY += move.Y;
            }
            if (movedX != 0 || movedY != 0)
                notes.Add($"{path.FriendlyName}: moved {movedX},{movedY} so it does not overlap the displays around it");
        }
    }

    private static DisplayModeRecord? SourceModeOf(DisplaySnapshot snapshot, DisplayPathRecord path) =>
        path.SourceModeInfoIdx != N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && path.SourceModeInfoIdx < snapshot.Modes.Count &&
        snapshot.Modes[(int)path.SourceModeInfoIdx].InfoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
            ? snapshot.Modes[(int)path.SourceModeInfoIdx]
            : null;

    private static (long Left, long Top, long Right, long Bottom) Rect(DisplayModeRecord mode, int dx = 0, int dy = 0) =>
        (mode.PositionX + dx, mode.PositionY + dy, mode.PositionX + dx + (long)mode.Width, mode.PositionY + dy + (long)mode.Height);

    private static bool Overlaps((long Left, long Top, long Right, long Bottom) a, (long Left, long Top, long Right, long Bottom) b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

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
