using SimRigMode.Models;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services.Display;

/// <summary>
/// One monitor Windows can drive right now, whether or not it is part of the layout on screen.
///
/// QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS) cannot see a monitor that is connected but switched
/// out of the layout. The apply pipeline only ever looked there, so every profile that needed to
/// switch a monitor ON was refused as "not connected" - switching from Sim Rig back to Desktop
/// could never have worked. QDC_ALL_PATHS can see them, and this is what it reports.
/// </summary>
public sealed class ConnectedTarget
{
    public long Adapter { get; set; }
    public uint TargetId { get; set; }

    /// <summary>True when the monitor is part of the layout on screen.</summary>
    public bool IsActive { get; set; }

    /// <summary>Sources on the same adapter that Windows reports can drive this monitor.</summary>
    public List<uint> SourceIds { get; set; } = [];

    public string MonitorDevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string AdapterDevicePath { get; set; } = "";
    public ushort EdidManufactureId { get; set; }
    public ushort EdidProductCodeId { get; set; }
    public uint ConnectorInstance { get; set; }
    public bool EdidIdsValid { get; set; }
    public uint OutputTechnology { get; set; }
    public bool IsIndirect { get; set; }
    public string IndirectReason { get; set; } = "";

    /// <summary>The same monitor as a path record, so the identity matcher can compare it with
    /// a saved display. It has no source yet; the builder assigns one.</summary>
    internal DisplayPathRecord AsCandidate() => new()
    {
        SourceAdapter = Adapter, TargetAdapter = Adapter, TargetId = TargetId,
        SourceModeInfoIdx = N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID, TargetModeInfoIdx = N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
        MonitorDevicePath = MonitorDevicePath, FriendlyName = FriendlyName, AdapterDevicePath = AdapterDevicePath,
        EdidManufactureId = EdidManufactureId, EdidProductCodeId = EdidProductCodeId, ConnectorInstance = ConnectorInstance,
        EdidIdsValid = EdidIdsValid, OutputTechnology = OutputTechnology, IsIndirect = IsIndirect, IndirectReason = IndirectReason,
        TargetAvailable = true
    };

    /// <summary>True when <paramref name="path"/> is this monitor.</summary>
    internal bool Is(DisplayPathRecord path) =>
        !string.IsNullOrWhiteSpace(MonitorDevicePath) && !string.IsNullOrWhiteSpace(path.MonitorDevicePath)
            ? MonitorDevicePath.Equals(path.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)
            : path.TargetAdapter == Adapter && path.TargetId == TargetId;
}
