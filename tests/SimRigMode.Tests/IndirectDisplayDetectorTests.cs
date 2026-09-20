using SimRigMode.Services.Display;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// The incident regression for display classification.
///
/// Values here are taken from the real capture in %LOCALAPPDATA%\SimRigMode\config.json as it
/// stood after the incident: the Elgato Prompter reported OutputTechnology 6 (LVDS) on its own
/// adapter LUID 1582295, while the three GPU-attached panels reported 5 (HDMI) and 10
/// (DisplayPort) on LUID 117063.
/// </summary>
public sealed class IndirectDisplayDetectorTests
{
    private const uint Hdmi = 5;
    private const uint Lvds = 6;
    private const uint DisplayPort = 10;
    private const uint Miracast = 15;
    private const uint IndirectWired = 16;
    private const uint IndirectVirtual = 17;

    private const string GpuAdapter = @"\\?\PCI#VEN_10DE&DEV_2684&SUBSYS_167E10DE&REV_A1#4&2d9c1cbb&0&0008#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";
    private const string DisplayLinkAdapter = @"\\?\ROOT#BASICDISPLAY#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";

    [Fact]
    public void Prompter_IsClassifiedIndirect_DespiteReportingLvds()
    {
        // THE test. A detector keyed on output technology would classify the exact device that
        // caused the incident as an ordinary panel, because DisplayLink reports LVDS here.
        var verdict = IndirectDisplayDetector.Classify(DisplayLinkAdapter, Lvds);

        Assert.True(verdict.IsIndirect);
        Assert.Equal(IndirectConfidence.Certain, verdict.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData(Hdmi)]
    [InlineData(DisplayPort)]
    [InlineData(Lvds)]          // a genuine laptop panel is LVDS on a PCI adapter
    public void GpuAttachedPanels_AreClassifiedDirect(uint outputTechnology)
    {
        var verdict = IndirectDisplayDetector.Classify(GpuAdapter, outputTechnology);

        Assert.False(verdict.IsIndirect);
        Assert.Equal(IndirectConfidence.Certain, verdict.Confidence);
    }

    [Theory]
    [InlineData(Miracast)]
    [InlineData(IndirectWired)]
    [InlineData(IndirectVirtual)]
    public void IndirectOutputTechnology_IsDecisive_EvenOnAPciAdapter(uint outputTechnology)
    {
        var verdict = IndirectDisplayDetector.Classify(GpuAdapter, outputTechnology);

        Assert.True(verdict.IsIndirect);
        Assert.Equal(IndirectConfidence.Certain, verdict.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownAdapter_FailsSafe_TreatedAsIndirect(string? adapterDevicePath)
    {
        // A false positive costs "SimRigMode does not manage this display".
        // A false negative costs the incident.
        var verdict = IndirectDisplayDetector.Classify(adapterDevicePath, Hdmi);

        Assert.True(verdict.IsIndirect);
        Assert.Equal(IndirectConfidence.Unknown, verdict.Confidence);
    }

    [Theory]
    [InlineData(@"\\?\ROOT#DISPLAY#0001#{guid}")]
    [InlineData(@"\\?\SWD#IDDCX#0000#{guid}")]
    [InlineData(@"\\?\USB#VID_17E9&PID_FF1A#{guid}")]
    public void NonPciAdapters_AreClassifiedIndirect(string adapterDevicePath)
        => Assert.True(IndirectDisplayDetector.Classify(adapterDevicePath, Lvds).IsIndirect);

    [Fact]
    public void PciPrefixMatchIsCaseInsensitive()
        => Assert.False(IndirectDisplayDetector.Classify(@"\\?\pci#ven_10de&dev_2684#4&0&0008#{guid}", Hdmi).IsIndirect);

    [Fact]
    public void Reason_ExplainsWhyOutputTechnologyWasNotUsed()
    {
        // The reason string is rendered verbatim in the opt-in warning dialog, so it has to
        // actually explain itself to someone deciding whether to manage the display.
        var reason = IndirectDisplayDetector.Classify(DisplayLinkAdapter, Lvds).Reason;
        Assert.Contains("software display adapter", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output technology", reason, StringComparison.OrdinalIgnoreCase);
    }
}
