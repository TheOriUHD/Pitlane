using SimRigMode.Models;
using SimRigMode.Services.Apps;
using SimRigMode.Tests.Fakes;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// Apps linked to a mode: started when it begins, closed when switching to the other mode.
/// Everything runs against FakeProcessController; no real program is started or ended.
/// </summary>
public sealed class LinkedAppsTests
{
    private const string SimHub = @"C:\Program Files (x86)\SimHub\SimHubWPF.exe";
    private const string PitHouse = @"C:\Program Files\MOZA Pit House\MOZA Pit House.exe";
    private const string Discord = @"C:\Users\<user>\AppData\Local\Discord\app-1.0.9200\Discord.exe";

    private static LinkedApp App(string path, bool start = true, bool close = true, bool force = true) =>
        new() { Name = Path.GetFileNameWithoutExtension(path), Path = path, StartWithMode = start, CloseWhenLeaving = close, ForceClose = force };

    private static AppLauncherService Launcher(FakeProcessController processes) =>
        new(processes, TimeSpan.FromSeconds(8), (_, _) => Task.CompletedTask);

    // ===== starting ==================================================================

    [Fact]
    public void Start_LaunchesEachAppThatIsNotRunning()
    {
        var processes = new FakeProcessController { Files = { SimHub, PitHouse } };

        var results = Launcher(processes).Start([App(SimHub), App(PitHouse)]);

        Assert.Equal([SimHub, PitHouse], processes.Started);
        Assert.All(results, result => Assert.True(result.Success));
    }

    [Fact]
    public void Start_NeverStartsASecondCopy()
    {
        var processes = new FakeProcessController { Files = { SimHub } };
        processes.Run(SimHub);

        var result = Assert.Single(Launcher(processes).Start([App(SimHub)]));

        Assert.Empty(processes.Started);
        Assert.True(result.Success);
        Assert.Contains("already running", result.Detail);
    }

