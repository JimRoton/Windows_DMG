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
/// The primary success path: an exFAT volume, identified, labelled and declared
/// mountable.
/// </summary>
/// <remarks>
/// The label is the part worth being careful about. It lives in a root directory
/// entry rather than in the boot sector, so a probe that reads only the boot
/// sector reports a serial and no name - which is exactly the thing a user
/// recognises their disk by. Both the synthetic volumes and the real fixtures
/// check that the directory walk actually happens.
/// </remarks>
public sealed class ExFatProbeTests
{
    [Fact]
    public void AnExfatVolumeIsIdentifiedWithItsLabelSerialAndGeometry()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.ExFat, info!.Kind);
        Assert.Equal("exFAT", info.Name);
        Assert.Equal("DMGFIX", info.VolumeLabel);
        Assert.Equal("6AA2-D346", info.VolumeSerial);
        Assert.Equal(512, info.BytesPerSector);
        Assert.Equal(4096, info.BytesPerCluster);
        Assert.Equal(2048L * 512, info.VolumeBytes);
        Assert.True(info.WindowsCanMount);
    }

    [Fact]
    public void AVolumeWithNoLabelEntryReportsNoLabelRatherThanAPlaceholder()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, label: null);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Null(info!.VolumeLabel);
        Assert.Equal(FilesystemKind.ExFat, info.Kind);
    }

    [Fact]
    public void ALabelEntryMarkedNotInUseMeansTheVolumeHasNoLabel()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "IGNORED");
        volume[SyntheticVolumes.ClusterOffset(SyntheticVolumes.RootDirectoryCluster)] = 0x03;

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Null(info!.VolumeLabel);
    }

    [Fact]
    public void ALabelLaterInTheRootDirectoryIsStillFound()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, label: null);
        int root = SyntheticVolumes.ClusterOffset(SyntheticVolumes.RootDirectoryCluster);

        // Two allocation-bitmap-shaped entries first, then the label.
        volume[root] = 0x81;
        volume[root + 32] = 0x82;

        Span<byte> label = volume.AsSpan(root + 64, 32);
        label[0] = 0x83;
        label[1] = 5;
        Encoding.Unicode.GetBytes("LATER").CopyTo(label[2..]);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal("LATER", info!.VolumeLabel);
    }

    [Fact]
    public void ALabelOfAnImpossibleLengthIsRefused()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");
        volume[SyntheticVolumes.ClusterOffset(SyntheticVolumes.RootDirectoryCluster) + 1] = 40;

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void ARootDirectoryOutsideTheClusterHeapIsRefused()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(96), 999999);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
        Assert.Contains("root directory", probed.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClusterHeapLargerThanTheVolumeIsRefused()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(92), 100000);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void AFatThatOverlapsTheClusterHeapIsRefused()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(84), 4096);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
        Assert.Contains("overlaps itself", probed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImpossibleSectorSizeIsRefused()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "DMGFIX");
        volume[108] = 20;

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.False(probed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, probed.Error.Code);
    }

    [Fact]
    public void AVolumeThatIsNotAFilesystemAtAllIsUnknownRatherThanAFailure()
    {
        byte[] volume = new byte[64 * 512];

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Unknown, info!.Kind);
        Assert.False(info.WindowsCanMount);
        Assert.Null(info.VolumeLabel);
    }

    [Fact]
    public void AProbeIsRelativeToThePartitionAndNotToTheDisk()
    {
        // The same volume, at an offset, behind a partition table: the probe must
        // read the boot sector at the partition's offset and nowhere else.
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "OFFSET");
        byte[] disk = new byte[(1 + 2048) * 512];
        volume.CopyTo(disk.AsSpan(512));
        SyntheticDisk.WriteMbr(disk, new MbrPartition(0x07, 1, 2048));

        using MemoryStream stream = new(disk, writable: false);
        Result<PartitionTable> table = PartitionTableReader.Read(stream);
        Assert.True(table.Ok, table.Ok ? "" : table.Error.ToString());

        Result<FilesystemInfo> probed = FilesystemProbe.Probe(stream, table.Value!.Partitions[0]);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal("OFFSET", info!.VolumeLabel);
    }

    [Theory]
    [InlineData("exfat-zlib.dmg")]
    [InlineData("exfat-sparse.dmg")]
    [InlineData("exfat-udro.dmg")]
    [InlineData("zerofill.dmg")]
    public void ARealExfatFixtureIsIdentifiedWithTheLabelHdiutilGaveIt(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
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
        Assert.Equal(FilesystemKind.ExFat, info!.Kind);
        Assert.True(info.WindowsCanMount);

        // hdiutil names every exFAT fixture with a DMG-prefixed volume name, and
        // the manifest records which filesystem it asked for.
        Assert.False(
            string.IsNullOrEmpty(info.VolumeLabel),
            $"{name}: the volume label came back empty, so the root directory was not read.");
        Assert.StartsWith("DMG", info.VolumeLabel, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", info.VolumeSerial ?? "");
        Assert.Equal(512, info.BytesPerSector);
        Assert.True(info.BytesPerCluster >= 512);
    }

    [Fact]
    public void BothPartitionsOfTheTwoPartitionFixtureAreExfat()
    {
        if (DmgBlockStreamFixtureTests.Skipped("multipart.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> table = PartitionTableReader.Read(image);
        Assert.True(table.Ok, table.Ok ? "" : table.Error.ToString());

        List<string?> labels = [];

        foreach (PartitionEntry partition in table.Value!.Partitions)
        {
            Result<FilesystemInfo> probed = FilesystemProbe.Probe(image, partition);
            Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
            Assert.Equal(FilesystemKind.ExFat, info!.Kind);
            labels.Add(info.VolumeLabel);
        }

        Assert.Equal(2, labels.Count);
        Assert.Equal(2, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
