using System.Runtime.CompilerServices;
using SimRigMode.Services.Elevation;

namespace SimRigMode.Tests;

/// <summary>
/// Belt and braces: latch elevation off for the entire test process before any test code runs.
///
/// A module initializer runs before the first access to any type in this assembly, so there is
/// no ordering hole. This is not the primary defence - ElevationGuard already refuses because
/// the entry assembly under xUnit is "testhost" rather than "SimRigMode" - but the old smoke
/// suite proved that relying on a single layer here is how machines get damaged.
/// </summary>
internal static class TestElevationKillSwitch
{
    [ModuleInitializer]
    internal static void Initialize() => ElevationPolicy.Disable("xUnit test host");
}
