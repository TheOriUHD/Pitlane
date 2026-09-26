using System.Text.Json;
using SimRigMode.Models;
using SimRigMode.Services.Usb;

namespace SimRigMode.Services.Display;

/// <summary>A parked monitor and where it is on the desktop, in physical pixels.</summary>
public sealed record ParkedScreen(string MonitorDevicePath, string FriendlyName, int X, int Y, int Width, int Height)
{
    public PixelRect Bounds => new(X, Y, Width, Height);
}

/// <summary>A monitor to park, with the snapshot that holds its source and target modes.</summary>
public sealed record ParkedDisplay(DisplayPathRecord Path, DisplaySnapshot ModeSource);

/// <summary>
/// Monitors that are PARKED when a mode leaves them out: kept attached to the desktop in a corner
/// where they touch it at a single point, instead of being detached. SimRigMode never switches
/// them off; their own power button does.
///
/// Measured on 2026-09-24: detached from the desktop, the PG27AQDP (HDMI) drops off the bus and
/// reconnects every 7.6 s, and every reconnect stalls Windows' input and window handling. It did
/// so switched on, in standby and switched off, though not every time it was off. Attached, it
/// never drops out. Detaching is therefore not a safe way to leave such a monitor out.
/// See docs/report.md.
/// </summary>
public static class DisplayPowerPolicy
{
    public const int MaxEntries = 32;

    /// <summary>
    /// Listed monitors that <paramref name="profile"/> leaves out but that are connected, each with
    /// a known mode to park it in: its live mode if it is attached now, otherwise the mode from a
    /// saved layout. A monitor with no known mode, or an indirect one, is never parked.
    /// </summary>
    public static IReadOnlyList<ParkedDisplay> PlanParking(AppConfig config, DisplaySnapshot profile,
        DisplaySnapshot live, IReadOnlyList<ConnectedTarget> connected)
    {
        var parked = new List<ParkedDisplay>();
        foreach (var entry in config.General.SwitchOffWhenUnused)
        {
            var monitor = entry.MonitorDevicePath;
            if (profile.Paths.Any(path => Same(path.MonitorDevicePath, monitor))) continue;

            var attached = live.Paths.FirstOrDefault(path => Same(path.MonitorDevicePath, monitor));
            if (attached is not null)
            {
                if (!attached.IsIndirect) parked.Add(new(attached, live));
                continue;
            }

            var target = connected.FirstOrDefault(candidate => Same(candidate.MonitorDevicePath, monitor));
            if (target is null || target.IsIndirect) continue;

            foreach (var saved in new[] { config.SimRig.Displays, config.Desktop.Displays })
            {
                var path = saved?.Paths.FirstOrDefault(candidate => Same(candidate.MonitorDevicePath, monitor));
                if (path is null) continue;
                parked.Add(new(path, saved!));
                break;
            }
        }
        return parked;
    }

    /// <summary>
    /// <paramref name="profile"/> plus every parked monitor, each at a corner where it touches the
    /// rest of the desktop at a single point, keeping its own resolution and timing.
    /// </summary>
    public static DisplaySnapshot WithParked(DisplaySnapshot profile, IReadOnlyList<ParkedDisplay> parked)
    {
        var result = Clone(profile);
        foreach (var park in parked)
        {
            var modes = park.ModeSource.Modes;
            var sourceIndex = park.Path.SourceModeInfoIdx;
            if (sourceIndex >= modes.Count || modes[(int)sourceIndex].InfoType != 1) continue;

            var source = Clone(modes[(int)sourceIndex]);
            (source.PositionX, source.PositionY) = ParkingPosition(result, source.Width, source.Height);
            var path = Clone(park.Path);
            result.Modes.Add(source);
            path.SourceModeInfoIdx = (uint)(result.Modes.Count - 1);

            var targetIndex = park.Path.TargetModeInfoIdx;
            if (targetIndex < modes.Count && modes[(int)targetIndex].InfoType == 2)
            {
                result.Modes.Add(Clone(modes[(int)targetIndex]));
                path.TargetModeInfoIdx = (uint)(result.Modes.Count - 1);
            }
            else path.TargetModeInfoIdx = uint.MaxValue;

            result.Paths.Add(path);
        }
        return result;
    }

