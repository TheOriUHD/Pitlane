using System.Runtime.InteropServices;
using SimRigMode.Models;
using SimRigMode.Services.Display;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services;

public sealed class DisplayService
{
    public DisplaySnapshot SetDisplayIncluded(DisplaySnapshot? profile, DisplaySnapshot current, DisplayPathRecord display, bool included)
    {
        var selected = profile?.Paths.Select(ClonePath).ToList() ?? [];
        var existing = selected.FirstOrDefault(path => SameDisplay(path, display));
        if (included && existing is null) selected.Add(ClonePath(display));
        if (!included && existing is not null) selected.Remove(existing);

        var rebuilt = new DisplaySnapshot { CapturedUtc = DateTime.UtcNow };
        foreach (var selectedPath in selected)
        {
            var livePath = current.Paths.FirstOrDefault(path => SameDisplay(path, selectedPath));
            if (livePath is not null) AddPath(current, livePath, rebuilt);
            else if (profile is not null) AddPath(profile, selectedPath, rebuilt);
        }
        NormalizePrimary(rebuilt);
        return rebuilt;
    }

    /// <summary>Bounded retries for the sizing/query race below.</summary>
    private const int CaptureAttempts = 5;

    public DisplaySnapshot CaptureCurrent()
    {
        // GetDisplayConfigBufferSizes and QueryDisplayConfig are two separate calls. If the
        // topology changes between them - exactly what a dock or an indirect display does when
        // it re-enumerates - the query returns ERROR_INSUFFICIENT_BUFFER. Retry BOTH calls: a
        // topology that grew needs new sizes, not just a larger array.
        uint pc = 0, mc = 0;
        N.DISPLAYCONFIG_PATH_INFO[] paths;
        N.DISPLAYCONFIG_MODE_INFO[] modes;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var sizing = N.GetDisplayConfigBufferSizes(N.QDC_ONLY_ACTIVE_PATHS, out pc, out mc);
            if (sizing != 0) throw new InvalidOperationException($"Display configuration sizing failed (Win32 {sizing}).");
            paths = new N.DISPLAYCONFIG_PATH_INFO[pc];
            modes = new N.DISPLAYCONFIG_MODE_INFO[mc];
            var error = N.QueryDisplayConfig(N.QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (error == 0) break;
            if (error == N.ERROR_INSUFFICIENT_BUFFER && attempt < CaptureAttempts) continue;
            throw new InvalidOperationException($"Display configuration capture failed (Win32 {error}) after {attempt} attempt(s).");
        }
        var result = new DisplaySnapshot();
        // One GET_ADAPTER_NAME per distinct adapter, not per path.
        var adapterPaths = new Dictionary<long, string>();
        foreach (var p in paths.Take((int)pc))
        {
            var target = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id); var source = GetSourceName(p.sourceInfo.adapterId, p.sourceInfo.id);
            var adapterKey = p.targetInfo.adapterId.Value;
            if (!adapterPaths.TryGetValue(adapterKey, out var adapterPath))
                adapterPaths[adapterKey] = adapterPath = GetAdapterDevicePath(p.targetInfo.adapterId);
            var verdict = IndirectDisplayDetector.Classify(adapterPath, p.targetInfo.outputTechnology);
            result.Paths.Add(new()
            {
                AdapterDevicePath = adapterPath,
                EdidManufactureId = target.edidManufactureId, EdidProductCodeId = target.edidProductCodeId,
                ConnectorInstance = target.connectorInstance, EdidIdsValid = target.edidIdsValid,
                IsIndirect = verdict.IsIndirect, IndirectReason = verdict.Reason,
                SourceAdapter = p.sourceInfo.adapterId.Value, SourceId = p.sourceInfo.id, SourceModeInfoIdx = p.sourceInfo.modeInfoIdx,
                TargetAdapter = p.targetInfo.adapterId.Value, TargetId = p.targetInfo.id, TargetModeInfoIdx = p.targetInfo.modeInfoIdx,
                OutputTechnology = p.targetInfo.outputTechnology, Rotation = p.targetInfo.rotation, Scaling = p.targetInfo.scaling,
                RefreshNumerator = p.targetInfo.refreshRate.Numerator, RefreshDenominator = p.targetInfo.refreshRate.Denominator,
                ScanLineOrdering = p.targetInfo.scanLineOrdering, TargetAvailable = p.targetInfo.IsTargetAvailable, StatusFlags = p.flags,
                FriendlyName = string.IsNullOrWhiteSpace(target.friendly) ? source : target.friendly, MonitorDevicePath = target.path, SourceName = source
            });
        }
        foreach (var m in modes.Take((int)mc))
        {
            var r = new DisplayModeRecord { InfoType = m.infoType, Id = m.id, Adapter = m.adapterId.Value };
            if (m.infoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) { r.Width = m.sourceMode.width; r.Height = m.sourceMode.height; r.PixelFormat = m.sourceMode.pixelFormat; r.PositionX = m.sourceMode.position.x; r.PositionY = m.sourceMode.position.y; }
            else if (m.infoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET) { r.PixelRate = m.targetMode.pixelRate; r.HSyncNumerator = m.targetMode.hSyncFreq.Numerator; r.HSyncDenominator = m.targetMode.hSyncFreq.Denominator; r.VSyncNumerator = m.targetMode.vSyncFreq.Numerator; r.VSyncDenominator = m.targetMode.vSyncFreq.Denominator; r.ActiveWidth = m.targetMode.activeSize.cx; r.ActiveHeight = m.targetMode.activeSize.cy; r.TotalWidth = m.targetMode.totalSize.cx; r.TotalHeight = m.targetMode.totalSize.cy; r.VideoStandard = m.targetMode.videoStandard; r.ScanLineOrdering = m.targetMode.scanLineOrdering; }
            // DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE (3) carries neither a source mode nor
            // target timings. The previous code had no third branch and reinterpreted its 40
            // bytes of rectangle data as target timings. Record the type and leave the payload
            // empty instead. Unreachable today because we never query virtual-mode-aware, but it
            // arms itself the moment anyone adds that flag.
            result.Modes.Add(r);
        }
        return result;
    }

    /// <summary>
    /// Re-applies the layout currently on screen and asks Windows to REMEMBER it. This is the
    /// only place SDC_SAVE_TO_DATABASE is used, it is reachable only from an explicit button, and
    /// mode switching never calls it. It exists so the persisted display database can be repaired
    /// deliberately - including cleaning up what the incident wrote into it.
    /// </summary>
    public int PersistCurrentTopologyAsWindowsDefault()
    {
        var live = CaptureCurrent();
        return Submit(live, N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_APPLY | N.SDC_SAVE_TO_DATABASE | N.SDC_ALLOW_CHANGES);
    }

    /// <summary>
    /// Submits a topology and returns the raw Win32 result. The flags are supplied by the
    /// caller, which is what lets DisplayApplyEngine validate before it applies and roll back
    /// afterwards. Note there is no overload that defaults to SDC_SAVE_TO_DATABASE: persisting
    /// is a deliberate, separate user action now. See docs/report.md.
    /// </summary>
    public int Submit(DisplaySnapshot snapshot, uint sdcFlags)
    {
        if (snapshot.Paths.Count == 0) throw new InvalidOperationException("This mode has no captured display layout.");
        ValidateModeIndices(snapshot);
        var paths = snapshot.Paths.Select(p => new N.DISPLAYCONFIG_PATH_INFO
        {
            sourceInfo = new() { adapterId = N.LUID.From(p.SourceAdapter), id = p.SourceId, modeInfoIdx = p.SourceModeInfoIdx },
            // targetAvailable is output-only and is deliberately submitted as 0.
            targetInfo = new() { adapterId = N.LUID.From(p.TargetAdapter), id = p.TargetId, modeInfoIdx = p.TargetModeInfoIdx, outputTechnology = p.OutputTechnology, rotation = p.Rotation, scaling = p.Scaling, refreshRate = new() { Numerator = p.RefreshNumerator, Denominator = p.RefreshDenominator }, scanLineOrdering = p.ScanLineOrdering },
            // Mask rather than replay. A captured DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE bit
            // submitted without SDC_VIRTUAL_MODE_AWARE makes Windows read modeInfoIdx under the
            // packed-bitfield convention, i.e. as garbage indices.
            flags = p.StatusFlags & N.SUBMITTABLE_PATH_FLAGS
        }).ToArray();
        var modes = snapshot.Modes.Select(m =>
        {
            var n = new N.DISPLAYCONFIG_MODE_INFO { infoType = m.InfoType, id = m.Id, adapterId = N.LUID.From(m.Adapter) };
            if (m.InfoType == 1) n.sourceMode = new() { width = m.Width, height = m.Height, pixelFormat = m.PixelFormat, position = new() { x = m.PositionX, y = m.PositionY } };
            else n.targetMode = new() { pixelRate = m.PixelRate, hSyncFreq = new() { Numerator = m.HSyncNumerator, Denominator = m.HSyncDenominator }, vSyncFreq = new() { Numerator = m.VSyncNumerator, Denominator = m.VSyncDenominator }, activeSize = new() { cx = m.ActiveWidth, cy = m.ActiveHeight }, totalSize = new() { cx = m.TotalWidth, cy = m.TotalHeight }, videoStandard = m.VideoStandard, scanLineOrdering = m.ScanLineOrdering };
            return n;
        }).ToArray();
        return N.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, sdcFlags);
    }

    /// <summary>
    /// A saved snapshot can disagree with its own mode array - a hand-edited config, a partial
    /// write, or a profile whose modes were trimmed. Submitting an out-of-range index is a
    /// native buffer read, so refuse before the array ever reaches SetDisplayConfig.
    /// </summary>
    private static void ValidateModeIndices(DisplaySnapshot snapshot)
    {
        foreach (var path in snapshot.Paths)
        {
            Check(path.SourceModeInfoIdx, "source");
            Check(path.TargetModeInfoIdx, "target");
        }

        void Check(uint index, string which)
        {
            if (index == N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) return;
            if (index >= snapshot.Modes.Count)
                throw new InvalidOperationException(
                    $"The captured display layout is inconsistent: a path references {which} mode {index}, " +
                    $"but the layout only holds {snapshot.Modes.Count} mode(s). Re-capture this mode's layout.");
        }
    }

    private static uint ImportMode(DisplaySnapshot source, uint index, DisplaySnapshot destination)
    {
        if (index == uint.MaxValue || index >= source.Modes.Count) return uint.MaxValue;
        var mode = source.Modes[(int)index];
        var existing = destination.Modes.FindIndex(candidate => candidate.InfoType == mode.InfoType && candidate.Adapter == mode.Adapter && candidate.Id == mode.Id);
        if (existing >= 0) return (uint)existing;
        destination.Modes.Add(CloneMode(mode)); return (uint)(destination.Modes.Count - 1);
    }

    private static void AddPath(DisplaySnapshot source, DisplayPathRecord path, DisplaySnapshot destination)
    {
        var added = ClonePath(path);
        added.SourceModeInfoIdx = ImportMode(source, path.SourceModeInfoIdx, destination);
        added.TargetModeInfoIdx = ImportMode(source, path.TargetModeInfoIdx, destination);
        added.DesktopModeInfoIdx = path.DesktopModeInfoIdx == uint.MaxValue ? uint.MaxValue : ImportMode(source, path.DesktopModeInfoIdx, destination);
        destination.Paths.Add(added);
    }

    private static void NormalizePrimary(DisplaySnapshot snapshot)
    {
        var sourceModes = snapshot.Paths.Select(path => path.SourceModeInfoIdx)
            .Where(index => index != uint.MaxValue && index < snapshot.Modes.Count)
            .Select(index => snapshot.Modes[(int)index]).Distinct().ToList();
        if (sourceModes.Count == 0 || sourceModes.Any(mode => mode.PositionX == 0 && mode.PositionY == 0)) return;
        var anchor = sourceModes.MinBy(mode => (long)mode.PositionX * mode.PositionX + (long)mode.PositionY * mode.PositionY)!;
        var offsetX = anchor.PositionX; var offsetY = anchor.PositionY;
        foreach (var mode in sourceModes) { mode.PositionX -= offsetX; mode.PositionY -= offsetY; }
    }

    private static DisplayPathRecord ClonePath(DisplayPathRecord path) => new()
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
    // Single definition, shared with the builder, the verifier and the settings window. This
    // comparison previously existed in three separate copies.
    private static bool SameDisplay(DisplayPathRecord left, DisplayPathRecord right) =>
        DisplayIdentityMatcher.SameDisplay(left, right);

    private readonly record struct TargetName(string friendly, string path, ushort edidManufactureId,
        ushort edidProductCodeId, uint connectorInstance, bool edidIdsValid);

    private static TargetName GetTargetName(N.LUID adapter, uint id)
    {
        var req = new N.DISPLAYCONFIG_TARGET_DEVICE_NAME { header = new() { type = N.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, size = (uint)Marshal.SizeOf<N.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = adapter, id = id }, monitorFriendlyDeviceName = "", monitorDevicePath = "" };
        if (N.DisplayConfigGetDeviceInfo(ref req) != 0) return new("", "", 0, 0, 0, false);
        // The EDID ids are only meaningful when Windows says so. Previously everything except
        // the friendly name and device path was discarded.
        var edidValid = (req.flags & N.TARGET_NAME_FLAG_EDID_IDS_VALID) != 0;
        return new(req.monitorFriendlyDeviceName, req.monitorDevicePath,
            edidValid ? req.edidManufactureId : (ushort)0, edidValid ? req.edidProductCodeId : (ushort)0,
            req.connectorInstance, edidValid);
    }

    /// <summary>
    /// The adapter's device path. \\?\PCI#... identifies a real graphics adapter; anything else
    /// is an indirect display driver. Returns "" on failure, which
    /// <see cref="IndirectDisplayDetector"/> treats as indirect by design.
    /// </summary>
    private static string GetAdapterDevicePath(N.LUID adapter)
    {
        var req = new N.DISPLAYCONFIG_ADAPTER_NAME { header = new() { type = N.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME, size = (uint)Marshal.SizeOf<N.DISPLAYCONFIG_ADAPTER_NAME>(), adapterId = adapter, id = 0 }, adapterDevicePath = "" };
        return N.DisplayConfigGetDeviceInfo(ref req) == 0 ? req.adapterDevicePath : "";
    }
    private static string GetSourceName(N.LUID adapter, uint id)
    {
        var req = new N.DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = new() { type = N.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<N.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = adapter, id = id }, viewGdiDeviceName = "" };
        return N.DisplayConfigGetDeviceInfo(ref req) == 0 ? req.viewGdiDeviceName : "Display";
    }
}
