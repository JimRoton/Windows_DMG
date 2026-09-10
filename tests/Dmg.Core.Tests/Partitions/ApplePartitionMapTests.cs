using System.Buffers.Binary;
using System.Text;
using Dmg.Core.Partitions;

namespace Dmg.Core.Tests.Partitions;

/// <summary>
/// The pre-GPT Apple layout: a driver descriptor map at block 0 and a chain of
/// <c>PM</c> entries after it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is synthetic, and deliberately so: <c>hdiutil</c> on macOS 15
/// will not author an Apple partition map any more - every fixture in the corpus
/// comes out as MBR or GPT - so an image of this shape can only be built by hand.
/// The layout is the one Apple documented and the one <c>Apple_HFS</c> images
/// from the era carry: big-endian throughout, 512-byte entries, each repeating
/// the map's own length.
/// </para>
/// </remarks>
public sealed class ApplePartitionMapTests
{
    [Fact]
    public void ADriverDescriptorMapAndItsEntriesReadBackAsPartitions()
    {
        byte[] disk = AppleDisk(
            2048,
            blockSize: 512,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 63),
            new ApmEntry("Macintosh HD", "Apple_HFS", 64, 1900),
            new ApmEntry("Extra", ApplePartitionMap.FreeSpaceType, 1964, 84));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.ApplePartitionMap, table!.Scheme);
        Assert.Equal(3, table.Partitions.Count);

        Assert.Equal(ApplePartitionMap.MapType, table.Partitions[0].TypeName);
        Assert.False(table.Partitions[0].IsFreeSpace);

        PartitionEntry volume = table.Partitions[1];
        Assert.Equal("Apple_HFS", volume.TypeName);
        Assert.Equal("Macintosh HD", volume.Name);
        Assert.Equal(64u, volume.StartSector);
        Assert.Equal(1963u, volume.EndSector);
        Assert.Equal(64 * 512, volume.ByteOffset);
        Assert.Null(volume.MbrType);
        Assert.Null(volume.TypeGuid);

        Assert.True(table.Partitions[2].IsFreeSpace);
        Assert.Single(
            table.Volumes,
            entry => string.Equals(entry.TypeName, "Apple_HFS", StringComparison.Ordinal));
    }

    [Fact]
    public void AMapWithNoDriverDescriptorMapInFrontOfItIsStillRead()
    {
        byte[] disk = AppleDisk(
            1024,
            blockSize: 512,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 63),
            new ApmEntry("Data", "Apple_HFS", 64, 900));

        // Wipe the ER signature; the PM entries alone still describe the disk.
        disk.AsSpan(0, 2).Clear();

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.ApplePartitionMap, table!.Scheme);
        Assert.Equal(2, table.Partitions.Count);
    }

    [Fact]
    public void BlocksLargerThanASectorAreConvertedToSectors()
    {
        byte[] disk = AppleDisk(
            4096,
            blockSize: 2048,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 15),
            new ApmEntry("Data", "Apple_HFS", 16, 200));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());

        PartitionEntry volume = table!.Partitions[1];
        Assert.Equal(16u * 4, volume.StartSector);
        Assert.Equal(200u * 4, volume.SectorCount);
    }

    [Fact]
    public void AMapThatEndsBeforeItSaidItWouldIsRefused()
    {
        byte[] disk = AppleDisk(
            1024,
            blockSize: 512,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 63),
            new ApmEntry("Data", "Apple_HFS", 64, 900));

        // Claim three entries but leave the third blank.
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(512 + 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan((2 * 512) + 4), 3);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("ends before it said", read.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverDescriptorMapWithNoPartitionMapBehindItIsRefused()
    {
        byte[] disk = SyntheticDisk.Blank(1024);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(0), ApplePartitionMap.DriverDescriptorSignature);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), 512);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("PM signature", read.Error.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void APartitionRunningOffTheEndOfTheDiskIsRefused()
    {
        byte[] disk = AppleDisk(
            1024,
            blockSize: 512,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 63),
            new ApmEntry("Data", "Apple_HFS", 64, 100000));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void ABlockSizeThatIsNotAMultipleOfASectorIsRefused()
    {
        byte[] disk = AppleDisk(
            1024,
            blockSize: 512,
            new ApmEntry("Apple", ApplePartitionMap.MapType, 1, 63),
            new ApmEntry("Data", "Apple_HFS", 64, 900));

        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), 777);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("block size", read.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>One synthetic map entry, in blocks rather than sectors.</summary>
    /// <param name="Name">pmPartName.</param>
    /// <param name="Type">pmParType.</param>
    /// <param name="Start">pmPyPartStart, in blocks.</param>
    /// <param name="Blocks">pmPartBlkCnt, in blocks.</param>
    private readonly record struct ApmEntry(string Name, string Type, uint Start, uint Blocks);

    /// <summary>
    /// A disk with a driver descriptor map at block 0 and one entry per partition
    /// after it, laid out exactly as Apple's documentation describes.
    /// </summary>
    private static byte[] AppleDisk(int sectors, int blockSize, params ApmEntry[] entries)
    {
        byte[] disk = SyntheticDisk.Blank(sectors);
        int sectorsPerBlock = blockSize / SyntheticDisk.SectorSize;

        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(0), ApplePartitionMap.DriverDescriptorSignature);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), (ushort)blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(4), (uint)(sectors / sectorsPerBlock));

        for (int index = 0; index < entries.Length; index++)
        {
            Span<byte> entry = disk.AsSpan(
                (index + 1) * sectorsPerBlock * SyntheticDisk.SectorSize,
                SyntheticDisk.SectorSize);

            BinaryPrimitives.WriteUInt16BigEndian(entry, ApplePartitionMap.MapEntrySignature);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)entries.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], entries[index].Start);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], entries[index].Blocks);
            Encoding.ASCII.GetBytes(entries[index].Name).CopyTo(entry[16..]);
            Encoding.ASCII.GetBytes(entries[index].Type).CopyTo(entry[48..]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[84..], entries[index].Blocks);
            BinaryPrimitives.WriteUInt32BigEndian(entry[88..], 0x33);
        }

        return disk;
    }
}
