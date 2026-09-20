using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services.Display;

/// <summary>How much weight the verdict carries.</summary>
public enum IndirectConfidence
{
    /// <summary>The adapter was classified from its device path.</summary>
    Certain,

    /// <summary>The adapter could not be classified. Treated as indirect, deliberately.</summary>
    Unknown
}

/// <param name="IsIndirect">True when this display is driven by an indirect display driver
/// (DisplayLink, a USB dock, a virtual display) rather than a GPU output.</param>
/// <param name="Reason">Shown verbatim in the opt-in warning, so it must read as prose.</param>
public sealed record IndirectDisplayVerdict(bool IsIndirect, IndirectConfidence Confidence, string Reason);

/// <summary>
/// Classifies a display as GPU-attached or indirect.
///
/// This exists because of the incident in docs/report.md. Every mode switch removed a
/// DisplayLink-backed Elgato Prompter from the submitted topology and the reverse switch rebuilt
/// it, which is the kind of transition that makes an indirect display stack tear down and
/// reconstruct a live display path.
///
/// The load-bearing detail: the Prompter reports OutputTechnology 6 (LVDS). Not
/// INDIRECT_WIRED (16), not INDIRECT_VIRTUAL (17). A detector keyed on output technology - the
/// obvious design - would have classified the exact device that caused the incident as an
/// ordinary panel. Classification is therefore based on the ADAPTER DEVICE PATH, which is
/// \\?\PCI#... for a real GPU and something else for an indirect display driver. In the captured
/// incident config the Prompter also sat alone on its own adapter LUID (1582295) while every
/// GPU-attached panel shared 117063.
/// </summary>
public static class IndirectDisplayDetector
{
    private const string PciAdapterPrefix = @"\\?\PCI#";

    /// <summary>
    /// Classifies one display. Pure - no hardware access, so it is fully testable against
    /// captured fixtures.
    /// </summary>
    /// <param name="adapterDevicePath">From DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME. Empty
    /// when the query failed.</param>
    /// <param name="outputTechnology">Secondary signal only.</param>
    public static IndirectDisplayVerdict Classify(string? adapterDevicePath, uint outputTechnology)
    {
        // Fail safe. An adapter we cannot classify is treated as indirect, which means it gets
        // carried through a mode switch untouched. The cost of a false positive is "SimRigMode
        // does not manage this display"; the cost of a false negative is the incident.
        if (string.IsNullOrWhiteSpace(adapterDevicePath))
            return new(true, IndirectConfidence.Unknown,
                "Windows did not report an adapter device path for this display, so SimRigMode " +
                "cannot confirm it is driven by a graphics card. It will be left untouched.");

        // An indirect output technology is decisive even on a PCI adapter path.
        if (outputTechnology is N.OUTPUT_TECHNOLOGY_INDIRECT_WIRED
                             or N.OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL
                             or N.OUTPUT_TECHNOLOGY_MIRACAST)
            return new(true, IndirectConfidence.Certain,
                $"This display reports an indirect output technology ({Describe(outputTechnology)}), " +
                "so it is driven by a display driver rather than a graphics output.");

        if (adapterDevicePath.StartsWith(PciAdapterPrefix, StringComparison.OrdinalIgnoreCase))
            return new(false, IndirectConfidence.Certain,
                "This display is attached to a graphics card output.");

        return new(true, IndirectConfidence.Certain,
            $"This display is driven by a software display adapter ({adapterDevicePath}) rather " +
            "than a graphics card, which is how DisplayLink and USB docks present themselves. " +
            "Note that such displays often report an ordinary output technology, so the adapter " +
            "is what identifies them.");
    }

    private static string Describe(uint outputTechnology) => outputTechnology switch
    {
        N.OUTPUT_TECHNOLOGY_INDIRECT_WIRED => "indirect wired",
        N.OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL => "indirect virtual",
        N.OUTPUT_TECHNOLOGY_MIRACAST => "Miracast",
        N.OUTPUT_TECHNOLOGY_HDMI => "HDMI",
        N.OUTPUT_TECHNOLOGY_LVDS => "LVDS",
        N.OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort",
        N.OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL => "DisplayPort over USB",
        N.OUTPUT_TECHNOLOGY_INTERNAL => "internal panel",
        _ => $"type {outputTechnology}"
    };
}
