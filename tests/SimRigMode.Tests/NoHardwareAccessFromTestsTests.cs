using System.Runtime.CompilerServices;
using SimRigMode.Services.Elevation;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// Enforces the rule that made the old suite dangerous: a test must never reach hardware.
///
/// The previous smoke-test project performed real SetDisplayConfig applies against the live
/// desktop with SDC_SAVE_TO_DATABASE, seeded a USB rule that resolved to a machine-wide disable
/// batch, and reached the production elevation path. See docs/report.md.
///
/// This scans this project's own sources, located at COMPILE time via CallerFilePath so it cannot
/// drift with build configuration or output layout.
/// </summary>
public sealed class NoHardwareAccessFromTestsTests
{
    /// <summary>
    /// Primitives that mutate machine state. None may be named anywhere under tests/.
    ///
    /// Note what is deliberately NOT on this list: ApplyBatchElevatedAsync and
    /// StartupService.SetEnabled. Tests call those on purpose, to assert that they refuse.
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "PerformLocalOperation",
        "CM_Enable_DevNode",
        "CM_Disable_DevNode",
        "SetDefaultEndpoint",
        // DDC/CI: sends a real monitor a command. The product must not have it either
        // (WindowsOwnedStateTests); this keeps a test from bringing it back.
        "SetVCPFeature",
        // Starts and ends real programs.
        "new SystemProcessController(",
        // Moves the user's real windows, and puts a real window over a monitor.
        "ParkedWindowMover.MoveOff",
        "new ParkedScreenKeeper("
    ];

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public void NoTestSourceNamesAMutatingNativePrimitive()
    {
        var root = SourceDirectory();
        Assert.True(Directory.Exists(root), $"Test source directory not found: {root}");

        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetFileName(file) == Path.GetFileName(ThisFile())) continue;   // this file lists them
            if (Path.GetFileName(file) == Path.GetFileName(WindowsOwnedStateTests.ThisFile)) continue;   // so does the product scan

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var pattern in Forbidden)
                    if (lines[i].Contains(pattern, StringComparison.Ordinal))
                        offences.Add($"{Path.GetFileName(file)}:{i + 1} references {pattern}");
        }

        Assert.True(offences.Count == 0,
            "Tests must never reach hardware:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [Fact]
    public void TheTestProcessCannotElevate()
    {
        Assert.Equal(ElevationMode.Disabled, ElevationPolicy.Mode);
        Assert.False(ElevationGuard.IsProductionAppImage(out _));
    }

    [Fact]
    public void TheTestProcessIsNotRunningFromAProductionApphost()
    {
        // tests/Directory.Build.props sets UseAppHost=false and fails the build if a runnable
        // binary appears under tests/. This is the runtime half of that guarantee.
        var image = Path.GetFileName(Environment.ProcessPath ?? "");
        Assert.NotEqual(Product.ExecutableFileName, image, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("SimRigMode.exe", image, StringComparer.OrdinalIgnoreCase);
    }
}
