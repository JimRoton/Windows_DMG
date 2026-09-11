using Dmg.Core;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// The Win32-to-<see cref="DmgError"/> mapping, tested on any machine.
/// </summary>
/// <remarks>
/// The native calls need Windows; deciding what their failures mean does not, and
/// that is the part with the judgement in it - the same reasoning
/// <c>VirtualDiskErrorsTests</c> is built on, for the twin mapper.
/// </remarks>
public sealed class VolumeErrorsTests
{
    private const string Subject = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    [Fact]
    public void AccessDeniedIsAMountFailureAboutWorkingOutTheLetter()
    {
        DmgError error = VolumeErrors.FromNative(VolumeErrors.AccessDenied, VolumeOperation.GetDeviceNumber, Subject);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Equal(6, (int)error.Code);
        Assert.Contains("drive letter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceThatVanishedSaysTheDiskWasDetached()
    {
        DmgError error = VolumeErrors.FromNative(VolumeErrors.FileNotFound, VolumeOperation.OpenDevice, Subject);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("detached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathNotFoundAndInvalidDriveReadTheSameAsFileNotFound()
    {
        DmgError fileNotFound = VolumeErrors.FromNative(VolumeErrors.FileNotFound, VolumeOperation.OpenDevice, Subject);
        DmgError pathNotFound = VolumeErrors.FromNative(VolumeErrors.PathNotFound, VolumeOperation.OpenDevice, Subject);
        DmgError invalidDrive = VolumeErrors.FromNative(VolumeErrors.InvalidDrive, VolumeOperation.OpenDevice, Subject);

        Assert.Equal(fileNotFound.Message, pathNotFound.Message);
        Assert.Equal(fileNotFound.Message, invalidDrive.Message);
    }

    [Fact]
    public void AnUnrecognisedFilesystemIsNotAMountFailure()
    {
        // Windows attached the disk fine; it just has no driver for what is on it.
        // That is dmg's own UnsupportedFormat story, not a mount failure.
        DmgError error = VolumeErrors.FromNative(VolumeErrors.UnrecognizedVolume, VolumeOperation.GetDeviceNumber, Subject);

        Assert.Equal(DmgExitCode.FilesystemNotMountable, error.Code);
        Assert.Equal(5, (int)error.Code);
    }

    [Fact]
    public void ASharingViolationNamesTheSubjectAndSaysItIsInUse()
    {
        DmgError error = VolumeErrors.FromNative(VolumeErrors.SharingViolation, VolumeOperation.OpenDevice, Subject);

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("another program", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATakenDriveLetterNamesTheLetterAndSuggestsAFix()
    {
        DmgError error = VolumeErrors.FromNative(VolumeErrors.DirectoryNotEmpty, VolumeOperation.SetMountPoint, @"E:\");

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains(@"E:\", error.Message, StringComparison.Ordinal);
        Assert.Contains("already in use", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--letter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidParameterIsOurBugAndSaysSo()
    {
        DmgError error = VolumeErrors.FromNative(VolumeErrors.InvalidParameter, VolumeOperation.GetVolumePathNames, Subject);

        Assert.Equal(DmgExitCode.InternalError, error.Code);
        Assert.Equal(1, (int)error.Code);
        Assert.Contains("bug in dmg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCodeStillLandsOnMountFailedAndNamesTheOperation()
    {
        DmgError error = VolumeErrors.FromNative(0x0000_2001, VolumeOperation.SetMountPoint, @"E:\");

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("Assigning a drive letter", error.Message, StringComparison.Ordinal);
        Assert.Contains("SetVolumeMountPointW", error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOperationHasItsOwnNativeCallNameInTheDetail()
    {
        (VolumeOperation operation, string callName)[] cases =
        [
            (VolumeOperation.OpenDevice, "CreateFileW"),
            (VolumeOperation.GetDeviceNumber, "IOCTL_STORAGE_GET_DEVICE_NUMBER"),
            (VolumeOperation.EnumerateVolumes, "FindFirstVolumeW"),
            (VolumeOperation.GetVolumePathNames, "GetVolumePathNamesForVolumeNameW"),
            (VolumeOperation.SetMountPoint, "SetVolumeMountPointW"),
        ];

        foreach ((VolumeOperation operation, string callName) in cases)
        {
            DmgError error = VolumeErrors.FromNative(VolumeErrors.AccessDenied, operation, Subject);
            Assert.Contains(callName, error.Detail!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SuccessDoesNotDescribeAFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VolumeErrors.FromNative(VolumeErrors.Success, VolumeOperation.OpenDevice, Subject));

        Assert.True(VolumeErrors.Succeeded(VolumeErrors.Success));
        Assert.False(VolumeErrors.Succeeded(VolumeErrors.AccessDenied));
    }

    [Fact]
    public void EndOfEnumerationIsRecognisedAndNothingElseIsMistakenForIt()
    {
        Assert.True(VolumeErrors.IsEndOfEnumeration(VolumeErrors.NoMoreFiles));
        Assert.False(VolumeErrors.IsEndOfEnumeration(VolumeErrors.AccessDenied));
    }

    [Theory]
    [InlineData(VolumeErrors.AccessDenied)]
    [InlineData(VolumeErrors.NotReady)]
    [InlineData(VolumeErrors.SharingViolation)]
    [InlineData(VolumeErrors.FileNotFound)]
    [InlineData(VolumeErrors.PathNotFound)]
    [InlineData(VolumeErrors.InvalidHandle)]
    [InlineData(VolumeErrors.InvalidDrive)]
    [InlineData(VolumeErrors.FileInvalid)]
    [InlineData(VolumeErrors.UnrecognizedVolume)]
    [InlineData(VolumeErrors.NotAReparsePoint)]
    public void FailuresAboutOneVolumeAreRecognisedSoEnumerationCanSkipThem(uint code)
    {
        Assert.True(VolumeErrors.IsAboutOneVolume(code));
    }

    [Fact]
    public void ADirectoryNotEmptyFailureIsNotTreatedAsAboutOneVolume()
    {
        // DirectoryNotEmpty means "the requested letter is taken", not "skip this
        // volume" - conflating the two would make a failed --letter silently look
        // like discovery just found nothing.
        Assert.False(VolumeErrors.IsAboutOneVolume(VolumeErrors.DirectoryNotEmpty));
    }

    [Fact]
    public void EveryNativeErrorConstantThisToolNamesHasAWinerrorName()
    {
        uint[] codes =
        [
            VolumeErrors.Success,
            VolumeErrors.FileNotFound,
            VolumeErrors.PathNotFound,
            VolumeErrors.AccessDenied,
            VolumeErrors.InvalidHandle,
            VolumeErrors.InvalidDrive,
            VolumeErrors.NoMoreFiles,
            VolumeErrors.NotReady,
            VolumeErrors.SharingViolation,
            VolumeErrors.InvalidParameter,
            VolumeErrors.InvalidName,
            VolumeErrors.DirectoryNotEmpty,
            VolumeErrors.MoreData,
            VolumeErrors.UnrecognizedVolume,
            VolumeErrors.FileInvalid,
            VolumeErrors.NotAReparsePoint,
        ];

        foreach (uint code in codes)
        {
            Assert.NotEqual("unrecognised", VolumeErrors.NativeErrorName(code));
        }

        Assert.Equal("unrecognised", VolumeErrors.NativeErrorName(0xFFFF_FFFF));
    }
}
