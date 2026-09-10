using System.Buffers.Binary;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Partitions;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// The other two filesystems Windows can mount, and therefore the other two this
/// tool supports.
/// </summary>
/// <remarks>
/// Both keep the volume label somewhere other than the boot sector - FAT in a
/// root directory entry, NTFS in the <c>$Volume</c> record of the master file
/// table - so both probes have to read past the first sector to answer the
/// question a user actually asks, which is "which disk is this".
/// </remarks>
public sealed class FatAndNtfsProbeTests
{
    [Fact]
    public void AFat32VolumeIsIdentifiedByItsClusterCountAndLabelledFromItsRootDirectory()
    {
        byte[] volume = SyntheticVolumes.Fat32Disk("DMGFAT32");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Fat32, info!.Kind);
        Assert.Equal("FAT32", info.Name);
        Assert.Equal("DMGFAT32", info.VolumeLabel);
        Assert.Equal("0844-AF12", info.VolumeSerial);
        Assert.Equal(512, info.BytesPerCluster);
        Assert.True(info.WindowsCanMount);
    }

    [Fact]
    public void TheDirectoryEntryWinsOverTheBootSectorCopy()
    {
        // Relabelling a volume rewrites the directory entry and leaves the boot
        // sector's copy behind, so the two disagree on any disk that has been
        // renamed. The directory entry is the live one.
        byte[] volume = SyntheticVolumes.Fat32Disk("NEWNAME", bootSectorLabel: "OLDNAME");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal("NEWNAME", info!.VolumeLabel);
    }

    [Fact]
    public void TheBootSectorCopyIsUsedWhenTheRootDirectoryHasNoLabelEntry()
    {
        byte[] volume = SyntheticVolumes.Fat32Disk(label: null, bootSectorLabel: "FALLBACK");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal("FALLBACK", info!.VolumeLabel);
    }

    [Fact]
    public void APlaceholderBootSectorLabelIsNotALabel()
    {
        byte[] volume = SyntheticVolumes.Fat32Disk(label: null, bootSectorLabel: "NO NAME");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Null(info!.VolumeLabel);
    }

    [Fact]
    public void ALongNameFragmentIsNotMistakenForAVolumeLabel()
    {
        // A long-file-name entry sets every low attribute bit, the volume label
        // bit included. Reading it as the label yields mojibake.
        byte[] volume = SyntheticVolumes.Fat32Disk(label: null, bootSectorLabel: null);
        Span<byte> entry = volume.AsSpan(SyntheticVolumes.Fat32RootDirectoryOffset, 32);
        entry[0] = 0x41;
        entry[11] = 0x0F;

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Null(info!.VolumeLabel);
    }

    [Fact]
    public void AFat16VolumeIsIdentifiedFromItsFixedRootDirectory()
    {
        byte[] volume = SyntheticVolumes.Fat16Disk("SMALLVOL");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Fat16, info!.Kind);
        Assert.Equal("SMALLVOL", info.VolumeLabel);
        Assert.True(info.WindowsCanMount);
    }

    [Fact]
    public void AFatVolumeWhoseMetadataFillsItIsRefused()
    {
        byte[] volume = SyntheticVolumes.Fat16Disk("SMALLVOL");
        BinaryPrimitives.WriteUInt16LittleEndian(volume.AsSpan(19), 8);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void AFatVolumeWithNoFatIsRefused()
    {
        byte[] volume = SyntheticVolumes.Fat16Disk("SMALLVOL");
        BinaryPrimitives.WriteUInt16LittleEndian(volume.AsSpan(22), 0);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void AnNtfsVolumeIsIdentifiedAndLabelledFromItsVolumeRecord()
    {
        byte[] volume = SyntheticVolumes.NtfsDisk("WINDOWS");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Ntfs, info!.Kind);
        Assert.Equal("NTFS", info.Name);
        Assert.Equal("WINDOWS", info.VolumeLabel);
        Assert.Equal("90AB-CDEF", info.VolumeSerial);
        Assert.Equal(4096, info.BytesPerCluster);
        Assert.True(info.WindowsCanMount);
    }

    [Fact]
    public void AnNtfsVolumeWithNoNameAttributeHasNoLabel()
    {
        byte[] volume = SyntheticVolumes.NtfsDisk(label: null);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Ntfs, info!.Kind);
        Assert.Null(info.VolumeLabel);
    }

    [Fact]
    public void AVolumeRecordWhoseFixupsDoNotMatchIsRefused()
    {
        // Two bytes per sector of every MFT record are held elsewhere; a record
        // whose sector does not end in the sequence number was written by
        // something that did not understand the format, or was damaged.
        byte[] volume = SyntheticVolumes.NtfsDisk("WINDOWS");
        int record = (4 * 8 * 512) + (3 * 1024);
        volume[record + 510] ^= 0xFF;

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
        Assert.Contains("update sequence", probed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnNtfsVolumeWithAnImpossibleRecordSizeIsRefused()
    {
        byte[] volume = SyntheticVolumes.NtfsDisk("WINDOWS");
        volume[64] = unchecked((byte)-40);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void TheRealFat32FixtureIsIdentifiedWithTheLabelHdiutilGaveIt()
    {
        if (DmgBlockStreamFixtureTests.Skipped("fat32.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> table = PartitionTableReader.Read(image);
        Assert.True(table.Ok, table.Ok ? "" : table.Error.ToString());

        PartitionEntry partition = Assert.Single(table.Value!.Partitions);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(image, partition);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Fat32, info!.Kind);
        Assert.Equal("DMGFAT32", info.VolumeLabel);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", info.VolumeSerial ?? "");
        Assert.True(info.WindowsCanMount);
    }
}
