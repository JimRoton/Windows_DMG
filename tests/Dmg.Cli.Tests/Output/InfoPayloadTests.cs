using Dmg.Cli.Info;
using Dmg.Cli.Output;
using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Tests.Output;

/// <summary>
/// <see cref="InfoPayload.From"/> against a report built by hand, so the field
/// mapping can be pinned without an image on disk. The screen and the JSON are
/// two renderings of the same <see cref="ImageReport"/>, and this is the half
/// of that contract the renderer tests do not reach.
/// </summary>
public sealed class InfoPayloadTests
{
    private static ImageReport Report(
        PartitionScheme? scheme = PartitionScheme.GuidPartitionTable,
        DmgError? note = null,
        VolumeUsage[]? volumes = null) => new()
        {
            Path = "/images/installer.dmg",
            FileName = "installer.dmg",
            FileBytes = 956_000_000,
            Format = ImageFormat.Udif,
            Container = "UDIF v4",
            Summary = "A UDIF container.",
            Encryption = new EncryptionReport(true, "AES-256 (encrcdsa v2)", WasUnlocked: true),
            DecodedBytes = 2_576_980_377,
            SectorCount = 5_033_164,
            ChunkCount = 2_462,
            Codecs =
            [
                new CodecUsage("zlib", 0x8000_0005, 2_301, true),
                new CodecUsage("bzip2", 0x8000_0006, 2, false),
            ],
            Partitioning = note is null ? scheme : null,
            PartitioningNote = note,
            Volumes = note is null ? volumes ?? [] : [],
        };

    private static VolumeUsage Exfat() => new(
        1,
        "Windows_NTFS",
        "disk image",
        "exFAT",
        "Installer",
        2_576_000_000,
        IsFreeSpace: false,
        CanMount: true,
        Refusal: null);

    [Fact]
    public void TheScalarFieldsComeStraightAcross()
    {
        InfoPayload payload = InfoPayload.From(Report(volumes: [Exfat()]));

        Assert.Equal("/images/installer.dmg", payload.Path);
        Assert.Equal(956_000_000, payload.FileBytes);
        Assert.Equal("Udif", payload.Format);
        Assert.Equal("UDIF v4", payload.Container);
        Assert.True(payload.Encrypted);
        Assert.Equal("AES-256 (encrcdsa v2)", payload.Encryption);
        Assert.True(payload.Unlocked);
        Assert.Equal(2_576_980_377UL, payload.DecodedBytes);
        Assert.Equal(5_033_164UL, payload.Sectors);
        Assert.Equal(2_462, payload.Chunks);
    }

    [Fact]
    public void FormatIsTheEnumNameNotAnEnumValue()
    {
        // The JSON contract is the identifier a caller matches with a string
        // equality, not the underlying number - so it has to be the name.
        Assert.Equal(ImageFormat.Raw.ToString(), InfoPayload.From(Report(volumes: [Exfat()]) with
        {
            Format = ImageFormat.Raw,
        }).Format);
    }

    [Fact]
    public void CodecEntryTypeIsHexNotDecimal()
    {
        CodecPayload zlib = InfoPayload.From(Report(volumes: [Exfat()]))
            .Codecs.Single(codec => codec.Name == "zlib");

        Assert.Equal("0x80000005", zlib.EntryType);
    }

    [Fact]
    public void CodecSupportAndCountsRoundTrip()
    {
        IReadOnlyList<CodecPayload> codecs = InfoPayload.From(Report(volumes: [Exfat()])).Codecs;

        Assert.Contains(codecs, codec => codec is { Name: "zlib", Chunks: 2_301, Supported: true });
        Assert.Contains(codecs, codec => codec is { Name: "bzip2", Chunks: 2, Supported: false });
    }

    [Fact]
    public void CanDecodeIsFalseWhenAnyCodecIsUnsupported() =>
        Assert.False(InfoPayload.From(Report(volumes: [Exfat()])).CanDecode);

    [Fact]
    public void PartitioningIsTheTokenNotTheEnumName()
    {
        string? token = InfoPayload.From(Report(scheme: PartitionScheme.ApplePartitionMap, volumes: [Exfat()]))
            .Partitioning;

        Assert.Equal("apm", token);
        Assert.NotEqual(PartitionScheme.ApplePartitionMap.ToString(), token);
    }

    [Fact]
    public void PartitioningIsNullRatherThanAPlaceholderWhenItWasNotRead()
    {
        InfoPayload payload = InfoPayload.From(Report(
            note: DmgError.Unsupported("This image uses bzip2, which this build has no decoder for.")));

        Assert.Null(payload.Partitioning);
        Assert.NotNull(payload.PartitioningNote);
        Assert.Contains("bzip2", payload.PartitioningNote, StringComparison.Ordinal);
        Assert.Empty(payload.Partitions);
    }

    [Fact]
    public void AnEmptyPartitionListWithNoNoteMeansNoPartitionsRatherThanUnknown()
    {
        InfoPayload payload = InfoPayload.From(Report(volumes: []));

        Assert.Empty(payload.Partitions);
        Assert.Null(payload.PartitioningNote);
    }

    [Fact]
    public void EveryVolumeFieldRoundTrips()
    {
        VolumeUsage refused = new(2, "Apple_HFS", null, "HFS+", null, 900_000, false, false, "no HFS+ driver");

        PartitionPayload partition = InfoPayload
            .From(Report(volumes: [Exfat(), refused]))
            .Partitions
            .Single(entry => entry.Number == 2);

        Assert.Equal("Apple_HFS", partition.Type);
        Assert.Null(partition.Name);
        Assert.Equal("HFS+", partition.Filesystem);
        Assert.Null(partition.Label);
        Assert.Equal(900_000, partition.Bytes);
        Assert.False(partition.FreeSpace);
        Assert.False(partition.Mountable);
        Assert.Equal("no HFS+ driver", partition.Refusal);
    }

    [Fact]
    public void CanMountComesFromTheReportNotRecomputed()
    {
        // exFAT is mountable, but the codec inventory says bzip2 is not
        // decodable, so the image as a whole cannot be mounted even though a
        // volume in it could be.
        InfoPayload payload = InfoPayload.From(Report(volumes: [Exfat()]));

        Assert.False(payload.CanDecode);
        Assert.False(payload.CanMount);
    }

    [Fact]
    public void NullReportIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => InfoPayload.From(null!));
}
