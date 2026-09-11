using Dmg.Core;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// Pins the contract of <see cref="IVolumeService"/> by exercising it through the
/// fake - the same reasoning <c>FakeVirtualDiskServiceTests</c> is built on.
/// </summary>
public sealed class FakeVolumeServiceTests
{
    private const string PhysicalPath = @"\\.\PhysicalDrive3";
    private const string VolumeName = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    private static readonly StorageDeviceNumber Disk = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);

    [Fact]
    public void ReportsTheDeviceItWasToldAbout()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, Disk);

        Result<StorageDeviceNumber> device = volumes.GetDeviceNumber(PhysicalPath);

        Assert.True(device.TryGetValue(out StorageDeviceNumber? found));
        Assert.Equal(Disk, found);
    }

    [Fact]
    public void AnUnknownPathFailsRatherThanReturningAGuess()
    {
        FakeVolumeService volumes = new();

        Assert.False(volumes.GetDeviceNumber(@"\\.\PhysicalDrive9").Ok);
    }

    [Fact]
    public void AScriptedDeviceFailureIsReturnedVerbatim()
    {
        DmgError refusal = new(DmgExitCode.MountFailed, "That card reader has no card in it.");
        FakeVolumeService volumes = new();
        volumes.FailDeviceNumber(VolumeName, refusal);

        Result<StorageDeviceNumber> device = volumes.GetDeviceNumber(VolumeName);

        Assert.False(device.Ok);
        Assert.Same(refusal, device.Error);
    }

    [Fact]
    public void EnumerateVolumesListsEveryAddedVolumeInOrder()
    {
        FakeVolumeService volumes = new();
        volumes.AddVolume(VolumeName, Disk, @"E:\");
        volumes.AddVolume(@"\\?\Volume{2}\", Disk, @"F:\");

        Result<IReadOnlyList<string>> names = volumes.EnumerateVolumes();

        Assert.True(names.TryGetValue(out IReadOnlyList<string>? found));
        Assert.Equal([VolumeName, @"\\?\Volume{2}\"], found);
    }

    [Fact]
    public void EnumerateVolumesCanBeMadeToFail()
    {
        FakeVolumeService volumes = new() { EnumerateVolumesFailure = new DmgError(DmgExitCode.MountFailed, "broken") };

        Assert.False(volumes.EnumerateVolumes().Ok);
    }

    [Fact]
    public void GetVolumePathNamesReturnsWhatTheVolumeWasGiven()
    {
        FakeVolumeService volumes = new();
        volumes.AddVolume(VolumeName, Disk, @"E:\");

        Result<IReadOnlyList<string>> paths = volumes.GetVolumePathNames(VolumeName);

        Assert.True(paths.TryGetValue(out IReadOnlyList<string>? found));
        Assert.Equal([@"E:\"], found);
    }

    [Fact]
    public void AnUnknownVolumeFailsWhenAskedForItsPathNames()
    {
        FakeVolumeService volumes = new();

        Assert.False(volumes.GetVolumePathNames(VolumeName).Ok);
    }

    [Fact]
    public void GetVolumePathNamesCanFailForOneVolumeWithoutAffectingOthers()
    {
        FakeVolumeService volumes = new();
        FakeVolume broken = volumes.AddVolume(VolumeName, Disk);
        volumes.AddVolume(@"\\?\Volume{2}\", Disk, @"F:\");
        broken.PathNamesFailure = new DmgError(DmgExitCode.MountFailed, "broken");

        Assert.False(volumes.GetVolumePathNames(VolumeName).Ok);
        Assert.True(volumes.GetVolumePathNames(@"\\?\Volume{2}\").Ok);
    }

    [Fact]
    public void AssignDriveLetterGivesTheVolumeThatMountPoint()
    {
        FakeVolumeService volumes = new();
        FakeVolume volume = volumes.AddVolume(VolumeName, Disk);

        Result assigned = volumes.AssignDriveLetter(VolumeName, "E");

        Assert.True(assigned.Ok);
        Assert.Contains(@"E:\", volume.PathNames);
    }

    [Fact]
    public void AssignDriveLetterFailsClearlyWhenAnotherVolumeAlreadyHasThatLetter()
    {
        FakeVolumeService volumes = new();
        volumes.AddVolume(VolumeName, Disk);
        volumes.AddVolume(@"\\?\Volume{taken}\", Disk, @"E:\");

        Result assigned = volumes.AssignDriveLetter(VolumeName, "E");

        Assert.False(assigned.Ok);
        Assert.Equal(DmgExitCode.MountFailed, assigned.Error.Code);
        Assert.Contains("already in use", assigned.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignDriveLetterFailsForAVolumeThisFakeDoesNotKnowAbout()
    {
        FakeVolumeService volumes = new();

        Assert.False(volumes.AssignDriveLetter(VolumeName, "E").Ok);
    }

    [Fact]
    public void AScriptedAssignFailureIsReturnedVerbatim()
    {
        DmgError refusal = new(DmgExitCode.MountFailed, "no.");
        FakeVolumeService volumes = new() { AssignDriveLetterFailure = refusal };
        volumes.AddVolume(VolumeName, Disk);

        Result assigned = volumes.AssignDriveLetter(VolumeName, "E");

        Assert.False(assigned.Ok);
        Assert.Same(refusal, assigned.Error);
    }

    [Fact]
    public void RecordsEveryCallInOrderSoSequenceCanBeAsserted()
    {
        FakeVolumeService volumes = new();
        volumes.SetDevice(PhysicalPath, Disk);
        volumes.AddVolume(VolumeName, Disk);

        volumes.GetDeviceNumber(PhysicalPath);
        volumes.EnumerateVolumes();
        volumes.GetVolumePathNames(VolumeName);
        volumes.AssignDriveLetter(VolumeName, "E");

        Assert.Collection(
            volumes.Calls,
            call => Assert.StartsWith("GetDeviceNumber(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("EnumerateVolumes(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("GetVolumePathNames(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("AssignDriveLetter(", call, StringComparison.Ordinal));
    }
}
