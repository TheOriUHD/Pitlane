using System.Collections.Concurrent;
using System.IO;

namespace SimRigMode.Services;

public sealed class LoggingService
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action<string>? EntryWritten;
    public string LogDirectory { get; }

    public LoggingService(string? baseDirectory = null)
    {
        LogDirectory = baseDirectory ?? Path.Combine(Product.DataDirectory, "Logs");
        Directory.CreateDirectory(LogDirectory);
        _path = Path.Combine(LogDirectory, $"{Product.Name}-{DateTime.Now:yyyy-MM-dd}.log");
        Cleanup();
    }

    public void Info(string message) => Write("INF", message);
    public void Warning(string message) => Write("WRN", message);
    public void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}: {ex.Message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        EntryWritten?.Invoke(line);
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await File.AppendAllTextAsync(_path, line + Environment.NewLine); }
            catch { }
            finally { _gate.Release(); }
        });
    }

    public string ReadRecent(int maxLines = 500)
    {
        try { return string.Join(Environment.NewLine, File.ReadLines(_path).TakeLast(maxLines)); }
        catch { return "No diagnostics have been recorded yet."; }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log").Where(f => File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-14))) File.Delete(file);
        }
        catch { }
    }
}
