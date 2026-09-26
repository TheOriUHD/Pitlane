using SimRigMode.Models;
using SimRigMode.Services.Display;

namespace SimRigMode.Tests.Fakes;

/// <summary>
/// Builds DisplaySnapshot fixtures. Values mirror the real post-incident capture: the GPU on
/// adapter LUID 117063 and the DisplayLink-backed Elgato Prompter alone on 1582295, reporting
/// OutputTechnology 6 (LVDS).
/// </summary>
internal static class TopologyFixture
{
    public const long GpuAdapter = 117063;
    public const long PrompterAdapter = 1582295;

    public const string GpuAdapterPath = @"\\?\PCI#VEN_10DE&DEV_2684#4&2d9c1cbb&0&0008#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";
    public const string PrompterAdapterPath = @"\\?\ROOT#BASICDISPLAY#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";

    public const string PrompterMonitorPath = @"\\?\DISPLAY#IDI3311#8&d1abe6d&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    public const string RigMonitorPath = @"\\?\DISPLAY#AUS2740#5&1176aab&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    public const string DeskMonitorPath = @"\\?\DISPLAY#AUS27F5#5&1176aab&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    public static DisplaySnapshot Empty() => new();

    /// <summary>Appends one display, allocating its source and target mode entries.</summary>
    public static DisplaySnapshot With(this DisplaySnapshot snapshot, string name, string monitorPath,
        string adapterPath, long adapter, uint sourceId, uint targetId, uint width, uint height,
        int x = 0, int y = 0, bool indirect = false, bool optIn = false,
        uint outputTechnology = 10, uint refreshNumerator = 60000, uint refreshDenominator = 1000)
    {
        var sourceIndex = (uint)snapshot.Modes.Count;
        snapshot.Modes.Add(new DisplayModeRecord
        {
            InfoType = 1, Adapter = adapter, Id = sourceId,
            Width = width, Height = height, PixelFormat = 2, PositionX = x, PositionY = y
        });

        var targetIndex = (uint)snapshot.Modes.Count;
        snapshot.Modes.Add(new DisplayModeRecord
        {
            InfoType = 2, Adapter = adapter, Id = targetId,
            ActiveWidth = width, ActiveHeight = height, TotalWidth = width, TotalHeight = height
        });

        snapshot.Paths.Add(new DisplayPathRecord
        {
            FriendlyName = name,
            MonitorDevicePath = monitorPath,
            AdapterDevicePath = adapterPath,
            SourceAdapter = adapter, SourceId = sourceId, SourceModeInfoIdx = sourceIndex,
            TargetAdapter = adapter, TargetId = targetId, TargetModeInfoIdx = targetIndex,
            OutputTechnology = outputTechnology,
            Rotation = 1, Scaling = 1,
            RefreshNumerator = refreshNumerator, RefreshDenominator = refreshDenominator,
            StatusFlags = 1,
            IsIndirect = indirect, IndirectOptIn = optIn,
            IndirectReason = indirect ? "software display adapter" : ""
        });
        return snapshot;
    }

    /// <summary>The Elgato Prompter: indirect, LVDS, own adapter. 1024x600 as captured.</summary>
    public static DisplaySnapshot WithPrompter(this DisplaySnapshot snapshot, int x = -1024, bool optIn = false)
        => snapshot.With("Elgato Prom.", PrompterMonitorPath, PrompterAdapterPath, PrompterAdapter,
            sourceId: 0, targetId: 256, width: 1024, height: 600, x: x, y: 0,
            indirect: true, optIn: optIn, outputTechnology: 6);

    /// <summary>The sim rig panel: PG27AQDP at 2560x1440, ~479.989 Hz.</summary>
    public static DisplaySnapshot WithRigMonitor(this DisplaySnapshot snapshot, long adapter = GpuAdapter, uint sourceId = 3)
        => snapshot.With("PG27AQDP", RigMonitorPath, GpuAdapterPath, adapter,
            sourceId: sourceId, targetId: 4352, width: 2560, height: 1440,
            outputTechnology: 5, refreshNumerator: 479989, refreshDenominator: 1000);

    /// <summary>A desk panel: PG27UCDM at 3840x2160.</summary>
    public static DisplaySnapshot WithDeskMonitor(this DisplaySnapshot snapshot, long adapter = GpuAdapter, uint sourceId = 1)
        => snapshot.With("PG27UCDM", DeskMonitorPath, GpuAdapterPath, adapter,
            sourceId: sourceId, targetId: 4355, width: 3840, height: 2160,
            refreshNumerator: 240000, refreshDenominator: 1000);

    /// <summary>
    /// The same monitor as QDC_ALL_PATHS reports it: connected, and drivable from
    /// <paramref name="sources"/> - by default sources 0-3, like a four-head GPU, plus the
    /// source it had when this fixture was captured.
    /// </summary>
    public static ConnectedTarget ToConnected(this DisplayPathRecord path, params uint[] sources)
    {
        var ids = sources.Length > 0 ? sources.ToList() : [0u, 1u, 2u, 3u];
        if (sources.Length == 0 && !ids.Contains(path.SourceId)) ids.Add(path.SourceId);
        return new ConnectedTarget
        {
            Adapter = path.TargetAdapter, TargetId = path.TargetId, SourceIds = ids,
            MonitorDevicePath = path.MonitorDevicePath, FriendlyName = path.FriendlyName,
            AdapterDevicePath = path.AdapterDevicePath,
            EdidManufactureId = path.EdidManufactureId, EdidProductCodeId = path.EdidProductCodeId,
            ConnectorInstance = path.ConnectorInstance, EdidIdsValid = path.EdidIdsValid,
            OutputTechnology = path.OutputTechnology, IsIndirect = path.IsIndirect, IndirectReason = path.IndirectReason
        };
    }
}
