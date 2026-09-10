using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Checks the CHS derivation against the worked examples in the VHD specification's
/// CHS appendix. These are the geometries Virtual PC, Hyper-V and Disk Management
/// produce for the same sizes, so a disagreement here is a disagreement with the
/// tool that has to attach the result.
/// </summary>
public sealed class VhdGeometryTests
{
    [Theory]
    // 16 MB - the smallest size the specification's worked example covers, and the
    // canonical 481/4/17 that every VHD implementation produces.
    [InlineData(16L * 1024 * 1024, 481, 4, 17)]
    // 1 GB. The 17-sector branch overflows the head count, falls through to 31 and
    // then to 63; the answer is the familiar 2080/16/63.
    [InlineData(1024L * 1024 * 1024, 2080, 16, 63)]
    // 2 GB, same branch, twice the cylinders.
    [InlineData(2048L * 1024 * 1024, 4161, 16, 63)]
    // 127.5 GB: exactly 65535 * 16 * 255 sectors, the largest CHS can address.
    [InlineData(65535L * 16 * 255 * 512, 65535, 16, 255)]
    public void MatchesTheSpecificationsWorkedGeometries(
        long diskSize,
        int cylinders,
        int heads,
        int sectorsPerTrack)
    {
        VhdGeometry geometry = VhdGeometry.ForDiskSize(diskSize);

        Assert.Equal((ushort)cylinders, geometry.Cylinders);
        Assert.Equal((byte)heads, geometry.Heads);
        Assert.Equal((byte)sectorsPerTrack, geometry.SectorsPerTrack);
    }

    [Fact]
    public void SwitchesToTheLargeDiskBranchAtExactlyThe63SectorThreshold()
    {
        // The specification's first test is `totalSectors >= 65535 * 16 * 63`, so the
        // sector immediately below the threshold must still take the small branch.
        const long ThresholdSectors = 65535L * 16 * 63;

        VhdGeometry atThreshold = VhdGeometry.ForSectorCount(ThresholdSectors);
        VhdGeometry belowThreshold = VhdGeometry.ForSectorCount(ThresholdSectors - 1);

        Assert.Equal(255, atThreshold.SectorsPerTrack);
        Assert.Equal(16, atThreshold.Heads);
        Assert.Equal(16191, atThreshold.Cylinders);

        Assert.Equal(63, belowThreshold.SectorsPerTrack);
        Assert.Equal(16, belowThreshold.Heads);
    }

    [Fact]
    public void ClampsRatherThanFailingAboveTheCylinderHeadSectorCeiling()
    {
        // A 500 GB disk is far past what CHS can address. The specification says to
        // pin the geometry at the maximum; the real capacity rides in the size
        // fields instead.
        VhdGeometry huge = VhdGeometry.ForDiskSize(500L * 1024 * 1024 * 1024);

        Assert.Equal(new VhdGeometry(65535, 16, 255), huge);
        Assert.Equal(VhdGeometry.MaxAddressableSectors, huge.TotalSectors);
    }

    [Fact]
    public void UsesTheSeventeenSectorBranchForSmallDisks()
    {
        // 100 MB stays inside the first branch: heads is rounded up from
        // cylinderTimesHeads and never reaches the 16-head fallbacks.
        // 204800 sectors / 17 = 12047; heads = ceil(12047 / 1024) = 12;
        // cylinders = 12047 / 12 = 1003.
        VhdGeometry geometry = VhdGeometry.ForDiskSize(100L * 1024 * 1024);

        Assert.Equal(17, geometry.SectorsPerTrack);
        Assert.Equal(12, geometry.Heads);
        Assert.Equal(1003, geometry.Cylinders);
    }

    [Fact]
    public void AddressesNoMoreSectorsThanTheDiskActuallyHas()
    {
        // Truncating division means the geometry usually addresses slightly fewer
        // sectors than the disk holds. What it must never do is claim more.
        long[] sizes =
        [
            3L * 1024 * 1024,
            16L * 1024 * 1024,
            700L * 1024 * 1024,
            4L * 1024 * 1024 * 1024,
            120L * 1024 * 1024 * 1024,
        ];

        foreach (long size in sizes)
        {
            VhdGeometry geometry = VhdGeometry.ForDiskSize(size);

            Assert.True(
                geometry.AddressableBytes <= size,
                $"{size} bytes produced geometry {geometry}, which addresses {geometry.AddressableBytes} bytes.");
        }
    }

    [Fact]
    public void RoundTripsThroughItsFourPackedBigEndianBytes()
    {
        VhdGeometry geometry = new(481, 4, 17);

        Span<byte> packed = stackalloc byte[VhdGeometry.Length];
        geometry.WriteTo(packed);

        // 481 == 0x01E1, big-endian, then one byte each for heads and sectors.
        Assert.Equal(0x01, packed[0]);
        Assert.Equal(0xE1, packed[1]);
        Assert.Equal(0x04, packed[2]);
        Assert.Equal(0x11, packed[3]);

        Assert.Equal(geometry, VhdGeometry.ReadFrom(packed));
    }

    [Fact]
    public void RejectsSizesThatAreNotAWholeNumberOfSectors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VhdGeometry.ForDiskSize(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => VhdGeometry.ForDiskSize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VhdGeometry.ForSectorCount(-1));
    }

    [Fact]
    public void PrintsTheWayDiskToolsPrintGeometry() =>
        Assert.Equal("2080/16/63", new VhdGeometry(2080, 16, 63).ToString());
}
