using System.Buffers.Binary;
using Dmg.Core.Filesystems;
using Dmg.Core.Tests.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Partitions;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Partitions;

/// <summary>
/// Images that are one volume from end to end, and the line between those and
/// images whose partition table is damaged.
/// </summary>
/// <remarks>
/// <para>
/// These two look alike from a distance - neither yields a partition table - and
/// conflating them is the failure mode this file exists to prevent. "No partition
/// table" is a positive finding: a filesystem was recognised at sector 0.
/// Everything else that fails to parse is corruption, and is refused, because a
/// damaged GPT reported as one big volume would hand Windows sectors from
/// somewhere nobody asked for.
/// </para>
/// </remarks>
public sealed class WholeDiskTests
{
    [Fact]
    public void AnExfatVolumeWithNoPartitionTableIsOneWholeDiskVolume()
    {
        byte[] disk = SyntheticVolumes.ExfatDisk(2048, "NOPART");

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.True(table!.IsWholeDisk);
        Assert.Equal(PartitionScheme.WholeDisk, table.Scheme);

        PartitionEntry only = Assert.Single(table.Partitions);
        Assert.Equal(1, only.Number);
        Assert.Equal(0u, only.StartSector);
        Assert.Equal(2048u, only.SectorCount);
        Assert.Equal("exFAT (whole disk)", only.TypeName);
        Assert.False(only.IsFreeSpace);
    }

    [Fact]
    public void AWholeDiskExfatVolumeIsNotMistakenForAnMbrBecauseOfIts55AaSignature()
    {
        // The trap: an exFAT boot sector ends in 0x55AA, exactly like an MBR whose
        // four slots are empty. Read in the wrong order, this image reports
        // "a master boot record with no partitions in it".
        byte[] disk = SyntheticVolumes.ExfatDisk(2048, "TRAP");

        Assert.Equal(0x55, disk[510]);
        Assert.Equal(0xAA, disk[511]);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.WholeDisk, read.Value!.Scheme);
    }

    [Fact]
    public void ADiskOfNothingAtAllIsCorruptRatherThanOneEmptyVolume()
    {
        using MemoryStream stream = SyntheticDisk.Open(SyntheticDisk.Blank(64));
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("no partition table", read.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RandomBytesAtSectorZeroAreCorruptRatherThanOneWholeDiskVolume()
    {
        byte[] disk = SyntheticDisk.Blank(64);
        new Random(1979).NextBytes(disk.AsSpan(0, 4096));

        // Whatever those bytes are, they are not a filesystem. Make sure the two
        // signatures that would short-circuit the check are absent.
        disk[510] = 0;
        disk[511] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(512), 0);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(1024), 0);
        disk.AsSpan(32, 4).Clear();

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void ADamagedPartitionTableIsNeverReportedAsAWholeDiskImage()
    {
        // A protective MBR with no GPT behind it: the closest thing there is to an
        // image that could be mistaken for having no partition table at all.
        byte[] disk = SyntheticDisk.WithMbr(2048, new MbrPartition(0xEE, 1, 2047));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void AWholeDiskHfsPlusVolumeIsRecognisedFromItsVolumeHeader()
    {
        byte[] disk = SyntheticVolumes.HfsPlusDisk(2048);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.WholeDisk, table!.Scheme);
        Assert.Equal("HFS+ (whole disk)", table.Partitions[0].TypeName);
    }

    [Fact]
    public void ARealExfatVolumeLiftedOutOfItsPartitionReadsBackAsAWholeDiskImage()
    {
        // The partition contents of a real image, with the MBR left behind: a
        // genuine Apple-written exFAT volume that has no partition table in front
        // of it, which is exactly the shape this story is about.
        if (DmgBlockStreamFixtureTests.Skipped("exfat-zlib.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> partitioned = PartitionTableReader.Read(image);
        Assert.True(partitioned.Ok, partitioned.Ok ? "" : partitioned.Error.ToString());

        PartitionEntry payload = partitioned.Value!.Partitions[0];
        byte[] volume = new byte[Math.Min(payload.ByteLength, 4 * 1024 * 1024)];
        image.Position = payload.ByteOffset;
        image.ReadExactly(volume);

        using MemoryStream stream = SyntheticDisk.Open(volume);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.WholeDisk, table!.Scheme);
        Assert.Equal("exFAT (whole disk)", table.Partitions[0].TypeName);
    }

    [Theory]
    [InlineData(FilesystemKind.ExFat, true)]
    [InlineData(FilesystemKind.Fat32, true)]
    [InlineData(FilesystemKind.Ntfs, true)]
    [InlineData(FilesystemKind.HfsPlus, false)]
    [InlineData(FilesystemKind.Apfs, false)]
    [InlineData(FilesystemKind.Unknown, false)]
    public void MountabilityIsAPropertyOfTheFilesystemAndNotOfTheLayout(
        FilesystemKind kind,
        bool expected) =>
        Assert.Equal(expected, FilesystemSignature.WindowsCanMount(kind));

    [Fact]
    public void ASignatureIsOnlyAcceptedWhereTheFormatPutsIt()
    {
        // "EXFAT" further into the sector than offset 3 is data, not a filesystem.
        byte[] disk = SyntheticDisk.Blank(64);
        "EXFAT   "u8.CopyTo(disk.AsSpan(64));
        disk[510] = 0x55;
        disk[511] = 0xAA;

        Assert.Equal(FilesystemKind.Unknown, FilesystemSignature.Recognize(disk));
    }
}
