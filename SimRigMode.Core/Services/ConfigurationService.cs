using System.Text.Json;
using System.IO;
using SimRigMode.Models;
using SimRigMode.Services.Usb;

namespace SimRigMode.Services;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly LoggingService _log;
    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public AppConfig Current { get; private set; } = new();

    public ConfigurationService(LoggingService log, string? directoryPath = null) { _log = log; DirectoryPath = directoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimRigMode"); Directory.CreateDirectory(DirectoryPath); }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(ConfigPath)) Current = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(ConfigPath), JsonOptions) ?? new();
            Migrate(Current);
            // The on-disk config is user-editable, so it gets the same treatment as an import.
            foreach (var finding in UsbRuleSanitizer.Sanitize(Current))
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
        Directory.CreateDirectory(DirectoryPath);
        var temp = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(temp, ConfigPath, true);
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
        var findings = UsbRuleSanitizer.Sanitize(loaded);
        foreach (var finding in findings) _log.Warning("Import: " + finding);

        // An imported config never arrives armed. Whatever machine it came from, the user has to
        // look at the rules here and arm it deliberately.
        loaded.General.UsbSwitchingArmed = false;

        Current = loaded;
        await SaveAsync();
        return findings;
    }
    public async Task ResetAsync() { Current = new(); await SaveAsync(); }
    private static void Migrate(AppConfig config)
    {
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
    }
}
