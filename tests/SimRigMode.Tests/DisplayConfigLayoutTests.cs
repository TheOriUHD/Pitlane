using System.Runtime.InteropServices;
using SimRigMode.NativeInterop;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// Marshalling layout assertions for the DisplayConfig interop structs.
///
/// These are the cheapest possible guard against a future edit silently corrupting every
/// SetDisplayConfig call. A wrong size here does not fail loudly - it hands the kernel a
/// misaligned array and produces behaviour that looks like a driver fault.
/// </summary>
public sealed class DisplayConfigLayoutTests
{
    [Theory]
    [InlineData(typeof(DisplayConfigNative.LUID), 8)]
    [InlineData(typeof(DisplayConfigNative.RATIONAL), 8)]
    [InlineData(typeof(DisplayConfigNative.POINTL), 8)]
    [InlineData(typeof(DisplayConfigNative.RECTL), 16)]
    [InlineData(typeof(DisplayConfigNative.SIZE2D), 8)]
    [InlineData(typeof(DisplayConfigNative.PATH_SOURCE_INFO), 20)]
    [InlineData(typeof(DisplayConfigNative.PATH_TARGET_INFO), 48)]
    [InlineData(typeof(DisplayConfigNative.DISPLAYCONFIG_PATH_INFO), 72)]
    [InlineData(typeof(DisplayConfigNative.SOURCE_MODE), 20)]
    [InlineData(typeof(DisplayConfigNative.VIDEO_SIGNAL_INFO), 48)]
    [InlineData(typeof(DisplayConfigNative.DESKTOP_IMAGE_INFO), 40)]
    [InlineData(typeof(DisplayConfigNative.DISPLAYCONFIG_MODE_INFO), 64)]
    [InlineData(typeof(DisplayConfigNative.DEVICE_INFO_HEADER), 20)]
    [InlineData(typeof(DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME), 420)]
    [InlineData(typeof(DisplayConfigNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME), 84)]
    [InlineData(typeof(DisplayConfigNative.DISPLAYCONFIG_ADAPTER_NAME), 276)]
    public void StructSizeMatchesTheNativeLayout(Type type, int expected)
        => Assert.Equal(expected, Marshal.SizeOf(type));

    [Fact]
    public void ModeInfoUnionArmsAllStartAtOffset16()
    {
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("sourceMode"));
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("targetMode"));
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("desktopImageInfo"));
    }

    [Fact]
    public void ModeInfoHeaderFieldsSitWhereTheNativeStructPutsThem()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("infoType"));
        Assert.Equal(4, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("id"));
        Assert.Equal(8, (int)Marshal.OffsetOf<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("adapterId"));
    }

    [Fact]
    public void PathInfoIsBlittable_SoArraysPinRatherThanCopy()
    {
        // targetAvailable is a BOOL declared as uint rather than [MarshalAs(Bool)] bool
        // specifically so this holds. GCHandle.Alloc(Pinned) throws for non-blittable types.
        var array = new DisplayConfigNative.DISPLAYCONFIG_PATH_INFO[2];
        var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        handle.Free();
    }

    [Fact]
    public void SubmittablePathFlags_ExcludeTheOutputOnlyBits()
    {
        var submittable = DisplayConfigNative.SUBMITTABLE_PATH_FLAGS;

        // Replaying SUPPORT_VIRTUAL_MODE without SDC_VIRTUAL_MODE_AWARE makes Windows read
        // modeInfoIdx as packed 16/16 bitfields, i.e. as garbage indices.
        Assert.Equal(0u, submittable & DisplayConfigNative.DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE);
        Assert.Equal(0u, submittable & DisplayConfigNative.DISPLAYCONFIG_PATH_BOOST_REFRESH_RATE);
        Assert.NotEqual(0u, submittable & DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE);
    }

    [Fact]
    public void ValidateAndApply_AreMutuallyExclusiveFlags()
    {
        // A validation pass must be a SEPARATE SetDisplayConfig call, never an extra bit on the
        // applying one.
        Assert.NotEqual(DisplayConfigNative.SDC_VALIDATE, DisplayConfigNative.SDC_APPLY);
        Assert.Equal(0u, DisplayConfigNative.SDC_VALIDATE & DisplayConfigNative.SDC_APPLY);
    }
}
