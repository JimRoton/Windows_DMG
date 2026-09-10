using System.Buffers.Binary;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Partitions;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// The refusal path, which has to be as good as the success path.
/// </summary>
/// <remarks>
/// A user with an HFS+ image is going to be told no whatever happens. What the
/// message says decides whether they understand why - a Windows machine has no
/// HFS+ driver and never will - or whether they try three more times and file a
/// bug. So these tests check the wording, not only the exit code: the filesystem
/// has to be named, and the alternative has to be offered.
/// </remarks>
public sealed class AppleFilesystemRefusalTests
{
    [Fact]
    public void AnHfsPlusVolumeIsIdentifiedAsHfsPlusAndRefusedByName()
    {
        byte[] volume = SyntheticVolumes.HfsPlusDisk(2048);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.HfsPlus, info!.Kind);
        Assert.Equal("HFS+", info.Name);
        Assert.False(info.WindowsCanMount);
        Assert.Equal(4096, info.BytesPerCluster);

        Result mounted = info.EnsureWindowsCanMount();

        Assert.False(mounted.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, mounted.Error.Code);
        Assert.Contains("HFS+", mounted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Windows cannot mount", mounted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("dmg extract", mounted.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApfsVolumeIsIdentifiedAsApfsAndRefusedByName()
    {
        byte[] volume = SyntheticVolumes.ApfsDisk(2048);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Apfs, info!.Kind);
        Assert.Equal("APFS", info.Name);
        Assert.False(info.WindowsCanMount);

        Result mounted = info.EnsureWindowsCanMount();

        Assert.False(mounted.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, mounted.Error.Code);
        Assert.Contains("APFS", mounted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("dmg extract", mounted.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoRefusalsAreNotTheSameMessage()
    {
        // The point of the story: a user must be able to tell from the message
        // which filesystem they have, because that is what decides what they do
        // next.
        string hfs = FilesystemInfo.Refusal(FilesystemKind.HfsPlus).Message;
        string apfs = FilesystemInfo.Refusal(FilesystemKind.Apfs).Message;

        Assert.NotEqual(hfs, apfs);
        Assert.DoesNotContain("APFS", hfs, StringComparison.Ordinal);
        Assert.DoesNotContain("HFS", apfs, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalReadsAsEnglishAndCarriesTheLabelWhenThereIsOne()
    {
        Assert.Contains(
            "contains an HFS+ volume",
            FilesystemInfo.Refusal(FilesystemKind.HfsPlus).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "contains an APFS volume",
            FilesystemInfo.Refusal(FilesystemKind.Apfs).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"Macintosh HD\"",
            FilesystemInfo.Refusal(FilesystemKind.HfsPlus, "Macintosh HD").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedVolumeIsRefusedWithoutClaimingToKnowWhatItIs()
    {
        DmgError refusal = FilesystemInfo.Refusal(FilesystemKind.Unknown);

        Assert.Equal(DmgExitCode.FilesystemNotMountable, refusal.Code);
        Assert.Contains("does not recognise", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("HFS", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("APFS", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMountableVolumeHasNoRefusal()
    {
        byte[] volume = SyntheticVolumes.ExfatDisk(2048, "MOUNTME");

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Null(info!.MountRefusal);
        Assert.True(info.EnsureWindowsCanMount().Ok);
    }

    [Fact]
    public void AnHfsxVolumeIsStillHfsPlus()
    {
        byte[] volume = SyntheticVolumes.HfsPlusDisk(2048);
        BinaryPrimitives.WriteUInt16BigEndian(volume.AsSpan(1024), 0x4858);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.HfsPlus, info!.Kind);
    }

    [Fact]
    public void AnOriginalHfsVolumeIsNamedHfsRatherThanHfsPlus()
    {
        byte[] volume = SyntheticVolumes.HfsPlusDisk(2048);
        BinaryPrimitives.WriteUInt16BigEndian(volume.AsSpan(1024), 0x4244);

        using MemoryStream disk = new(volume, writable: false);
        Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, 0, volume.Length);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(FilesystemKind.Hfs, info!.Kind);
        Assert.Contains("HFS volume", info.MountRefusal!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hfsplus.dmg", FilesystemKind.HfsPlus, "Apple_HFS")]
    [InlineData("apfs.dmg", FilesystemKind.Apfs, "Apple_APFS")]
    public void ARealAppleFixtureIsIdentifiedAndRefusedByName(
        string name,
        FilesystemKind expected,
        string partitionType)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream image = DmgBlockStreamFixtureTests.Open(file);

        Result<PartitionTable> table = PartitionTableReader.Read(image);
        Assert.True(table.Ok, table.Ok ? "" : table.Error.ToString());

        PartitionEntry partition = Assert.Single(
            table.Value!.Partitions,
            entry => string.Equals(entry.TypeName, partitionType, StringComparison.Ordinal));

        Result<FilesystemInfo> probed = FilesystemProbe.Probe(image, partition);

        Assert.True(probed.TryGetValue(out FilesystemInfo? info), probed.Ok ? "" : probed.Error.ToString());
        Assert.Equal(expected, info!.Kind);
        Assert.False(info.WindowsCanMount);

        Result mounted = info.EnsureWindowsCanMount();

        Assert.False(mounted.Ok);
        Assert.Equal(DmgExitCode.FilesystemNotMountable, mounted.Error.Code);
        Assert.Contains(
            FilesystemSignature.Describe(expected),
            mounted.Error.Message,
            StringComparison.Ordinal);
        Assert.Contains("dmg extract", mounted.Error.Message, StringComparison.Ordinal);

        Console.WriteLine($"{name}: {mounted.Error.Message}");
    }
}
