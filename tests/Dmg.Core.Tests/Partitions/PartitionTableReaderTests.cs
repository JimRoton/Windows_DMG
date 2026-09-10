using System.Buffers.Binary;
using Dmg.Core.Partitions;

namespace Dmg.Core.Tests.Partitions;

/// <summary>
/// The gate in front of everything else: what is on this disk, and is the answer
/// trustworthy enough to hand a filesystem driver.
/// </summary>
/// <remarks>
/// The refusals matter as much as the successes here. A GPT whose checksum does
/// not match is not "a disk with some partitions on it that we should try anyway";
/// it is an image whose layout is unknown, and mounting it would hand Windows
/// sectors from somewhere the user did not ask for.
/// </remarks>
public sealed class PartitionTableReaderTests
{
    private const string AppleHfsType = "48465300-0000-11AA-AA11-00306543ECAC";
    private const string MicrosoftBasicData = "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7";
    private const string EfiSystemType = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";

    [Fact]
    public void AGptDiskReportsTypeGuidsRangesAndNames()
    {
        byte[] disk = SyntheticDisk.WithGpt(
            2048,
            new GptPartition(EfiSystemType, 40, 299, "EFI"),
            new GptPartition(AppleHfsType, 300, 1999, "Macintosh HD"));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.GuidPartitionTable, table!.Scheme);
        Assert.Equal(2, table.Partitions.Count);

        PartitionEntry efi = table.Partitions[0];
        Assert.Equal(1, efi.Number);
        Assert.Equal(EfiSystemType, efi.TypeGuid);
        Assert.Equal("EFI System", efi.TypeName);
        Assert.Equal("EFI", efi.Name);
        Assert.Equal(40u, efi.StartSector);
        Assert.Equal(299u, efi.EndSector);
        Assert.Equal(260u, efi.SectorCount);
        Assert.Equal(40 * 512, efi.ByteOffset);

        PartitionEntry hfs = table.Partitions[1];
        Assert.Equal(2, hfs.Number);
        Assert.Equal("Apple_HFS", hfs.TypeName);
        Assert.Equal("Macintosh HD", hfs.Name);
        Assert.Equal(1999u, hfs.EndSector);
        Assert.NotNull(hfs.UniqueGuid);
    }

    [Fact]
    public void AGptHeaderWhoseChecksumDoesNotMatchIsRefused()
    {
        byte[] disk = SyntheticDisk.WithGpt(2048, new GptPartition(AppleHfsType, 40, 1999, "HD"));

        // Move the entry array by one sector and leave the checksum alone: exactly
        // what a partly overwritten header looks like.
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(512 + 72), 3);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("checksum", read.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGptEntryArrayWhoseChecksumDoesNotMatchIsRefused()
    {
        byte[] disk = SyntheticDisk.WithGpt(2048, new GptPartition(AppleHfsType, 40, 1999, "HD"));

        // Repoint the first partition without touching the array checksum.
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan((2 * 512) + 32), 64);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("entries fail the checksum", read.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProtectiveMbrWithNoGptBehindItIsCorruptRatherThanAOneEeDisk()
    {
        byte[] disk = SyntheticDisk.WithMbr(2048, new MbrPartition(0xEE, 1, 2047));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("EFI PART", read.Error.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AGptHeaderThatDisagreesAboutItsOwnLbaIsRefused()
    {
        byte[] disk = SyntheticDisk.WithGpt(2048, new GptPartition(AppleHfsType, 40, 1999, "HD"));

        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(512 + 24), 7);
        SyntheticDisk.ResealGptHeader(disk);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Theory]
    [InlineData(91)]
    [InlineData(600)]
    public void AGptHeaderOfAnImpossibleSizeIsRefused(uint headerSize)
    {
        byte[] disk = SyntheticDisk.WithGpt(2048, new GptPartition(AppleHfsType, 40, 1999, "HD"));

        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(512 + 12), headerSize);

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void AnMbrDiskReportsEveryDeclaredPartitionInSlotOrder()
    {
        byte[] disk = SyntheticDisk.WithMbr(
            4096,
            new MbrPartition(0x07, 63, 2000, Status: 0x80),
            new MbrPartition(0x0B, 2063, 2000));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.MasterBootRecord, table!.Scheme);
        Assert.Equal(2, table.Partitions.Count);
        Assert.Equal((byte)0x07, table.Partitions[0].MbrType);
        Assert.Equal("exFAT or NTFS (0x07)", table.Partitions[0].TypeName);
        Assert.Equal(63u, table.Partitions[0].StartSector);
        Assert.Equal(2062u, table.Partitions[0].EndSector);
        Assert.Equal("FAT32 (0x0B)", table.Partitions[1].TypeName);
        Assert.Equal(2, table.Partitions[1].Number);
        Assert.Null(table.Partitions[1].TypeGuid);
    }

    [Fact]
    public void AnEmptySlotBetweenTwoPartitionsDoesNotConsumeANumber()
    {
        byte[] disk = SyntheticDisk.Blank(4096);
        SyntheticDisk.WriteMbr(
            disk,
            new MbrPartition(0x07, 63, 1000),
            default,
            new MbrPartition(0x0B, 1063, 1000));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal([1, 2], table!.Partitions.Select(entry => entry.Number));
    }

    [Fact]
    public void AnMbrPartitionRunningOffTheEndOfTheDiskIsRefused()
    {
        byte[] disk = SyntheticDisk.WithMbr(1024, new MbrPartition(0x07, 63, 100000));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("off the end of the disk", read.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMbrPartitionOfZeroLengthIsRefused()
    {
        byte[] disk = SyntheticDisk.WithMbr(1024, new MbrPartition(0x07, 63, 0));

        using MemoryStream stream = SyntheticDisk.Open(disk);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void AnEmptyDiskHasNoPartitionsAndSaysSo()
    {
        using MemoryStream stream = SyntheticDisk.Open([]);
        Result<PartitionTable> read = PartitionTableReader.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void TypeGuidsAreDecodedWithApplesMixedEndianness()
    {
        // The Apple_HFS type GUID as it sits on disk, straight out of hfsplus.dmg.
        byte[] raw =
        [
            0x00, 0x53, 0x46, 0x48, 0x00, 0x00, 0xAA, 0x11,
            0xAA, 0x11, 0x00, 0x30, 0x65, 0x43, 0xEC, 0xAC,
        ];

        Assert.Equal(AppleHfsType, GuidPartitionTable.FormatGuid(raw));
        Assert.Equal("Apple_HFS", GuidPartitionTable.DescribeType(GuidPartitionTable.FormatGuid(raw)));
    }

    [Fact]
    public void AnUnknownTypeIsNamedByItsGuidRatherThanCalledUnsupported()
    {
        Assert.Contains(
            "5A5A5A5A",
            GuidPartitionTable.DescribeType("5A5A5A5A-0000-0000-0000-000000000000"),
            StringComparison.Ordinal);

        Assert.Contains("0x2A", MasterBootRecord.DescribeType(0x2A), StringComparison.Ordinal);
    }

    [Fact]
    public void MicrosoftBasicDataIsRecognisedBecauseItIsTheOneWindowsMounts() =>
        Assert.Equal("Microsoft Basic Data", GuidPartitionTable.DescribeType(MicrosoftBasicData));
}