    /// <summary>
    /// Where to park a <paramref name="width"/> x <paramref name="height"/> monitor: a spot that
    /// touches the desktop at exactly one corner and overlaps nothing, so the mouse practically
    /// never crosses into it. The primary display's corners are tried first, bottom-right first,
    /// then every other display's. If nothing fits, it goes to the right of the rightmost display,
    /// which the mouse can reach but which still stops the dropouts.
    /// </summary>
    public static (int X, int Y) ParkingPosition(DisplaySnapshot layout, uint width, uint height)
    {
        var rects = layout.Paths.Select(path => SourceRect(layout, path)).OfType<Rect>().Distinct().ToList();
        if (rects.Count == 0) return (0, 0);
        int w = (int)width, h = (int)height;

        foreach (var anchor in rects.OrderBy(rect => rect.X == 0 && rect.Y == 0 ? 0 : 1))
        {
            var corners = new[]
            {
                (X: anchor.Right, Y: anchor.Bottom), (X: anchor.Right, Y: anchor.Y - h),
                (X: anchor.X - w, Y: anchor.Bottom), (X: anchor.X - w, Y: anchor.Y - h)
            };
            foreach (var (x, y) in corners)
            {
                var candidate = new Rect(x, y, w, h);
                if (!rects.Any(other => Overlaps(candidate, other) || SharesEdge(candidate, other))) return (x, y);
            }
        }

        var rightmost = rects.MaxBy(rect => rect.Right);
        return (rightmost.Right, rightmost.Y);
    }

    /// <summary>
    /// The monitors Pitlane parked that are still parked: in the layout, and out of the mouse's
    /// reach, touching no other display along an edge. One the user has since moved next to
    /// another display in Windows' settings is theirs again, and is left alone.
    /// </summary>
    public static IReadOnlyList<ParkedScreen> LocateParked(DisplaySnapshot live, IEnumerable<string> parkedMonitorPaths)
    {
        var parked = parkedMonitorPaths.Where(IsPlausibleMonitorPath).ToList();
        if (parked.Count == 0) return [];
        var displays = live.Paths
            .Select(path => (Path: path, Rect: SourceRect(live, path)))
            .Where(display => display.Rect is not null)
            .Select(display => (display.Path, Rect: display.Rect!.Value))
            .ToList();

        var screens = new List<ParkedScreen>();
        foreach (var (path, rect) in displays)
        {
            if (!parked.Any(entry => Same(entry, path.MonitorDevicePath))) continue;
            var reachable = displays.Any(other => !ReferenceEquals(other.Path, path) && other.Rect != rect &&
                                                  (Overlaps(rect, other.Rect) || SharesEdge(rect, other.Rect)));
            if (!reachable) screens.Add(new(path.MonitorDevicePath, path.FriendlyName, rect.X, rect.Y, rect.W, rect.H));
        }
        return screens;
    }

    /// <summary>Whether the modes switch this indirect display, rather than carrying it through.</summary>
    public static bool IsSwitchedWithModes(GeneralConfig general, string monitorDevicePath) =>
        general.SwitchedIndirectDisplays.Any(path => Same(path, monitorDevicePath));

    public static void SetSwitchedWithModes(GeneralConfig general, string monitorDevicePath, bool switched)
    {
        general.SwitchedIndirectDisplays.RemoveAll(path => Same(path, monitorDevicePath));
        if (switched && IsPlausibleMonitorPath(monitorDevicePath) && general.SwitchedIndirectDisplays.Count < MaxEntries)
            general.SwitchedIndirectDisplays.Add(monitorDevicePath);
    }

    public static bool IsListed(GeneralConfig general, string monitorDevicePath) =>
        !string.IsNullOrWhiteSpace(monitorDevicePath) &&
        general.SwitchOffWhenUnused.Any(entry => Same(entry.MonitorDevicePath, monitorDevicePath));

    /// <returns>True when the monitor was newly added.</returns>
    public static bool Remember(GeneralConfig general, string monitorDevicePath, string friendlyName, string reason)
    {
        if (!IsPlausibleMonitorPath(monitorDevicePath) || IsListed(general, monitorDevicePath)) return false;
        if (general.SwitchOffWhenUnused.Count >= MaxEntries) return false;
        general.SwitchOffWhenUnused.Add(new DisplayPowerOffEntry
        {
            MonitorDevicePath = monitorDevicePath,
            FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? MonitorId(monitorDevicePath) : friendlyName.Trim(),
            Reason = reason,
            AddedUtc = DateTime.UtcNow
        });
        return true;
    }

    public static bool Forget(GeneralConfig general, string monitorDevicePath)
    {
        general.ParkedDisplays.RemoveAll(path => Same(path, monitorDevicePath));
        return general.SwitchOffWhenUnused.RemoveAll(entry => Same(entry.MonitorDevicePath, monitorDevicePath)) > 0;
    }

