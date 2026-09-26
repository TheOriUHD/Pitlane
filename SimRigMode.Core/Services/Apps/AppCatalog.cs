using System.Diagnostics;
using SimRigMode.Models;
using SimRigMode.NativeInterop;
using SimRigMode.Services.Usb;

namespace SimRigMode.Services.Apps;

public sealed record DiscoveredApp(string Name, string Path, string Arguments, bool IsSimRacing);

/// <summary>Which apps a mode switch starts and closes. Pure.</summary>
public static class AppTransitionPlanner
{
    public static IReadOnlyList<LinkedApp> ToStart(AppConfig config, WorkspaceMode target) =>
        Mode(config, target).LinkedApps.Where(app => app.StartWithMode).ToList();

    /// <summary>The other mode's apps that close when it ends - except any the target mode also
    /// uses, which would otherwise be closed and immediately started again.</summary>
    public static IReadOnlyList<LinkedApp> ToClose(AppConfig config, WorkspaceMode target)
    {
        var entering = Mode(config, target).LinkedApps;
        var leaving = Mode(config, target == WorkspaceMode.SimRig ? WorkspaceMode.Desktop : WorkspaceMode.SimRig).LinkedApps;
        return leaving
            .Where(app => app.CloseWhenLeaving)
            .Where(app => !entering.Any(kept => kept.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static ModeConfig Mode(AppConfig config, WorkspaceMode mode) =>
        mode == WorkspaceMode.SimRig ? config.SimRig : config.Desktop;
}

/// <summary>Recognising apps: sim racing tools, and Start menu clutter that is not an app. Pure.</summary>
public static class AppCatalog
{
    private static readonly string[] SimRacingMarkers =
    [
        "simhub", "pit house", "pithouse", "fanatec", "fanalab", "simucube", "truedrive", "racehub",
        "asetek", "g hub", "lghub", "thrustmaster", "heusinkveld", "smartcontrol", "simagic", "simpro",
        "cammus", "conspit", "directforce", "crewchief", "crew chief", "trading paints", "garage 61",
        "garage61", "content manager", "racelab", "iracing", "ioverlay", "kapps", "beamng", "assetto",
        "automobilista", "rfactor", "le mans ultimate", "raceroom", "sim racing studio", "simracingstudio",
        "buttkicker", "next level racing"
    ];

    private static readonly string[] NoiseMarkers =
    [
        "uninstall", "readme", "read me", "help", "manual", "documentation", "website", "release notes",
        "changelog", "license", "eula", "repair", "safe mode"
    ];

    public static bool IsSimRacing(string name, string path)
    {
        var text = (name + " " + System.IO.Path.GetFileNameWithoutExtension(path ?? "")).ToLowerInvariant();
        return SimRacingMarkers.Any(text.Contains);
    }

    public static bool IsNoise(string name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        return NoiseMarkers.Any(lower.Contains);
    }

    /// <summary>A full path to a program, which is all a linked app may be.</summary>
    public static bool IsProgramPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 512 && !path.Any(char.IsControl) &&
        System.IO.Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Worth offering in the app list: a program outside the Windows folder.</summary>
    public static bool IsOfferable(string? path, string windowsDirectory) =>
        IsProgramPath(path) &&
        !path!.StartsWith(windowsDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sim racing tools first, then alphabetical.</summary>
    public static IReadOnlyList<DiscoveredApp> Order(IEnumerable<DiscoveredApp> apps) =>
        apps.OrderByDescending(app => app.IsSimRacing).ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Finds installed apps from the Start menu and from what is running, so nobody has to
/// hunt for an .exe. Read-only.</summary>
public sealed class AppDiscoveryService
{
    public IReadOnlyList<DiscoveredApp> Discover()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessPath ?? "";
        var found = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
            catch { continue; }
            foreach (var shortcut in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (AppCatalog.IsNoise(name)) continue;
                if (ProcessNative.TryReadShortcut(shortcut) is not { } link) continue;
                if (!AppCatalog.IsOfferable(link.Target, windows) || !File.Exists(link.Target)) continue;
                found.TryAdd(link.Target, new(name, link.Target, link.Arguments, AppCatalog.IsSimRacing(name, link.Target)));
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = ProcessNative.TryGetImagePath(process.Id);
                    if (path is null || found.ContainsKey(path) || !AppCatalog.IsOfferable(path, windows)) continue;
                    if (path.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = FriendlyName(path, process.ProcessName);
                    found.TryAdd(path, new(name, path, "", AppCatalog.IsSimRacing(name, path)));
                }
                catch { /* exited while we looked */ }
            }
        }

        return AppCatalog.Order(found.Values);
    }

    public static string FriendlyName(string path, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }
        catch { return fallback; }
    }
}

/// <summary>
/// Linked apps come from the on-disk config or an imported file, and a mode switch STARTS them,
/// so they are checked like any other input from outside the process. An imported file never
/// starts anything until the user has looked at it and turned its apps on.
/// </summary>
public static class LinkedAppSanitizer
{
    public const int MaxAppsPerMode = 32;

    public static IReadOnlyList<SanitizerFinding> Sanitize(AppConfig config, bool imported)
    {
        var findings = new List<SanitizerFinding>();
        foreach (var mode in new[] { config.Desktop, config.SimRig })
        {
            mode.LinkedApps ??= [];
            var apps = mode.LinkedApps;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                var arguments = app?.Arguments ?? "";
                if (app is not null && AppCatalog.IsProgramPath(app.Path) && arguments.Length <= 2048 &&
                    !arguments.Any(char.IsControl) && seen.Add(app.Path))
                {
                    app.Arguments = arguments;
                    if (string.IsNullOrWhiteSpace(app.Name)) app.Name = Path.GetFileNameWithoutExtension(app.Path);
                    continue;
                }
                findings.Add(new("BAD_LINKED_APP", app?.Name ?? "(empty)",
                    "A linked app had no usable program path, or was listed twice. Dropped."));
                apps.RemoveAt(i--);
            }
            if (apps.Count > MaxAppsPerMode)
            {
                findings.Add(new("TOO_MANY_LINKED_APPS", mode.Name, $"{apps.Count} apps exceeded the limit of {MaxAppsPerMode}; the extras were dropped."));
                apps.RemoveRange(MaxAppsPerMode, apps.Count - MaxAppsPerMode);
            }
            if (imported && apps.Any(app => app.StartWithMode))
            {
                foreach (var app in apps) app.StartWithMode = false;
                findings.Add(new("IMPORTED_APPS_PAUSED", mode.Name, "Apps from an imported file do not start until you turn them on."));
            }
        }
        return findings;
    }
}
