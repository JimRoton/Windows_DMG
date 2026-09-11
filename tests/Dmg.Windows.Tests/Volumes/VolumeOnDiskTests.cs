using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// Picking the drive letter, and only the drive letter, out of everywhere a volume
/// is reachable from.
/// </summary>
public sealed class VolumeOnDiskTests
{
    private const string VolumeName = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    private static readonly StorageDeviceNumber Device = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);

    [Fact]
    public void APlainLetterPathIsRecognisedAsTheDriveLetter()
    {
        VolumeOnDisk volume = new(VolumeName, Device, [@"E:\"]);

        Assert.Equal("E", volume.DriveLetter);
        Assert.True(volume.HasDriveLetter);
    }

    [Fact]
    public void AFolderMountPointIsNotMistakenForADriveLetter()
    {
        // C:\mnt\image\ is not something dmg unmount can be given.
        VolumeOnDisk volume = new(VolumeName, Device, [@"C:\mnt\image\"]);

        Assert.Null(volume.DriveLetter);
        Assert.False(volume.HasDriveLetter);
    }

    [Fact]
    public void TheLetterIsFoundEvenWhenAFolderMountPointComesFirst()
    {
        VolumeOnDisk volume = new(VolumeName, Device, [@"C:\mnt\image\", @"F:\"]);

        Assert.Equal("F", volume.DriveLetter);
    }

    [Fact]
    public void NoPathNamesAtAllMeansNoDriveLetter()
    {
        VolumeOnDisk volume = new(VolumeName, Device, []);

        Assert.Null(volume.DriveLetter);
        Assert.Empty(volume.PathNames);
    }

    [Fact]
    public void ANullPathNamesListIsTreatedAsEmptyRatherThanThrowing()
    {
        VolumeOnDisk volume = new(VolumeName, Device, null!);

        Assert.NotNull(volume.PathNames);
        Assert.Empty(volume.PathNames);
        Assert.False(volume.HasDriveLetter);
    }

    [Fact]
    public void ToStringNamesTheLetterOrSaysThereIsNone()
    {
        VolumeOnDisk withLetter = new(VolumeName, Device, [@"E:\"]);
        VolumeOnDisk withoutLetter = new(VolumeName, Device, []);

        Assert.Equal($"{VolumeName} at E:", withLetter.ToString());
        Assert.Equal($"{VolumeName} with no drive letter", withoutLetter.ToString());
    }
}
