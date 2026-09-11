using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// The identity drive-letter discovery joins on: device type and number, not number
/// alone.
/// </summary>
public sealed class StorageDeviceNumberTests
{
    [Fact]
    public void TheSameTypeAndNumberIsTheSameDeviceRegardlessOfPartition()
    {
        StorageDeviceNumber whole = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);
        StorageDeviceNumber partition = new(StorageDeviceNumber.Disk, 3, 1);

        Assert.True(whole.IsSameDeviceAs(partition));
        Assert.True(partition.IsSameDeviceAs(whole));
    }

    [Fact]
    public void TheSameNumberOnADifferentDeviceTypeIsADifferentDevice()
    {
        // A CD-ROM and a disk can both be device 1. Matching on the number alone
        // would occasionally pick a volume on somebody else's hardware.
        StorageDeviceNumber disk = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);
        StorageDeviceNumber cdRom = new(StorageDeviceNumber.CdRom, 3, StorageDeviceNumber.WholeDevice);

        Assert.False(disk.IsSameDeviceAs(cdRom));
    }

    [Fact]
    public void ADifferentNumberOnTheSameTypeIsADifferentDevice()
    {
        StorageDeviceNumber first = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);
        StorageDeviceNumber second = new(StorageDeviceNumber.Disk, 4, StorageDeviceNumber.WholeDevice);

        Assert.False(first.IsSameDeviceAs(second));
    }

    [Fact]
    public void IsSameDeviceAsIsFalseAgainstNullRatherThanThrowing()
    {
        StorageDeviceNumber disk = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);

        Assert.False(disk.IsSameDeviceAs(null));
    }

    [Fact]
    public void IsPartitionReflectsWhetherThisIsTheWholeDeviceOrOnePartOfIt()
    {
        StorageDeviceNumber whole = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);
        StorageDeviceNumber partition = new(StorageDeviceNumber.Disk, 3, 1);

        Assert.False(whole.IsPartition);
        Assert.True(partition.IsPartition);
    }

    [Fact]
    public void ToStringNamesThePhysicalDriveAndThePartitionWhenThereIsOne()
    {
        StorageDeviceNumber whole = new(StorageDeviceNumber.Disk, 3, StorageDeviceNumber.WholeDevice);
        StorageDeviceNumber partition = new(StorageDeviceNumber.Disk, 3, 2);

        Assert.Equal(@"\\.\PhysicalDrive3 (type 7)", whole.ToString());
        Assert.Equal(@"\\.\PhysicalDrive3 partition 2 (type 7)", partition.ToString());
    }
}
