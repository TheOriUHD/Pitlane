using System.Text.Json;
using System.IO;
using SimRigMode.Models;
using SimRigMode.Services.Apps;
using SimRigMode.Services.Display;
using SimRigMode.Services.Usb;

namespace SimRigMode.Services;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly LoggingService _log;

    /// <summary>
    /// Every save goes through one temp file, and saves now come from the settings window, the
    /// mode switch and the display watchdog. Two overlapping saves used to be able to collide on
    /// that temp file, so they are serialised here rather than by each caller.
    /// </summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public AppConfig Current { get; private set; } = new();

    public ConfigurationService(LoggingService log, string? directoryPath = null) { _log = log; _isDefaultLocation = directoryPath is null; DirectoryPath = directoryPath ?? Product.DataDirectory; Directory.CreateDirectory(DirectoryPath); }

    private readonly bool _isDefaultLocation;

    /// <summary>
    /// The first start after the rename finds no configuration under %LOCALAPPDATA%\Pitlane and
    /// copies the one SimRigMode left behind. It is copied, not moved: the old folder stays as it
    /// was, as a backup.
    /// </summary>
    private void CarryOverLegacyConfiguration()
    {
        if (!_isDefaultLocation || File.Exists(ConfigPath)) return;
        var legacy = Path.Combine(Product.LegacyDataDirectory, "config.json");
        if (!File.Exists(legacy)) return;
        try
        {
            File.Copy(legacy, ConfigPath);
            _log.Info($"Configuration: carried over from {legacy}");
        }
        catch (Exception ex) { _log.Error("Configuration: the SimRigMode configuration could not be carried over", ex); }
    }

    public async Task LoadAsync()
    {
        CarryOverLegacyConfiguration();
        try
        {
            if (File.Exists(ConfigPath)) Current = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(ConfigPath), JsonOptions) ?? new();
            Migrate(Current);
            // The on-disk config is user-editable, so it gets the same treatment as an import.
            foreach (var finding in Sanitize(Current, imported: false))
                _log.Warning("Configuration: " + finding);
        }
        catch (Exception ex)
        {
            _log.Error("Configuration could not be loaded; defaults are in use", ex);
            try { File.Copy(ConfigPath, ConfigPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"); } catch { }
            Current = new();
        }
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, ConfigPath, true);
        }
        finally { _saveGate.Release(); }
    }

    public async Task ExportAsync(string path) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Current, JsonOptions));
    /// <summary>
    /// Imports a configuration. The file is sanitized BEFORE it becomes the live config and
    /// before anything is written to disk: an imported file previously installed arbitrary USB
    /// rules, including hub-self-disable, with no validation and no confirmation.
    /// </summary>
    /// <returns>What the sanitizer changed, for the caller to show the user.</returns>
    public async Task<IReadOnlyList<SanitizerFinding>> ImportAsync(string path)
    {
        var loaded = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(path), JsonOptions)
            ?? throw new InvalidDataException("The file does not contain a valid configuration.");
        Migrate(loaded);
        var findings = Sanitize(loaded, imported: true);
        foreach (var finding in findings) _log.Warning("Import: " + finding);

        // An imported config never arrives armed. Whatever machine it came from, the user has to
        // look at the rules here and arm it deliberately.
        loaded.General.UsbSwitchingArmed = false;

        Current = loaded;
        await SaveAsync();
        return findings;
    }
    public async Task ResetAsync() { Current = new(); await SaveAsync(); }

    private static IReadOnlyList<SanitizerFinding> Sanitize(AppConfig config, bool imported)
    {
        config.General ??= new();
        config.Desktop ??= new() { Name = "Desktop Mode" };
        config.SimRig ??= new() { Name = "Sim Rig Mode" };
        return UsbRuleSanitizer.Sanitize(config)
            .Concat(DisplayPowerPolicy.Sanitize(config.General))
            .Concat(LinkedAppSanitizer.Sanitize(config, imported))
            .ToList();
    }

    private static void Migrate(AppConfig config)
    {
        config.Integration ??= new();
        if (config.SchemaVersion < 1) config.SchemaVersion = 1;
        if (config.SchemaVersion < 2)
        {
            foreach (var rule in config.UsbRules)
            {
                if (string.IsNullOrWhiteSpace(rule.IdentityKey)) rule.IdentityKey = "instance:" + rule.InstanceId.ToLowerInvariant();
                rule.LastKnownTargets ??= [];
            }
            config.SchemaVersion = 2;
        }
        if (config.SchemaVersion < 3)
        {
            MigrateIntegration(config.Integration);
            config.SchemaVersion = 3;
        }
        config.Integration.Legacy = null;
    }

    /// <summary>
    /// The Home Assistant settings used to be a switch, two webhooks, a URL, an entity and a
    /// token. They are now the switch, a host, a token and an entity. The switch keeps its key,
    /// so it carries over as it was; the URL and entity move to their new names. Webhooks have
    /// no equivalent and go.
    /// </summary>
    internal static void MigrateIntegration(IntegrationConfig integration)
    {
        var legacy = integration.Legacy;
        if (legacy is null) return;
        string Text(string key) => legacy.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
        if (string.IsNullOrWhiteSpace(integration.Host)) integration.Host = Text("BaseUrl");
        if (string.IsNullOrWhiteSpace(integration.Entity)) integration.Entity = Text("EntityId");
        integration.Legacy = null;
    }
}
