using System.Buffers.Binary;
using System.Text;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Partitions;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;
using Dmg.Core.Tests.Partitions;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// Which volume gets mounted, and what happens when the answer is not obvious.
/// </summary>
/// <remarks>
/// The default rule is "the single mountable volume" - not the first, not the
/// largest. An image with one exFAT volume needs no argument; an image with two
/// gets an error listing both, because guessing which half of somebody's disk they
/// meant is not a service. An image with none exits 5 saying what is actually in
/// it.
/// </remarks>
public sealed class VolumeMapTests
{
    [Fact]
    public void OneMountableVolumeIsTheDefaultWithNoArgument()
    {
        using MemoryStream disk = new(Disk(("exfat", "ONLYONE")), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);

        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = map!.Select(null);

        Assert.True(selected.TryGetValue(out DiskVolume? volume), selected.Ok ? "" : selected.Error.ToString());
        Assert.Equal(1, volume!.Number);
        Assert.Equal("ONLYONE", volume.Filesystem.VolumeLabel);
        Assert.True(volume.IsMountable);
    }

    [Fact]
    public void TwoMountableVolumesAreAnErrorThatListsThemBothRatherThanAGuess()
    {
        using MemoryStream disk = new(Disk(("exfat", "FIRST"), ("exfat", "SECOND")), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);

        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(2, map!.Mountable.Count);

        Result<DiskVolume> selected = map.Select(null);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.UsageError, selected.Error.Code);
        Assert.Contains("--partition", selected.Error.Message, StringComparison.Ordinal);
        Assert.Contains("FIRST", selected.Error.Message, StringComparison.Ordinal);
        Assert.Contains("SECOND", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitPartitionIsHonouredWhenThereAreSeveral()
    {
        using MemoryStream disk = new(Disk(("exfat", "FIRST"), ("exfat", "SECOND")), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(2);

        Assert.True(selected.TryGetValue(out DiskVolume? volume), selected.Ok ? "" : selected.Error.ToString());
        Assert.Equal("SECOND", volume!.Filesystem.VolumeLabel);
    }

    [Fact]
    public void NoMountableVolumeExitsFiveNamingWhatWasFound()
    {
        using MemoryStream disk = new(Disk(("hfs", null)), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(null);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, selected.Error.Code);
        Assert.Contains("HFS+", selected.Error.Message, StringComparison.Ordinal);
        Assert.Contains("dmg extract", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralUnmountableVolumesAreAllNamedInTheRefusal()
    {
        using MemoryStream disk = new(Disk(("hfs", null), ("apfs", null)), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(null);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, selected.Error.Code);
        Assert.Contains("HFS+", selected.Error.Message, StringComparison.Ordinal);
        Assert.Contains("APFS", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForAnUnmountableVolumeByNumberIsRefusedByFilesystemName()
    {
        using MemoryStream disk = new(Disk(("exfat", "GOOD"), ("hfs", null)), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(2);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, selected.Error.Code);
        Assert.Contains("HFS+", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneUnmountableVolumeDoesNotStopTheOtherFromBeingTheDefault()
    {
        using MemoryStream disk = new(Disk(("exfat", "GOOD"), ("hfs", null)), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(null);

        Assert.True(selected.TryGetValue(out DiskVolume? volume), selected.Ok ? "" : selected.Error.ToString());
        Assert.Equal("GOOD", volume!.Filesystem.VolumeLabel);
    }

    [Fact]
    public void APartitionNumberThatDoesNotExistIsAUsageErrorListingTheOnesThatDo()
    {
        using MemoryStream disk = new(Disk(("exfat", "ONLYONE")), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(disk);
        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = read.Value!.Select(7);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.UsageError, selected.Error.Code);
        Assert.Contains("no partition 7", selected.Error.Message, StringComparison.Ordinal);
        Assert.Contains("ONLYONE", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVolumeThatWillNotProbeIsListedAndRefusedButDoesNotSinkTheMap()
    {
        byte[] disk = Disk(("exfat", "GOOD"), ("exfat", "BROKEN"));

        // Break the second volume's exFAT geometry, leaving its signature intact.
        int second = (1 + 2048) * SyntheticVolumes.SectorSize;
        disk[second + 108] = 30;

        using MemoryStream stream = new(disk, writable: false);
        Result<VolumeMap> read = VolumeMap.Read(stream);

        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(2, map!.Volumes.Count);
        Assert.Single(map.Mountable);
        Assert.NotNull(map.Volumes[1].ProbeFailure);

        Result<DiskVolume> byNumber = map.Select(2);
        Assert.False(byNumber.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, byNumber.Error.Code);

        Result<DiskVolume> byDefault = map.Select(null);
        Assert.True(byDefault.Ok, byDefault.Ok ? "" : byDefault.Error.ToString());
        Assert.Equal("GOOD", byDefault.Value!.Filesystem.VolumeLabel);
    }

    [Fact]
    public void FreeSpaceIsNeverAMountCandidateAndSaysSoWhenAskedFor()
    {
        // An Apple partition map with the map itself, one HFS+ volume and a run of
        // free space, so free space carries a partition number of its own.
        using MemoryStream stream = new(AppleDisk(), writable: false);
        Result<VolumeMap> read = VolumeMap.Read(stream);

        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

        DiskVolume free = Assert.Single(map!.Volumes, volumeEntry => volumeEntry.Partition.IsFreeSpace);
        Assert.False(free.IsMountable);

        Result<DiskVolume> selected = map.Select(free.Number);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, selected.Error.Code);
        Assert.Contains("free space", selected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeDiskImageHasExactlyOneVolumeAndNeedsNoArgument()
    {
        byte[] disk = SyntheticVolumes.ExfatDisk(2048, "WHOLE");

        using MemoryStream stream = new(disk, writable: false);
        Result<VolumeMap> read = VolumeMap.Read(stream);

        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(PartitionScheme.WholeDisk, map!.Scheme);

        Result<DiskVolume> selected = map.Select(null);

        Assert.True(selected.TryGetValue(out DiskVolume? volume), selected.Ok ? "" : selected.Error.ToString());
        Assert.Equal("WHOLE", volume!.Filesystem.VolumeLabel);
        Assert.Equal(0, volume.ByteOffset);
    }

    [Fact]
    public void ADiskWhosePartitionTableIsDamagedFailsToReadAtAll()
    {
        byte[] disk = SyntheticDisk.WithMbr(2048, new MbrPartition(0xEE, 1, 2047));

        using MemoryStream stream = new(disk, writable: false);
        Result<VolumeMap> read = VolumeMap.Read(stream);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, read.Error.Code);
    }

    [Fact]
    public void TheTwoPartitionFixtureNeedsAPartitionArgumentAndTakesOne()
    {
        if (DmgBlockStreamFixtureTests.Skipped("multipart.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<VolumeMap> read = VolumeMap.Read(image);
        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(2, map!.Mountable.Count);

        Result<DiskVolume> ambiguous = map.Select(null);
        Assert.False(ambiguous.Ok);
        Assert.Equal(DmgExitCode.UsageError, ambiguous.Error.Code);

        Result<DiskVolume> chosen = map.Select(1);
        Assert.True(chosen.TryGetValue(out DiskVolume? volume), chosen.Ok ? "" : chosen.Error.ToString());
        Assert.Equal(FilesystemKind.ExFat, volume!.Filesystem.Kind);

        Console.WriteLine($"multipart.dmg: {ambiguous.Error.Message}");
    }

    [Theory]
    [InlineData("exfat-zlib.dmg")]
    [InlineData("fat32.dmg")]
    public void ASingleVolumeFixtureMountsWithNoArgument(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<VolumeMap> read = VolumeMap.Read(image);
        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = map!.Select(null);

        Assert.True(selected.TryGetValue(out DiskVolume? volume), selected.Ok ? "" : selected.Error.ToString());
        Assert.True(volume!.IsMountable);
        Assert.Equal(volume.Partition.ByteOffset, volume.ByteOffset);

        Console.WriteLine($"{name}: {volume}");
    }

    [Theory]
    [InlineData("hfsplus.dmg")]
    [InlineData("apfs.dmg")]
    public void AnAppleFixtureRefusesWithExitCodeFiveAndNamesTheFilesystem(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<VolumeMap> read = VolumeMap.Read(image);
        Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

        Result<DiskVolume> selected = map!.Select(null);

        Assert.False(selected.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, selected.Error.Code);
        Assert.Contains("dmg extract", selected.Error.Message, StringComparison.Ordinal);

        Console.WriteLine($"{name}: {selected.Error.Message}");
    }

    /// <summary>
    /// An MBR disk carrying one 2048-sector volume per entry, each of the given
    /// kind: <c>exfat</c>, <c>hfs</c> or <c>apfs</c>.
    /// </summary>
    private static byte[] Disk(params (string Kind, string? Label)[] volumes)
    {
        const int volumeSectors = 2048;
        byte[] disk = new byte[(1 + (volumes.Length * volumeSectors)) * SyntheticVolumes.SectorSize];
        MbrPartition[] entries = new MbrPartition[volumes.Length];

        for (int index = 0; index < volumes.Length; index++)
        {
            byte[] contents = volumes[index].Kind switch
            {
                "exfat" => SyntheticVolumes.ExfatDisk(volumeSectors, volumes[index].Label),
                "hfs" => SyntheticVolumes.HfsPlusDisk(volumeSectors),
                "apfs" => SyntheticVolumes.ApfsDisk(volumeSectors),
                _ => throw new ArgumentOutOfRangeException(nameof(volumes), volumes[index].Kind, "Unknown kind."),
            };

            int start = 1 + (index * volumeSectors);
            contents.CopyTo(disk.AsSpan(start * SyntheticVolumes.SectorSize));
            entries[index] = new MbrPartition(0x07, (uint)start, volumeSectors);
        }

        SyntheticDisk.WriteMbr(disk, entries);

        return disk;
    }

    /// <summary>
    /// An Apple-partitioned disk: a driver descriptor map at block 0, three map
    /// entries at blocks 1 to 3, an HFS+ volume from block 4, and free space after
    /// it.
    /// </summary>
    private static byte[] AppleDisk()
    {
        const int mapBlocks = 3;
        const int volumeStart = 4;
        const int volumeBlocks = 1024;
        const int freeBlocks = 64;

        byte[] disk = new byte[(volumeStart + volumeBlocks + freeBlocks) * SyntheticVolumes.SectorSize];
        SyntheticVolumes.HfsPlusDisk(volumeBlocks)
            .CopyTo(disk.AsSpan(volumeStart * SyntheticVolumes.SectorSize));

        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(0), 0x4552);
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), SyntheticVolumes.SectorSize);

        WriteMapEntry(disk, 1, "Apple_partition_map", 1, mapBlocks);
        WriteMapEntry(disk, 2, "Apple_HFS", volumeStart, volumeBlocks);
        WriteMapEntry(disk, 3, "Apple_Free", volumeStart + volumeBlocks, freeBlocks);

        return disk;
    }

    /// <summary>Writes one Apple partition map entry.</summary>
    private static void WriteMapEntry(byte[] disk, int block, string type, uint start, uint blocks)
    {
        Span<byte> entry = disk.AsSpan(block * SyntheticVolumes.SectorSize, SyntheticVolumes.SectorSize);

        BinaryPrimitives.WriteUInt16BigEndian(entry, 0x504D);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], 3);
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], start);
        BinaryPrimitives.WriteUInt32BigEndian(entry[12..], blocks);
        Encoding.ASCII.GetBytes(type).CopyTo(entry[48..]);
    }
}
