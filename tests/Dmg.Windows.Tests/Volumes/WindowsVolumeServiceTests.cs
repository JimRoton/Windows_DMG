using System.Runtime.Versioning;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// The one thing about the real service that can be checked without Windows: that
/// it is marked as needing it, and that it actually implements the port.
/// </summary>
/// <remarks>
/// Everything else about <see cref="WindowsVolumeService"/> - whether
/// <c>CreateFileW</c>, <c>DeviceIoControl</c>, <c>FindFirstVolumeW</c> and
/// <c>SetVolumeMountPointW</c> behave the way their documentation says - needs a
/// real Windows box with a real disk attached, which this repository does not have.
/// </remarks>
public sealed class WindowsVolumeServiceTests
{
    [Fact]
    public void TheWindowsServiceIsMarkedWindowsOnlyAndImplementsThePort()
    {
        Type service = typeof(WindowsVolumeService);

        Assert.True(typeof(IVolumeService).IsAssignableFrom(service));

        SupportedOSPlatformAttribute? platform = service
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .FirstOrDefault();

        Assert.NotNull(platform);
        Assert.Equal("windows", platform.PlatformName);
    }
}
