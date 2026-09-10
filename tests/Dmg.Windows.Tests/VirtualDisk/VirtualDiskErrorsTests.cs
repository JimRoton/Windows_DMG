using System.Runtime.Versioning;
using Dmg.Core;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.VirtualDisk;

/// <summary>
/// The Win32-to-<see cref="DmgError"/> mapping, tested on any machine.
/// </summary>
/// <remarks>
/// The native calls need Windows; deciding what their failures mean does not, and
/// that is the part with the judgement in it. Every case here is a code a user is
/// realistically going to hit.
/// </remarks>
public sealed class VirtualDiskErrorsTests
{
    private const string VhdPath = @"C:\scratch\dmg\payload.vhd";

    [Fact]
    public void AccessDeniedIsAnElevationProblemAndSaysExactlyWhatToDo()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.AccessDenied,
            VirtualDiskOperation.Attach,
            VhdPath);

        Assert.Equal(DmgExitCode.ElevationRequired, error.Code);
        Assert.Equal(7, (int)error.Code);

        // The remedy has to be actionable: which shell, started how.
        Assert.Contains("Run as administrator", error.Message, StringComparison.Ordinal);
        Assert.Contains("Manage Volume", error.Message, StringComparison.Ordinal);

        // And it must not promise something this tool deliberately will not do.
        Assert.Contains("will not raise a UAC prompt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegeNotHeldIsTheSameProblemWithADifferentCode()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.PrivilegeNotHeld,
            VirtualDiskOperation.Attach,
            VhdPath);

        Assert.Equal(DmgExitCode.ElevationRequired, error.Code);
        Assert.Contains(VirtualDiskErrors.ElevationRemedy, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsAMountFailureThatNamesThePath()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.FileNotFound,
            VirtualDiskOperation.Open,
            VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Equal(6, (int)error.Code);
        Assert.Contains(VhdPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFolderIsDistinguishedFromAMissingFile()
    {
        DmgError file = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.FileNotFound, VirtualDiskOperation.Open, VhdPath);
        DmgError folder = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.PathNotFound, VirtualDiskOperation.Open, VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, folder.Code);
        Assert.NotEqual(file.Message, folder.Message);
    }

    [Fact]
    public void NotAVirtualDiskIsAMountFailureThatNamesTheCause()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.NotVirtualDisk,
            VirtualDiskOperation.Open,
            VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Equal(6, (int)error.Code);

        // "MountFailed" on its own is useless; the message has to say why.
        Assert.Contains("not recognise", error.Message, StringComparison.Ordinal);
        Assert.Contains("virtual disk", error.Message, StringComparison.Ordinal);
        Assert.Contains(VhdPath, error.Message, StringComparison.Ordinal);
        Assert.Contains("ERROR_VIRTDISK_NOT_VIRTUAL_DISK", error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedFooterIsACorruptImageNotAMountFailure()
    {
        foreach (uint code in new[]
        {
            VirtualDiskErrors.VhdDriveFooterMissing,
            VirtualDiskErrors.VhdDriveFooterChecksumMismatch,
            VirtualDiskErrors.VhdDriveFooterCorrupt,
        })
        {
            DmgError error = VirtualDiskErrors.FromNative(code, VirtualDiskOperation.Open, VhdPath);

            Assert.Equal(DmgExitCode.CorruptImage, error.Code);
            Assert.Equal(9, (int)error.Code);
        }
    }

    [Fact]
    public void ASharingViolationTellsTheUserToCloseWhateverHasTheFile()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.SharingViolation, VirtualDiskOperation.Open, VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("another program", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiskThatIsNotAttachedHasNoPhysicalDevice()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.DeviceNotExist, VirtualDiskOperation.GetPhysicalPath, VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("not attached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunOutOfSpaceIsItsOwnExitCode()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.DiskFull, VirtualDiskOperation.Attach, VhdPath);

        Assert.Equal(DmgExitCode.InsufficientSpace, error.Code);
        Assert.Equal(8, (int)error.Code);
    }

    [Fact]
    public void AnUnsupportedFormatIsNotReportedAsAMountFailure()
    {
        Assert.Equal(
            DmgExitCode.UnsupportedFormat,
            VirtualDiskErrors.FromNative(VirtualDiskErrors.VhdFormatUnknown, VirtualDiskOperation.Open, VhdPath).Code);

        Assert.Equal(
            DmgExitCode.UnsupportedFormat,
            VirtualDiskErrors.FromNative(VirtualDiskErrors.NotSupported, VirtualDiskOperation.Attach, VhdPath).Code);
    }

    [Fact]
    public void AnInvalidParameterIsOurBugAndSaysSo()
    {
        DmgError error = VirtualDiskErrors.FromNative(
            VirtualDiskErrors.InvalidParameter, VirtualDiskOperation.Attach, VhdPath);

        Assert.Equal(DmgExitCode.InternalError, error.Code);
        Assert.Contains("bug in dmg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVirtualDiskFacilityCodeStillLandsOnMountFailed()
    {
        // 0xC03A0042 is not a code this tool knows, but it is unmistakably a
        // virtual-disk failure, and guessing "mount failed" beats guessing wrong.
        DmgError error = VirtualDiskErrors.FromNative(0xC03A_0042, VirtualDiskOperation.Attach, VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("0xC03A0042", error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntirelyUnknownCodeIsStillReportedUsefully()
    {
        DmgError error = VirtualDiskErrors.FromNative(0x0000_04D5, VirtualDiskOperation.Detach, VhdPath);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("Detaching", error.Message, StringComparison.Ordinal);
        Assert.Contains("DetachVirtualDisk returned 0x000004D5", error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMappedFailureCarriesTheNativeCodeInItsDetail()
    {
        uint[] codes =
        [
            VirtualDiskErrors.FileNotFound,
            VirtualDiskErrors.PathNotFound,
            VirtualDiskErrors.AccessDenied,
            VirtualDiskErrors.SharingViolation,
            VirtualDiskErrors.NotSupported,
            VirtualDiskErrors.DeviceNotExist,
            VirtualDiskErrors.InvalidParameter,
            VirtualDiskErrors.DiskFull,
            VirtualDiskErrors.PrivilegeNotHeld,
            VirtualDiskErrors.NotVirtualDisk,
            VirtualDiskErrors.VirtDiskProviderNotFound,
        ];

        foreach (uint code in codes)
        {
            DmgError error = VirtualDiskErrors.FromNative(code, VirtualDiskOperation.Open, VhdPath);

            Assert.NotNull(error.Detail);
            Assert.Contains("OpenVirtualDisk", error.Detail, StringComparison.Ordinal);
            Assert.Contains($"0x{code:X8}", error.Detail, StringComparison.Ordinal);
            Assert.NotEqual("unrecognised", VirtualDiskErrors.NativeErrorName(code));
        }
    }

    [Fact]
    public void NoMappedFailureEverReportsSuccess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VirtualDiskErrors.FromNative(VirtualDiskErrors.Success, VirtualDiskOperation.Open, VhdPath));

        Assert.True(VirtualDiskErrors.Succeeded(0));
        Assert.False(VirtualDiskErrors.Succeeded(1));
    }

    [Fact]
    public void TheFakeAndTheRealServiceAgreeAboutWhatANativeErrorMeans()
    {
        // The point of routing the fake through the shipping mapper: a test can
        // rehearse "the user was not elevated" and get the real exit code and the
        // real wording, on macOS.
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);
        service.FailOpenWithNativeError(VirtualDiskErrors.AccessDenied, VhdPath);

        Result<IVirtualDiskHandle> opened = service.Open(VhdPath, VirtualDiskAccessMode.ReadOnly);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.ElevationRequired, opened.Error.Code);
        Assert.Equal(
            VirtualDiskErrors.FromNative(VirtualDiskErrors.AccessDenied, VirtualDiskOperation.Open, VhdPath),
            opened.Error);
    }

    [Fact]
    public void TheWindowsServiceIsMarkedWindowsOnlyAndImplementsThePort()
    {
        Type service = typeof(WindowsVirtualDiskService);

        Assert.True(typeof(IVirtualDiskService).IsAssignableFrom(service));

        SupportedOSPlatformAttribute? platform = service
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .FirstOrDefault();

        Assert.NotNull(platform);
        Assert.Equal("windows", platform.PlatformName);
    }
}
