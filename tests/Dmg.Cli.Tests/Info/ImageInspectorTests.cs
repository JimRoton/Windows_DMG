using Dmg.Cli.Info;
using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Tests.Info;

/// <summary>
/// The inspector against real images, because the whole point of <c>info</c> is
/// that it tells the truth about a file hdiutil actually wrote. Every test degrades
/// to a pass when the fixtures have not been generated.
/// </summary>
public sealed class ImageInspectorTests
{
    private static readonly ImageInspector Inspector = new();

    [Theory]
    [InlineData("exfat-zlib.dmg", "zlib")]
    [InlineData("adc.dmg", "ADC")]
    [InlineData("exfat-udro.dmg", "raw")]
    [InlineData("zerofill.dmg", "zero-fill")]
    public void AUdifImageIsDescribedDownToItsCodecs(string fixture, string codec)
    {
        if (Report(fixture) is not ImageReport report)
        {
            return;
        }

        Assert.Equal(ImageFormat.Udif, report.Format);
        Assert.Equal("UDIF v4", report.Container);
        Assert.False(report.Encryption.IsEncrypted);
        Assert.True(report.DecodedBytes > 0);
        Assert.Equal(report.DecodedBytes, report.SectorCount * 512);
        Assert.Contains(report.Codecs, entry => entry.Name == codec);
        Assert.Equal(report.ChunkCount, report.Codecs.Sum(entry => entry.Chunks));
    }

    [Theory]
    [InlineData("exfat-zlib.dmg", "exFAT", true)]
    [InlineData("fat32.dmg", "FAT32", true)]
    [InlineData("hfsplus.dmg", "HFS+", false)]
    [InlineData("apfs.dmg", "APFS", false)]
    public void TheFilesystemAndItsMountabilityAreReported(string fixture, string filesystem, bool mountable)
    {
        if (Report(fixture) is not ImageReport report)
        {
            return;
        }

        VolumeUsage volume = Assert.Single(report.Volumes, entry => !entry.IsFreeSpace);

        Assert.Equal(filesystem, volume.Filesystem);
        Assert.Equal(mountable, volume.CanMount);
        Assert.Equal(mountable, report.CanMount);
    }