    public static bool IsParked(GeneralConfig general, string monitorDevicePath) =>
        general.ParkedDisplays.Any(path => Same(path, monitorDevicePath));

    /// <summary>The on-disk config is hand-editable and importable, so these lists are checked
    /// like any other input from outside the process.</summary>
    public static IReadOnlyList<SanitizerFinding> Sanitize(GeneralConfig general)
    {
        var findings = new List<SanitizerFinding>();
        general.SwitchOffWhenUnused ??= [];
        general.ParkedDisplays ??= [];
        general.SwitchedIndirectDisplays ??= [];
        var switched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        general.SwitchedIndirectDisplays.RemoveAll(path => !IsPlausibleMonitorPath(path) || !switched.Add(path));
        if (general.SwitchedIndirectDisplays.Count > MaxEntries)
            general.SwitchedIndirectDisplays.RemoveRange(MaxEntries, general.SwitchedIndirectDisplays.Count - MaxEntries);
        var list = general.SwitchOffWhenUnused;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry is not null && IsPlausibleMonitorPath(entry.MonitorDevicePath) && seen.Add(entry.MonitorDevicePath)) continue;
            findings.Add(new("BAD_DISPLAY_POWER_ENTRY", entry?.FriendlyName ?? "(empty)",
                "A monitor in the park list had no usable device path, or was listed twice. Dropped."));
            list.RemoveAt(i--);
        }
        if (list.Count > MaxEntries)
        {
            findings.Add(new("TOO_MANY_DISPLAY_POWER_ENTRIES", "(config)",
                $"{list.Count} monitors exceeded the limit of {MaxEntries}; the extras were dropped."));
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        }
        // Parked state only means something for a listed monitor.
        general.ParkedDisplays.RemoveAll(path => !IsPlausibleMonitorPath(path) || !seen.Contains(path));
        return findings;
    }

    /// <summary>A monitor interface path: \\?\DISPLAY#&lt;id&gt;#&lt;instance&gt;#{guid}.</summary>
    public static bool IsPlausibleMonitorPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 512 && !path.Any(char.IsControl) &&
        path.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase);

    /// <summary>"\\?\DISPLAY#AUS2740#..." gives "AUS2740", for when no friendly name is known.</summary>
    public static string MonitorId(string path)
    {
        var parts = (path ?? "").Split('#');
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "A monitor";
    }

    /// <summary>The friendliest known name for a monitor, from the park list or a saved layout.</summary>
    public static string Describe(AppConfig config, string monitorDevicePath)
    {
        var listed = config.General.SwitchOffWhenUnused
            .FirstOrDefault(entry => Same(entry.MonitorDevicePath, monitorDevicePath))?.FriendlyName;
        if (!string.IsNullOrWhiteSpace(listed)) return listed;
        var saved = new[] { config.Desktop.Displays, config.SimRig.Displays }
            .SelectMany(snapshot => snapshot?.Paths ?? [])
            .FirstOrDefault(path => Same(path.MonitorDevicePath, monitorDevicePath))?.FriendlyName;
        return string.IsNullOrWhiteSpace(saved) ? MonitorId(monitorDevicePath) : saved;
    }

    /// <summary>Whether the monitor belongs to the layout of the mode that is active now. One that
    /// does, and still keeps dropping out, has a connection problem rather than an idle one.</summary>
    public static bool IsInCurrentLayout(AppConfig config, string monitorDevicePath)
    {
        var mode = config.CurrentMode switch
        {
            WorkspaceMode.Desktop => config.Desktop,
            WorkspaceMode.SimRig => config.SimRig,
            _ => null
        };
        return mode?.Displays?.Paths.Any(path => Same(path.MonitorDevicePath, monitorDevicePath)) == true;
    }

    private readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
    }

    private static Rect? SourceRect(DisplaySnapshot layout, DisplayPathRecord path)
    {
        if (path.SourceModeInfoIdx >= layout.Modes.Count) return null;
        var mode = layout.Modes[(int)path.SourceModeInfoIdx];
        return mode.InfoType == 1 ? new Rect(mode.PositionX, mode.PositionY, (int)mode.Width, (int)mode.Height) : null;
    }

    private static bool Overlaps(Rect a, Rect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>Touching along a stretch of edge, rather than at a single corner point.</summary>
    private static bool SharesEdge(Rect a, Rect b) =>
        ((a.Right == b.X || b.Right == a.X) && a.Y < b.Bottom && b.Y < a.Bottom) ||
        ((a.Bottom == b.Y || b.Bottom == a.Y) && a.X < b.Right && b.X < a.Right);

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
