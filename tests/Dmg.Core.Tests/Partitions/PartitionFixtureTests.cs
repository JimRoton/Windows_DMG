using Dmg.Core.Imaging;
using Dmg.Core.Partitions;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Partitions;

/// <summary>
/// The partition readers against disks Apple laid out, rather than disks we did.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic tests prove the field arithmetic. These prove the fields are the
/// ones <c>hdiutil</c> actually writes - which is not a formality: the exFAT and
/// FAT32 fixtures turned out to be MBR disks whose single partition starts at LBA
/// 1, not at the 2048 every "standard layout" document assumes, and the Apple
/// images are GPT with the payload at LBA 40.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> <c>fixtures/generated/</c> is gitignored and
/// hdiutil is macOS-only, so a machine with no corpus reports that and passes.
/// </para>
/// </remarks>
public sealed class PartitionFixtureTests
{
    /// <summary>The fixtures whose disks carry a real master boot record.</summary>
    public static TheoryData<string, byte> MbrFixtures() => new()
    {
        { "exfat-zlib.dmg", 0x07 },
        { "exfat-sparse.dmg", 0x07 },
        { "exfat-udro.dmg", 0x07 },
        { "zerofill.dmg", 0x07 },
        { "fat32.dmg", 0x0B },
    };

    /// <summary>The fixtures whose disks carry a GUID partition table.</summary>
    public static TheoryData<string, string> GptFixtures() => new()
    {
        { "hfsplus.dmg", "Apple_HFS" },
        { "apfs.dmg", "Apple_APFS" },
    };

    [Theory]
    [MemberData(nameof(MbrFixtures))]
    public void AnMbrFixtureReadsBackAsOnePartitionOfTheExpectedType(string name, byte expectedType)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream disk = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> read = PartitionTableReader.Read(disk);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.MasterBootRecord, table!.Scheme);
        PartitionEntry only = Assert.Single(table.Partitions);
        Assert.Equal(expectedType, only.MbrType);
        Assert.True(only.StartSector > 0, $"{name}: the payload cannot start at sector 0 behind an MBR.");
        Assert.True(
            only.EndSector < table.DiskSectors,
            $"{name}: the partition runs to sector {only.EndSector} of a {table.DiskSectors}-sector disk.");
    }

    [Fact]
    public void TheTwoPartitionFixtureReadsBackAsTwoPartitions()
    {
        if (DmgBlockStreamFixtureTests.Skipped("multipart.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream disk = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> read = PartitionTableReader.Read(disk);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.MasterBootRecord, table!.Scheme);
        Assert.Equal(2, table.Partitions.Count);
        Assert.All(table.Partitions, entry => Assert.Equal((byte)0x07, entry.MbrType));
        Assert.True(
            table.Partitions[0].EndSector < table.Partitions[1].StartSector,
            "The two partitions overlap, which no partitioner would write.");
    }

    [Theory]
    [MemberData(nameof(GptFixtures))]
    public void AGptFixturePassesItsOwnChecksumsAndNamesItsPayload(string name, string expectedTypeName)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream disk = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> read = PartitionTableReader.Read(disk);

        Assert.True(read.TryGetValue(out PartitionTable? table), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.GuidPartitionTable, table!.Scheme);
        Assert.NotNull(table.DiskGuid);

        PartitionEntry payload = Assert.Single(
            table.Partitions,
            entry => string.Equals(entry.TypeName, expectedTypeName, StringComparison.Ordinal));

        Assert.NotEqual(0u, payload.StartSector);
        Assert.False(string.IsNullOrEmpty(payload.Name), $"{name}: the GPT entry carries no name.");
        Assert.False(string.IsNullOrEmpty(payload.TypeGuid));
    }

    [Fact]
    public void ADamagedGptHeaderInARealImageIsRefusedRatherThanWorkedAround()
    {
        // Read a real GPT disk, flip one byte of the header, and check the reader
        // notices - the same disk, the only difference being the damage.
        if (DmgBlockStreamFixtureTests.Skipped("hfsplus.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream disk = DmgBlockStreamFixtureTests.Open(file);

        byte[] head = new byte[64 * 512];
        disk.Position = 0;
        disk.ReadExactly(head);

        head[512 + 48] ^= 0x01;

        using MemoryStream damaged = SyntheticDisk.Open(head);
        Result<PartitionTable> read = PartitionTableReader.Read(damaged);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
        Assert.Contains("checksum", read.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