    [Theory]
    [InlineData("hfsplus.dmg")]
    [InlineData("apfs.dmg")]
    public void AnUnmountableVolumeIsDescribedRatherThanRefused(string fixture)
    {
        if (Report(fixture) is not ImageReport report)
        {
            return;
        }

        // This is the point of the verb: an image Windows cannot mount is still an
        // image the user asked about, so it gets a full description and a sentence
        // saying why it cannot be mounted.
        Assert.True(report.CanDecode);
        Assert.NotNull(report.Partitioning);
        Assert.Null(report.PartitioningNote);

        VolumeUsage volume = report.Volumes.Single(entry => !entry.IsFreeSpace);

        Assert.False(volume.CanMount);
        Assert.NotNull(volume.Refusal);
        Assert.Contains("Windows", volume.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndecodableImageIsStillDescribedDownToItsCodecs()
    {
        if (Report("bzip2.dmg") is not ImageReport report)
        {
            return;
        }

        // bzip2 has no decoder here, so the container half is answered in full and
        // the partition half is skipped with the reason attached - never an error.
        Assert.Equal("UDIF v4", report.Container);
        Assert.True(report.DecodedBytes > 0);
        Assert.Contains(report.Codecs, codec => codec.Name == "bzip2" && !codec.IsSupported);
        Assert.False(report.CanDecode);
        Assert.False(report.CanMount);
        Assert.Null(report.Partitioning);
        Assert.NotNull(report.PartitioningNote);
        Assert.Contains("bzip2", report.PartitioningNote.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatImageHasNoChunkTableAndStillHasPartitions()
    {
        if (Report("exfat-raw.dmg") is not ImageReport report)
        {
            return;
        }

        Assert.Equal(ImageFormat.Raw, report.Format);
        Assert.Equal(0, report.ChunkCount);
        Assert.Empty(report.Codecs);

        // No chunk table means nothing to decode, which means the partition table
        // is simply there to be read.
        Assert.True(report.CanDecode);
        Assert.Equal(PartitionScheme.MasterBootRecord, report.Partitioning);
        Assert.True(report.CanMount);
    }

    [Fact]
    public void AMultiPartitionImageReportsEveryPartition()
    {
        if (Report("multipart.dmg") is not ImageReport report)
        {
            return;
        }

        Assert.True(report.Volumes.Count(volume => !volume.IsFreeSpace) >= 2);
        Assert.All(
            report.Volumes.Where(volume => !volume.IsFreeSpace),
            volume => Assert.True(volume.Bytes > 0));
    }

    [Fact]
    public void AnEncryptedImageWithoutAPassphraseIsDescribedAsFarAsItsWrapper()
    {
        if (Report("exfat-enc256.dmg") is not ImageReport report)
        {
            return;
        }

        Assert.True(report.Encryption.IsEncrypted);
        Assert.False(report.Encryption.WasUnlocked);
        Assert.Contains("AES-256", report.Encryption.Description, StringComparison.Ordinal);

        // It answered what it could without a passphrase, and said what it could
        // not - rather than failing on a file the user is entitled to ask about.
        Assert.NotNull(report.PartitioningNote);
        Assert.Empty(report.Volumes);
    }

    [Theory]
    [InlineData("exfat-enc128.dmg", "AES-128")]
    [InlineData("exfat-enc256.dmg", "AES-256")]
    public void APassphraseOpensAnEncryptedImageAllTheWayThrough(string fixture, string algorithm)
    {
        string? path = Fixtures.Path(fixture);

        if (path is null)
        {
            return;
        }

        using Passphrase passphrase = Passphrase.FromString("dmg-test-passphrase\n");
        Result<ImageReport> inspected = Inspector.Inspect(path, passphrase);

        Assert.True(inspected.Ok, inspected.Ok ? string.Empty : inspected.Error.ToString());

        ImageReport report = inspected.Value!;

        Assert.True(report.Encryption.WasUnlocked);
        Assert.Contains(algorithm, report.Encryption.Description, StringComparison.Ordinal);
        Assert.NotNull(report.Partitioning);
        Assert.True(report.CanMount);
    }

    [Fact]
    public void AWrongPassphraseIsExitFourAndNotAReport()
    {
        string? path = Fixtures.Path("exfat-enc256.dmg");

        if (path is null)
        {
            return;
        }

        using Passphrase passphrase = Passphrase.FromString("not the passphrase\n");
        Result<ImageReport> inspected = Inspector.Inspect(path, passphrase);

        // The one encrypted case that is a genuine failure: the question could not
        // be answered at all, as opposed to answered unwelcomely.
        Assert.False(inspected.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, inspected.Error.Code);
    }

    [Fact]
    public void AMissingFileIsAUsageError()
    {
        Result<ImageReport> inspected = Inspector.Inspect(
            System.IO.Path.Combine(AppContext.BaseDirectory, "no-such-image.dmg"));

        Assert.False(inspected.Ok);
        Assert.Equal(DmgExitCode.UsageError, inspected.Error.Code);
    }

    [Fact]
    public void SomethingThatIsNotAnImageIsAnUnsupportedFormat()
    {
        // 4,095 bytes, deliberately: a whole number of sectors with no koly
        // trailer is a legitimate raw sector image, and calling one of those
        // unrecognised would be the bug. A part-sector length cannot be either.
        using MemoryStream stream = new(new byte[4095], writable: false);

        Result<ImageReport> inspected = Inspector.Inspect("junk.bin", stream);

        Assert.False(inspected.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, inspected.Error.Code);
    }

    [Fact]
    public void AFlatFileOfWholeSectorsIsARawImageRatherThanAnError()
    {
        using MemoryStream stream = new(new byte[4096], writable: false);

        Result<ImageReport> inspected = Inspector.Inspect("flat.img", stream);

        Assert.True(inspected.Ok);
        Assert.Equal(ImageFormat.Raw, inspected.Value!.Format);
        Assert.Equal(4096UL, inspected.Value!.DecodedBytes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Inspector.Inspect("  "));
        Assert.Throws<ArgumentNullException>(() => Inspector.Inspect("x.dmg", (Stream)null!));
    }

    private static ImageReport? Report(string fixture)
    {
        string? path = Fixtures.Path(fixture);

        if (path is null)
        {
            return null;
        }

        Result<ImageReport> inspected = Inspector.Inspect(path);

        Assert.True(inspected.Ok, inspected.Ok ? string.Empty : inspected.Error.ToString());

        return inspected.Value!;
    }
}
