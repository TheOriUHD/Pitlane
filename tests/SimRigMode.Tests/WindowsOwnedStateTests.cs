using System.Runtime.CompilerServices;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// SimRigMode changes the machine only inside a mode switch the user starts, and only what that
/// switch is for: this session's display layout, the default audio devices, the USB devices a rule
/// names. It never writes what Windows owns and keeps by itself, and nothing it runs in the
/// background changes anything.
///
/// On 2026-09-24 it broke that three ways. Old builds saved every switch into Windows' display
/// database, so a detached monitor stayed detached with SimRigMode closed, and looped. The display
/// watchdog re-attached monitors on its own. Parking sent monitors a DDC/CI power command. All
/// three are gone; these tests fail if any of them comes back. See docs/report.md, section 0.
///
/// They scan the product sources, located at compile time via CallerFilePath. Comment lines are
/// skipped, so the history above can be written down.
/// </summary>
public sealed class WindowsOwnedStateTests
{
    /// <summary>This file's path. Captured here, because CallerFilePath resolves at the call site.</summary>
    internal static string ThisFile { get; } = SourceFile();
    private static string SourceFile([CallerFilePath] string path = "") => path;

    [Fact]
    public void NothingSavesALayoutIntoWindowsDisplayDatabase()
    {
        // Two mentions are allowed: the constant, and the check in Submit that refuses it.
        var offences = ProductCode()
            .Where(line => line.Text.Contains("SDC_SAVE_TO_DATABASE", StringComparison.Ordinal))
            .Where(line => !(line.File.EndsWith("DisplayConfigNative.cs", StringComparison.Ordinal) &&
                             line.Text.StartsWith("internal const uint SDC_SAVE_TO_DATABASE", StringComparison.Ordinal)))
            .Where(line => !(line.File.EndsWith("DisplayService.cs", StringComparison.Ordinal) && line.Text == RefusalCheck))
            .Select(Describe)
            .ToList();

        Assert.True(offences.Count == 0,
            "SimRigMode must never write Windows' display database:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    private const string RefusalCheck = "if ((sdcFlags & N.SDC_SAVE_TO_DATABASE) != 0)";

    [Fact]
    public void TheOnlySetDisplayConfigCall_RefusesTheSaveFlagFirst()
    {
        var calls = ProductCode().Where(line => line.Text.Contains("SetDisplayConfig(", StringComparison.Ordinal)).ToList();
        var declaration = Assert.Single(calls, line => line.File.EndsWith("DisplayConfigNative.cs", StringComparison.Ordinal));
        Assert.Contains("static extern", declaration.Text);
        var call = Assert.Single(calls, line => !line.File.EndsWith("DisplayConfigNative.cs", StringComparison.Ordinal));
        Assert.EndsWith("DisplayService.cs", call.File);

        var submit = Method(call.File, "public int Submit(DisplaySnapshot snapshot, uint sdcFlags)");
        var check = submit.FindIndex(line => line.Text == RefusalCheck);
        Assert.True(check >= 0, "DisplayService.Submit no longer refuses SDC_SAVE_TO_DATABASE.");
        Assert.StartsWith("throw new InvalidOperationException(", submit[check + 1].Text);
        Assert.Contains(submit.Skip(check + 2), line => line.Line == call.Line);
    }

    [Fact]
    public void NothingSendsAMonitorAPowerCommand()
    {
        string[] forbidden = ["SetVCPFeature", "dxva2", "SC_MONITORPOWER", "PhysicalMonitor"];
        var offences = ProductCode()
            .Where(line => forbidden.Any(token => line.Text.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(Describe)
            .ToList();

        Assert.True(offences.Count == 0,
            "A monitor's power is the user's to control:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    [Fact]
    public void DisplaysChangeOnlyInsideAModeSwitch()
    {
        // The apply engine is the only way to a layout change, and only the switch builds one.
        var engines = ProductCode().Where(line => line.Text.Contains("new DisplayApplyEngine(", StringComparison.Ordinal)).ToList();
        var engine = Assert.Single(engines);
        Assert.EndsWith("ModeSwitchService.cs", engine.File);
        Assert.Contains(Method(engine.File, "private async Task ApplyDisplayAsync(ModeConfig mode, CancellationToken token)"),
            line => line.Line == engine.Line);

        var submitters = ProductCode()
            .Where(line => line.Text.Contains(".Submit(", StringComparison.Ordinal))
            .Select(line => Path.GetFileName(line.File))
            .Distinct()
            .Order()
            .ToList();
        Assert.Equal(new[] { "DisplayApplyEngine.cs", "DisplayConfigGateway.cs" }, submitters);

        // No second way to change displays around the engine.
        string[] bypasses = ["ChangeDisplaySettings", "DisplayConfigSetDeviceInfo"];
        Assert.DoesNotContain(ProductCode(), line => bypasses.Any(token => line.Text.Contains(token, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheDisplayWatchdogOnlyReports()
    {
        // It listens and tells the user. It must not switch modes, change the layout, or ask
        // anything of a device - that is what re-attaching monitors on its own did.
        string[] forbidden = ["Switcher.", "SwitchModeAsync", "SwitchAsync", "Displays.", "DisplayConfigGateway", "DisplayApplyEngine", "Submit(", "CM_Request_Device_Eject"];
        var watchdog = Method("App.xaml.cs", "private void StartDisplayWatchdog()")
            .Concat(Method("App.xaml.cs", "private async Task OnDisplayBounceAsync(DisplayBounce bounce)"))
            .Concat(ProductCode().Where(line => line.File.EndsWith("MonitorHotplugWatcher.cs", StringComparison.Ordinal)))
            .Concat(ProductCode().Where(line => line.File.EndsWith("DisplayBounceDetector.cs", StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(watchdog);

        var offences = watchdog
            .Where(line => forbidden.Any(token => line.Text.Contains(token, StringComparison.Ordinal)))
            .Select(Describe)
            .ToList();
        Assert.True(offences.Count == 0,
            "The display watchdog must only report:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    // ===== source scanning =============================================================

    private sealed record SourceLine(string File, int Line, string Text);

    private static string Describe(SourceLine line) => $"{line.File}:{line.Line}: {line.Text}";

    /// <summary>tests/SimRigMode.Tests/ is two levels below the repository root.</summary>
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile)!, "..", ".."));

    /// <summary>Every line of product code: the app and SimRigMode.Core, not tests, tools or build
    /// output. Trimmed, with comment lines left out.</summary>
    private static IEnumerable<SourceLine> ProductCode()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "SimRigMode.csproj")), $"Repository root not found: {root}");

        string[] skipped = ["tests", "tools", "bin", "obj", "dist", "artifacts", "references", ".git"];
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order())
        {
            var relative = Path.GetRelativePath(root, file);
            var folders = relative.Split(Path.DirectorySeparatorChar)[..^1];
            if (folders.Any(folder => skipped.Contains(folder, StringComparer.OrdinalIgnoreCase))) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].Trim();
                if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("*", StringComparison.Ordinal)) continue;
                yield return new(relative, i + 1, text);
            }
        }
    }

    /// <summary>The lines of one method, from its signature to the closing brace that ends it.</summary>
    private static List<SourceLine> Method(string file, string signature)
    {
        var lines = ProductCode().Where(line => line.File.EndsWith(file, StringComparison.Ordinal)).ToList();
        var start = lines.FindIndex(line => line.Text.StartsWith(signature, StringComparison.Ordinal));
        Assert.True(start >= 0, $"{file} no longer has {signature}");

        var body = new List<SourceLine>();
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Count; i++)
        {
            body.Add(lines[i]);
            foreach (var character in lines[i].Text)
            {
                if (character == '{') { depth++; opened = true; }
                else if (character == '}') depth--;
            }
            if (opened && depth <= 0) break;
        }
        return body;
    }
}
