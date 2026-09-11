using Dmg.Core;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// The join at the heart of S8.4 - matching a disk to its volumes by device number,
/// not by racing <c>GetLogicalDrives</c> - and the S8.5 assignment that rides on it.
/// </summary>
public sealed class DriveLetterDiscoveryTests
{
    private const string PhysicalPath = @"\\.\PhysicalDrive3";
    private const string VolumeName = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    private static readonly StorageDeviceNumber OurDisk = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);

    // FindVolumesOn

    [Fact]
    public void MatchesOnlyVolumesOnTheSameDeviceTypeAndNumber()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        volumes.AddVolume(VolumeName, OurDisk, @"E:\");
        volumes.AddVolume(
            @"\\?\Volume{other-number}\",
            new StorageDeviceNumber(StorageDeviceNumber.Disk, 4, StorageDeviceNumber.WholeDevice),
            @"F:\");
        volumes.AddVolume(
            @"\\?\Volume{other-type}\",
            new StorageDeviceNumber(StorageDeviceNumber.CdRom, 3, StorageDeviceNumber.WholeDevice),
            @"G:\");

        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath);

        Assert.True(found.TryGetValue(out IReadOnlyList<VolumeOnDisk>? matches));
        VolumeOnDisk match = Assert.Single(matches);
        Assert.Equal(VolumeName, match.VolumeName);
        Assert.Equal("E", match.DriveLetter);
    }

    [Fact]
    public void SkipsAVolumeThatRefusesToOpenAndStillFindsTheRest()
    {
        // A locked BitLocker volume or an empty card reader refuses to open. That
        // is not our disk's problem, and should not stop discovery from finding
        // the volume that is.
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.AddVolume(VolumeName, OurDisk, @"E:\");
        volumes.AddVolume(@"\\?\Volume{locked}\", OurDisk);
        volumes.FailDeviceNumber(@"\\?\Volume{locked}\", new DmgError(DmgExitCode.MountFailed, "locked"));

        RecordingOutput output = new();
        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath, output);

        Assert.True(found.TryGetValue(out IReadOnlyList<VolumeOnDisk>? matches));
        VolumeOnDisk match = Assert.Single(matches);
        Assert.Equal(VolumeName, match.VolumeName);
    }

    [Fact]
    public void FailsAtOnceWhenOurOwnDiskCannotBeIdentified()
    {
        FakeVolumeService volumes = new();
        volumes.FailDeviceNumber(PhysicalPath, new DmgError(DmgExitCode.MountFailed, "the disk was detached"));

        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath);

        Assert.False(found.Ok);
        Assert.Equal(DmgExitCode.MountFailed, found.Error.Code);
    }

    [Fact]
    public void FailsWhenTheMachinesVolumesCannotBeListed()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.EnumerateVolumesFailure = new DmgError(DmgExitCode.MountFailed, "enumeration broken");

        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath);

        Assert.False(found.Ok);
    }

    [Fact]
    public void AMatchingVolumeWithUnreadableMountPointsIsStillReportedWithNoPaths()
    {
        // The volume is ours and we know where it is; we just cannot ask where
        // Windows mounted it. That still proves the disk carries a volume, which
        // tells "no letter yet" apart from "Windows cannot read this filesystem".
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        FakeVolume volume = volumes.AddVolume(VolumeName, OurDisk);
        volume.PathNamesFailure = new DmgError(DmgExitCode.MountFailed, "broken");

        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath);

        Assert.True(found.TryGetValue(out IReadOnlyList<VolumeOnDisk>? matches));
        VolumeOnDisk match = Assert.Single(matches);
        Assert.Empty(match.PathNames);
        Assert.False(match.HasDriveLetter);
    }

    [Fact]
    public void NoMatchingVolumesIsANormalEmptyAnswerNotAFailure()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        Result<IReadOnlyList<VolumeOnDisk>> found = DriveLetterDiscovery.FindVolumesOn(volumes, PhysicalPath);

        Assert.True(found.TryGetValue(out IReadOnlyList<VolumeOnDisk>? matches));
        Assert.Empty(matches);
    }

    // WaitForVolume

    [Fact]
    public void WaitForVolumeReturnsAtOnceWhenAVolumeIsAlreadyThere()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.AddVolume(VolumeName, OurDisk, @"E:\");

        FakeDelay delay = new();
        Result<VolumeOnDisk> found = DriveLetterDiscovery.WaitForVolume(volumes, PhysicalPath, VolumeWaitPolicy.Once, delay);

        Assert.True(found.Ok);
        Assert.Empty(delay.Waits);
    }

    [Fact]
    public void WaitForVolumeRetriesUntilAVolumeAppears()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        FakeDelay delay = new(onWait: () => volumes.AddVolume(VolumeName, OurDisk, @"E:\"));

        Result<VolumeOnDisk> found = DriveLetterDiscovery.WaitForVolume(
            volumes, PhysicalPath, new VolumeWaitPolicy(3, TimeSpan.Zero), delay);

        Assert.True(found.TryGetValue(out VolumeOnDisk? volume));
        Assert.Equal("E", volume.DriveLetter);
        Assert.Single(delay.Waits);
    }

    [Fact]
    public void WaitForVolumeGivesUpWhenNothingEverAppears()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        FakeDelay delay = new();
        Result<VolumeOnDisk> found = DriveLetterDiscovery.WaitForVolume(
            volumes, PhysicalPath, new VolumeWaitPolicy(3, TimeSpan.Zero), delay);

        Assert.False(found.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, found.Error.Code);
        Assert.Contains("HFS+", found.Error.Message, StringComparison.Ordinal);

        // Waited between attempts 1-2 and 2-3, never after the last attempt.
        Assert.Equal(2, delay.Waits.Count);
    }

    [Fact]
    public void WaitForVolumePropagatesAFailureInsteadOfRetryingIt()
    {
        FakeVolumeService volumes = new();
        volumes.FailDeviceNumber(PhysicalPath, new DmgError(DmgExitCode.MountFailed, "gone"));

        FakeDelay delay = new();
        Result<VolumeOnDisk> found = DriveLetterDiscovery.WaitForVolume(
            volumes, PhysicalPath, new VolumeWaitPolicy(5, TimeSpan.Zero), delay);

        Assert.False(found.Ok);
        Assert.Empty(delay.Waits);
    }

    // WaitForDriveLetter

    [Fact]
    public void WaitForDriveLetterSucceedsOnceWindowsAssignsOne()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.AddVolume(VolumeName, OurDisk, @"E:\");

        Result<string> found = DriveLetterDiscovery.WaitForDriveLetter(volumes, PhysicalPath, VolumeWaitPolicy.Once);

        Assert.True(found.TryGetValue(out string? letter));
        Assert.Equal("E", letter);
    }

    [Fact]
    public void WaitForDriveLetterDistinguishesAVolumeWithNoLetterFromNoVolumeAtAll()
    {
        FakeVolumeService withVolume = new();
        withVolume.SetDevice(PhysicalPath, OurDisk);
        withVolume.AddVolume(VolumeName, OurDisk); // exists, but Windows gave it no letter

        FakeVolumeService withNoVolume = new();
        withNoVolume.SetDevice(PhysicalPath, OurDisk);

        VolumeWaitPolicy policy = new(2, TimeSpan.Zero);

        Result<string> noLetter = DriveLetterDiscovery.WaitForDriveLetter(withVolume, PhysicalPath, policy, new FakeDelay());
        Result<string> noVolume = DriveLetterDiscovery.WaitForDriveLetter(withNoVolume, PhysicalPath, policy, new FakeDelay());

        Assert.Equal(DmgExitCode.MountFailed, noLetter.Error.Code);
        Assert.Contains("--letter", noLetter.Error.Message, StringComparison.Ordinal);

        Assert.Equal(DmgExitCode.FilesystemNotMountable, noVolume.Error.Code);
    }

    [Fact]
    public void WaitForDriveLetterIgnoresAVolumeReachableOnlyByAFolderMountPoint()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.AddVolume(VolumeName, OurDisk, @"C:\mnt\image\");

        Result<string> found = DriveLetterDiscovery.WaitForDriveLetter(
            volumes, PhysicalPath, new VolumeWaitPolicy(1, TimeSpan.Zero), new FakeDelay());

        Assert.False(found.Ok);
        Assert.Equal(DmgExitCode.MountFailed, found.Error.Code);
    }

    // AssignDriveLetter (S8.5)

    [Theory]
    [InlineData("e")]
    [InlineData("E:")]
    [InlineData(@"E:\")]
    [InlineData("nope")]
    public void AssignDriveLetterRejectsAnythingThatIsNotAlreadyCanonical(string nonCanonical)
    {
        FakeVolumeService volumes = new();

        Assert.Throws<ArgumentException>(() =>
            DriveLetterDiscovery.AssignDriveLetter(volumes, PhysicalPath, nonCanonical));
    }

    [Fact]
    public void AssignDriveLetterWaitsForTheVolumeThenAssignsTheRequestedLetter()
    {
        // The disk was attached with ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER, so
        // there is no letter to wait for - only the volume itself.
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        FakeDelay delay = new(onWait: () => volumes.AddVolume(VolumeName, OurDisk));

        Result<string> assigned = DriveLetterDiscovery.AssignDriveLetter(
            volumes, PhysicalPath, "E", new VolumeWaitPolicy(3, TimeSpan.Zero), delay);

        Assert.True(assigned.TryGetValue(out string? letter));
        Assert.Equal("E", letter);
        Assert.Contains($"AssignDriveLetter({VolumeName}, E)", volumes.Calls);
    }

    [Fact]
    public void AssignDriveLetterFailsClearlyWhenTheLetterIsAlreadyTaken()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);
        volumes.AddVolume(VolumeName, OurDisk);
        volumes.AddVolume(
            @"\\?\Volume{other}\",
            new StorageDeviceNumber(StorageDeviceNumber.Disk, 9, StorageDeviceNumber.WholeDevice),
            @"E:\");

        Result<string> assigned = DriveLetterDiscovery.AssignDriveLetter(volumes, PhysicalPath, "E", VolumeWaitPolicy.Once);

        Assert.False(assigned.Ok);
        Assert.Equal(DmgExitCode.MountFailed, assigned.Error.Code);
        Assert.Contains("already in use", assigned.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignDriveLetterFailsWhenNoVolumeEverAppearsOnTheDisk()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, OurDisk);

        Result<string> assigned = DriveLetterDiscovery.AssignDriveLetter(
            volumes, PhysicalPath, "E", new VolumeWaitPolicy(2, TimeSpan.Zero), new FakeDelay());

        Assert.False(assigned.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, assigned.Error.Code);
    }
}