    [Fact]
    public void Start_ReportsAMissingProgramInsteadOfThrowing()
    {
        var processes = new FakeProcessController();

        var result = Assert.Single(Launcher(processes).Start([App(SimHub)]));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Detail);
        Assert.Empty(processes.Started);
    }

    // ===== closing ===================================================================

    [Fact]
    public async Task Close_AsksNicelyFirst()
    {
        var processes = new FakeProcessController();
        var id = processes.Run(PitHouse);

        var result = Assert.Single(await Launcher(processes).CloseAsync([App(PitHouse)], CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal([id], processes.CloseRequests);
        Assert.Empty(processes.Ended);
    }

    [Fact]
    public async Task Close_EndsAnAppThatHidesInTheTray_WhenAllowed()
    {
        var processes = new FakeProcessController();
        var id = processes.Run(SimHub);
        processes.IgnoresClose.Add(id);

        var result = Assert.Single(await Launcher(processes).CloseAsync([App(SimHub, force: true)], CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal([id], processes.Ended);
        Assert.Contains("was ended", result.Detail);
    }

    [Fact]
    public async Task Close_NeverEndsAnAppWhenThatIsNotAllowed()
    {
        var processes = new FakeProcessController();
        var id = processes.Run(SimHub);
        processes.IgnoresClose.Add(id);

        var result = Assert.Single(await Launcher(processes).CloseAsync([App(SimHub, force: false)], CancellationToken.None));

        Assert.False(result.Success);
        Assert.Empty(processes.Ended);
        Assert.True(processes.Running.ContainsKey(id));
        Assert.Contains("tray", result.Detail);
    }

    [Fact]
    public async Task Close_ReportsAnAppItIsNotAllowedToEnd()
    {
        var processes = new FakeProcessController();
        var id = processes.Run(SimHub);
        processes.IgnoresClose.Add(id);
        processes.Protected.Add(id);

        var result = Assert.Single(await Launcher(processes).CloseAsync([App(SimHub)], CancellationToken.None));

        Assert.False(result.Success);
        Assert.Contains("administrator", result.Detail);
    }

    [Fact]
    public async Task Close_HandlesEveryRunningCopy_AndLeavesOtherProgramsAlone()
    {
        var processes = new FakeProcessController();
        var first = processes.Run(SimHub);
        var second = processes.Run(SimHub);
        var other = processes.Run(Discord);

        await Launcher(processes).CloseAsync([App(SimHub)], CancellationToken.None);

        Assert.Contains(first, processes.CloseRequests);
        Assert.Contains(second, processes.CloseRequests);
        Assert.DoesNotContain(other, processes.CloseRequests);
        Assert.True(processes.Running.ContainsKey(other));
    }

    [Fact]
    public async Task Close_AnAppThatIsNotRunning_IsFine()
    {
        var result = Assert.Single(await Launcher(new FakeProcessController()).CloseAsync([App(SimHub)], CancellationToken.None));

        Assert.True(result.Success);
        Assert.Contains("not running", result.Detail);
    }

    // ===== which apps a switch touches ===============================================

    [Fact]
    public void EnteringSimRig_StartsItsAppsAndClosesTheDesktopOnes()
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.AddRange([App(SimHub), App(PitHouse, start: false)]);
        config.Desktop.LinkedApps.Add(App(Discord));

        Assert.Equal([SimHub], AppTransitionPlanner.ToStart(config, WorkspaceMode.SimRig).Select(app => app.Path));
        Assert.Equal([Discord], AppTransitionPlanner.ToClose(config, WorkspaceMode.SimRig).Select(app => app.Path));
    }

    [Fact]
    public void EnteringDesktop_ClosesTheSimRigAppsThatAskForIt()
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.AddRange([App(SimHub), App(PitHouse, close: false)]);

        Assert.Equal([SimHub], AppTransitionPlanner.ToClose(config, WorkspaceMode.Desktop).Select(app => app.Path));
        Assert.Empty(AppTransitionPlanner.ToStart(config, WorkspaceMode.Desktop));
    }

    [Fact]
    public void AnAppLinkedToBothModes_IsNeverClosedJustToBeStartedAgain()
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.Add(App(Discord));
        config.Desktop.LinkedApps.Add(App(Discord.ToUpperInvariant()));

        Assert.Empty(AppTransitionPlanner.ToClose(config, WorkspaceMode.Desktop));
        Assert.Empty(AppTransitionPlanner.ToClose(config, WorkspaceMode.SimRig));
    }

    // ===== finding apps ==============================================================

    [Theory]
    [InlineData("SimHub", SimHub, true)]
    [InlineData("MOZA Pit House", PitHouse, true)]
    [InlineData("Fanatec App", @"C:\Program Files\Fanatec\Fanatec App\FanatecApp.exe", true)]
    [InlineData("Simucube Tuner", @"C:\Program Files\Simucube\Simucube Tuner.exe", true)]
    [InlineData("RaceHub", @"C:\Program Files\Asetek SimSports\RaceHub\RaceHub.exe", true)]
    [InlineData("Logitech G HUB", @"C:\Program Files\LGHUB\lghub.exe", true)]
    [InlineData("anything", @"C:\Games\SimHub\SimHubWPF.exe", true)]
    [InlineData("Spotify", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", false)]
    [InlineData("Discord", Discord, false)]
    public void SimRacingToolsAreRecognised(string name, string path, bool expected)
        => Assert.Equal(expected, AppCatalog.IsSimRacing(name, path));

    [Theory]
    [InlineData("Uninstall SimHub", true)]
    [InlineData("SimHub Help", true)]
    [InlineData("Release Notes", true)]
    [InlineData("SimHub", false)]
    public void StartMenuClutterIsSkipped(string name, bool expected) => Assert.Equal(expected, AppCatalog.IsNoise(name));

    [Fact]
    public void WindowsToolsAreNotOffered()
    {
        Assert.False(AppCatalog.IsOfferable(@"C:\Windows\System32\notepad.exe", @"C:\Windows"));
        Assert.True(AppCatalog.IsOfferable(SimHub, @"C:\Windows"));
    }

    [Fact]
    public void SimRacingToolsAreListedFirst()
    {
        var ordered = AppCatalog.Order([
            new("Spotify", @"C:\a\Spotify.exe", "", false),
            new("SimHub", SimHub, "", true),
            new("Discord", Discord, "", false)]);

        Assert.Equal(["SimHub", "Discord", "Spotify"], ordered.Select(app => app.Name));
    }

    // ===== configuration from outside the process ====================================

    [Theory]
    [InlineData("")]
    [InlineData("SimHubWPF.exe")]
    [InlineData(@"C:\Program Files\SimHub\SimHub.bat")]
    public void AnythingButAFullProgramPath_IsDropped(string path)
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.Add(new LinkedApp { Name = "x", Path = path });

        var findings = LinkedAppSanitizer.Sanitize(config, imported: false);

        Assert.Empty(config.SimRig.LinkedApps);
        Assert.Single(findings);
    }

    [Fact]
    public void AppsFromAnImportedFile_DoNotStartUntilTurnedOn()
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.Add(App(SimHub));

        var findings = LinkedAppSanitizer.Sanitize(config, imported: true);

        Assert.False(Assert.Single(config.SimRig.LinkedApps).StartWithMode);
        Assert.Contains(findings, finding => finding.Code == "IMPORTED_APPS_PAUSED");
    }

    [Fact]
    public void YourOwnConfig_KeepsItsApps()
    {
        var config = new AppConfig();
        config.SimRig.LinkedApps.AddRange([App(SimHub), App(SimHub.ToUpperInvariant())]);

        LinkedAppSanitizer.Sanitize(config, imported: false);

        Assert.True(Assert.Single(config.SimRig.LinkedApps).StartWithMode);   // the duplicate is dropped
    }

    [Fact]
    public void AMissingAppList_IsTolerated()
    {
        var config = new AppConfig();
        config.Desktop.LinkedApps = null!;

        LinkedAppSanitizer.Sanitize(config, imported: false);

        Assert.NotNull(config.Desktop.LinkedApps);
    }
}
