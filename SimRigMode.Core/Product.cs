namespace SimRigMode;

/// <summary>
/// What the user sees the app called. The code keeps its original name, SimRigMode, as a
/// codename: namespaces, project names and internal identifiers were not renamed.
/// </summary>
public static class Product
{
    public const string Name = "Pitlane";
    public const string ExecutableFileName = "Pitlane.exe";

    /// <summary>%LOCALAPPDATA%\Pitlane: the configuration, and the logs under Logs.</summary>
    public static string DataDirectory => Path.Combine(LocalAppData, Name);

    /// <summary>Where builds from before the rename kept their data. Read once, to carry the
    /// configuration over; never written.</summary>
    public static string LegacyDataDirectory => Path.Combine(LocalAppData, "SimRigMode");

    /// <summary>
    /// The standby test build (dotnet publish -p:StandbyTest=true), made on 2026-09-26 for the
    /// user to try by hand whether the rig monitor can go into standby again. A monitor on the park
    /// list is left out of the layout like any other, so it gets no input, instead of being
    /// parked; every monitor dropout and return is logged. It breaks SAFETY.md invariant 11 on
    /// purpose, to see whether the dropout loop still happens, and is never the normal build.
    /// </summary>
    public static bool IsStandbyTest =>
#if STANDBY_TEST
        true;
#else
        false;
#endif

    /// <summary>
    /// The capture demo build (dotnet publish -p:CaptureDemo=true), for recording the README's
    /// switch animation on a real machine. A switch plays exactly as it looks, and changes nothing:
    /// no display, app, sound, USB device or smart plug is touched, and the mode it shows is never
    /// saved. It looks like the normal build on purpose, and is never deployed for use.
    /// </summary>
    public static bool IsCaptureDemo =>
#if CAPTURE_DEMO
        true;
#else
        false;
#endif

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
