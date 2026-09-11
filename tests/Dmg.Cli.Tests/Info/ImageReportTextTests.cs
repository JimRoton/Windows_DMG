using Dmg.Cli.Info;
using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Tests.Info;

/// <summary>
/// The screen, rendered from a report built by hand so the exact wording can be
/// pinned without an image on disk.
/// </summary>
public sealed class ImageReportTextTests
{
    private static ImageReport Report(
        PartitionScheme? scheme = PartitionScheme.GuidPartitionTable,
        DmgError? note = null,
        params VolumeUsage[] volumes) => new()
        {
            Path = "/images/installer.dmg",
            FileName = "installer.dmg",
            FileBytes = 956_000_000,
            Format = ImageFormat.Udif,
            Container = "UDIF v4",
            Summary = "A UDIF container.",
            Encryption = EncryptionReport.None,
            DecodedBytes = 2_576_980_377,
            SectorCount = 5_033_164,
            ChunkCount = 2_462,
            Codecs =
            [
                new CodecUsage("zlib", 0x8000_0005, 2_301, true),
                new CodecUsage("zero-fill", 0, 117, true),
                new CodecUsage("raw", 1, 44, true),
            ],
            Partitioning = note is null ? scheme : null,
            PartitioningNote = note,
            Volumes = note is null ? volumes : [],
        };

    private static VolumeUsage Exfat() =>
        new(1, "Windows_NTFS", null, "exFAT", "Installer", 2_576_000_000, false, true, null);

    private static VolumeUsage HfsPlus() =>
        new(
            1,
            "Apple_HFS",
            "disk image",
            "HFS+",
            "Install macOS",
            12_900_000_000,
            false,
            false,
            "Windows has no HFS+ driver, so it cannot mount this volume. Use 'dmg extract' to "
            + "copy files out of it instead, or read it on a Mac.");

    [Fact]
    public void TheContainerHalfIsAlwaysThere()
    {
        IReadOnlyList<string> lines = ImageReportText.Render(Report(volumes: Exfat()));

        Assert.Contains(lines, line => line.Contains("installer.dmg", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("UDIF v4", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("encryption", StringComparison.Ordinal)
            && line.Contains("none", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("2.40 GiB", StringComparison.Ordinal)
            && line.Contains("5,033,164 sectors", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCodecBreakdownIsOnOneLineCommonestFirst()
    {
        string chunks = ImageReportText
            .Render(Report(volumes: Exfat()))
            .Single(line => line.Contains("chunks", StringComparison.Ordinal));

        Assert.Contains("2,462", chunks, StringComparison.Ordinal);
        Assert.True(
            chunks.IndexOf("zlib", StringComparison.Ordinal)
                < chunks.IndexOf("zero-fill", StringComparison.Ordinal),
            chunks);
    }

    [Fact]
    public void AnUnsupportedCodecIsShouted()
    {
        ImageReport report = Report(volumes: Exfat()) with
        {
            Codecs = [new CodecUsage("bzip2", 0x8000_0006, 2, false)],
        };

        Assert.Contains(
            ImageReportText.Render(report),
            line => line.Contains("bzip2 2 (NOT SUPPORTED)", StringComparison.Ordinal));
    }

    [Fact]
    public void AMountableVolumeSaysSo()
    {
        Assert.Contains(
            ImageReportText.Render(Report(volumes: Exfat())),
            line => line.Contains("exFAT \"Installer\"", StringComparison.Ordinal)
                && line.Contains("->  mountable", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnmountableVolumeGetsTheReasonUnderneath()
    {
        IReadOnlyList<string> lines = ImageReportText.Render(Report(volumes: HfsPlus()));

        Assert.Contains(lines, line => line.Contains("NOT MOUNTABLE", StringComparison.Ordinal));

        // "NOT MOUNTABLE" on its own is not something a user can act on.
        Assert.Contains(lines, line => line.Contains("HFS+ driver", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("dmg extract", StringComparison.Ordinal));
    }

    [Fact]
    public void NoLineRunsPastTheTerminal()
    {
        IReadOnlyList<string> lines = ImageReportText.Render(Report(volumes: HfsPlus()));

        Assert.All(lines, line => Assert.True(line.Length <= 100, $"{line.Length}: {line}"));
    }

    [Fact]
    public void EveryPartitionGetsALineAndOnlyTheFirstGetsTheLabel()
    {
        IReadOnlyList<string> lines = ImageReportText.Render(
            Report(volumes: [Exfat(), Exfat() with { Number = 2 }]));

        Assert.Single(lines, line => line.Contains("partitions", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(line => line.Contains("exFAT", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnUnreadablePartitioningSaysNotReadAndWhy()
    {
        IReadOnlyList<string> lines = ImageReportText.Render(Report(
            note: DmgError.Unsupported("This image uses bzip2, which this build has no decoder for.")));

        Assert.Contains(lines, line => line.Contains("partitioning", StringComparison.Ordinal)
            && line.Contains("not read", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("bzip2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PartitionScheme.GuidPartitionTable, "GPT")]
    [InlineData(PartitionScheme.MasterBootRecord, "MBR")]
    [InlineData(PartitionScheme.ApplePartitionMap, "Apple partition map")]
    [InlineData(PartitionScheme.WholeDisk, "none (whole disk)")]
    public void TheSchemeIsNamedTheWayAPersonWouldSayIt(PartitionScheme scheme, string expected)
    {
        Assert.Equal(expected, PartitionSchemeName.Display(scheme));
        Assert.Contains(
            ImageReportText.Render(Report(scheme, volumes: Exfat())),
            line => line.Contains("partitioning", StringComparison.Ordinal)
                && line.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PartitionScheme.GuidPartitionTable, "gpt")]
    [InlineData(PartitionScheme.MasterBootRecord, "mbr")]
    [InlineData(PartitionScheme.ApplePartitionMap, "apm")]
    [InlineData(PartitionScheme.WholeDisk, "whole-disk")]
    public void TheJsonTokenIsNotTheEnumName(PartitionScheme scheme, string expected)
    {
        // Renaming an enum member must not silently change the JSON contract.
        Assert.Equal(expected, PartitionSchemeName.Token(scheme));
        Assert.NotEqual(scheme.ToString(), PartitionSchemeName.Token(scheme));
    }

    [Fact]
    public void AFlatImageOmitsTheChunkRowEntirely()
    {
        ImageReport report = Report(volumes: Exfat()) with
        {
            Format = ImageFormat.Raw,
            Container = "raw sector image (no UDIF container)",
            ChunkCount = 0,
            Codecs = [],
        };

        // "chunks 0" would be a claim about a container that has no chunk table.
        Assert.DoesNotContain(
            ImageReportText.Render(report),
            line => line.Contains("chunks", StringComparison.Ordinal));
    }

    [Fact]
    public void WritingGoesToStdout()
    {
        RecordingOutput output = new();

        ImageReportText.WriteTo(output.Output, Report(volumes: Exfat()));

        Assert.Empty(output.Stderr);
        Assert.NotEmpty(output.StdoutLines);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ImageReportText.Render(null!));
        Assert.Throws<ArgumentNullException>(() => ImageReportText.WriteTo(null!, Report()));
        Assert.Throws<ArgumentNullException>(
            () => ImageReportText.WriteTo(new RecordingOutput().Output, null!));
    }
}
