using Dmg.Core;
using Dmg.Core.Codecs;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Info;

/// <summary>
/// Reads an image and answers what it is, without ever decoding it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two halves, and why the second one is optional.</b> The container half -
/// format, encryption, decoded size, the codec inventory - comes from the trailer,
/// the property list and the block map. It costs three small reads, touches no byte
/// of the data fork, and is available for every image this build recognises,
/// including ones whose codecs it has no decoder for. That is the half the design
/// promises is instant and safe on anything.
/// </para>
/// <para>
/// The partition half cannot be had on those terms, and it is worth being plain
/// about why rather than quietly pretending. "Which filesystem is in partition 1"
/// is a question about the <em>contents</em> of the decoded disk: the answer lives
/// in sector 0 and in each volume's superblock, and on a compressed image those
/// sectors are inside chunks. There is no way to read them without decoding those
/// chunks. So this does decode - a handful of chunks, not the image - and only when
/// the codec inventory says every codec in the image has a decoder. When it does
/// not, or when the partition table will not parse, the half is skipped with a
/// reason attached and the container half still stands. That is what makes
/// <c>dmg info</c> useful on a bzip2 image: it says what the image is and why this
/// build cannot go further, instead of failing.
/// </para>
/// <para>
/// <b>Nothing here exits or prints.</b> It returns a <see cref="ImageReport"/> or a
/// <see cref="DmgError"/>, so the same call is what a unit test makes.
/// </para>
/// </remarks>
public sealed class ImageInspector
{
    private readonly ImageFormatProbeChain _chain;
    private readonly ChunkDecoderRegistry _codecs;

    /// <summary>Builds an inspector.</summary>
    /// <param name="chain">The format probes. Defaults to the shipping chain.</param>
    /// <param name="codecs">The codecs. Defaults to the shipping registry.</param>
    public ImageInspector(ImageFormatProbeChain? chain = null, ChunkDecoderRegistry? codecs = null)
    {
        _chain = chain ?? ImageFormatProbeChain.Default;
        _codecs = codecs ?? ChunkDecoderRegistry.Default;
    }

    /// <summary>
    /// Describes the image at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The file to describe.</param>
    /// <param name="passphrase">
    /// A passphrase to unlock the image with, when it is encrypted and one has been
    /// supplied. Null asks for the encrypted wrapper to be described and left shut.
    /// </param>
    public Result<ImageReport> Inspect(string path, Passphrase? passphrase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Result<FileStream> opened = Open(path);

        if (!opened.TryGetValue(out FileStream? file))
        {
            return opened.CastFailure<ImageReport>();
        }

        using (file)
        {
            return Inspect(path, file, passphrase);
        }
    }

    /// <summary>
    /// Describes an image already open as a stream - the overload a test uses, and
    /// the one that does all the work.
    /// </summary>
    /// <param name="path">What to call the image in the report.</param>
    /// <param name="file">The whole file, readable and seekable.</param>
    /// <param name="passphrase">A passphrase, when one has been supplied.</param>
    public Result<ImageReport> Inspect(string path, Stream file, Passphrase? passphrase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(file);

        Result<ImageProbeContext> context = ImageProbeContext.Create(file, path);

        if (!context.TryGetValue(out ImageProbeContext? probe))
        {
            return context.CastFailure<ImageReport>();
        }

        long fileBytes = probe.Length;

        if (!EncryptedImageProbe.IsEncrcdsaV2(probe))
        {
            // Not a v2 wrapper. Either it is not encrypted, or it is the legacy v1
            // layout - and the chain refuses that one by name, which is a better
            // answer than anything this method could invent.
            return Describe(path, fileBytes, file, EncryptionReport.None, _chain.Identify(probe));
        }

        if (passphrase is null)
        {
            // The wrapper is all there is to say, and saying it is a complete
            // answer to "what is this file".
            return Result<ImageReport>.Success(Locked(path, fileBytes, probe));
        }

        Result<EncryptedBlockStream> unlocked = EncryptedBlockStream.Open(
            file,
            passphrase.Bytes,
            leaveOpen: true);

        if (!unlocked.TryGetValue(out EncryptedBlockStream? plaintext))
        {
            // A wrong passphrase is exit 4 and stays exit 4. It is not "an image we
            // described as best we could" - it is a question this run could not
            // answer at all.
            return unlocked.CastFailure<ImageReport>();
        }

        using (plaintext)
        {
            EncryptionReport encryption = Unlocked(plaintext.Header);

            return Describe(path, fileBytes, plaintext, encryption, _chain.Identify(plaintext, path));
        }
    }

    private Result<ImageReport> Describe(
        string path,
        long fileBytes,
        Stream payload,
        EncryptionReport encryption,
        Result<ImageFormatDetection> identified)
    {
        if (!identified.TryGetValue(out ImageFormatDetection? detection))
        {
            return identified.CastFailure<ImageReport>();
        }

        Result<ulong> decoded = detection.DecodedLengthInBytes();

        if (!decoded.TryGetValue(out ulong decodedBytes))
        {
            return decoded.CastFailure<ImageReport>();
        }

        ImageReport report = new()
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            FileBytes = fileBytes,
            Format = detection.Format,
            Container = Container(detection),
            Summary = detection.Summary,
            Encryption = encryption,
            DecodedBytes = decodedBytes,
            SectorCount = detection.SectorCount,
        };

        return detection.Format == ImageFormat.Udif
            ? DescribeUdif(report, payload)
            : DescribeFlat(report, payload);
    }

    /// <summary>
    /// A UDIF container: parse the block map, count the codecs, then read the
    /// partition table through a decoding stream if - and only if - every codec in
    /// the image has a decoder.
    /// </summary>
    private Result<ImageReport> DescribeUdif(ImageReport report, Stream payload)
    {
        Result<DmgImage> opened = DmgImage.Open(payload);

        if (!opened.TryGetValue(out DmgImage? image))
        {
            return opened.CastFailure<ImageReport>();
        }

        report = report with
        {
            Codecs = CountCodecs(image),
            ChunkCount = image.Regions.Sum(region => region.Chunks.Count(Carries)),
        };

        if (!report.CanDecode)
        {
            return Result<ImageReport>.Success(report with
            {
                PartitioningNote = DmgError.Unsupported(
                    "The partitioning was not read: this image uses "
                    + Join(report.UnsupportedCodecs.Select(codec => codec.Name))
                    + ", which this build has no decoder for.",
                    "Reading a partition table means decoding the chunks that hold sector 0."),
            });
        }

        Result<DmgBlockStream> stream = DmgBlockStream.Create(payload, image, leaveOpen: true);

        if (!stream.TryGetValue(out DmgBlockStream? disk))
        {
            return Result<ImageReport>.Success(report with { PartitioningNote = stream.Error });
        }

        using (disk)
        {
            return Result<ImageReport>.Success(WithPartitions(report, disk));
        }
    }

    /// <summary>
    /// A flat sector image: there is no block map and no codec, so the bytes on
    /// disk are the decoded disk and the partition table is simply there.
    /// </summary>
    private static Result<ImageReport> DescribeFlat(ImageReport report, Stream payload) =>
        Result<ImageReport>.Success(WithPartitions(report, payload));

    private static ImageReport WithPartitions(ImageReport report, Stream disk)
    {
        Result<VolumeMap> read;

        try
        {
            read = VolumeMap.Read(disk);
        }
        catch (DmgStreamException exception)
        {
            // A decode failure surfaces through Stream.Read, which cannot return a
            // Result. It is still not a reason to fail the whole report: the
            // container half is already answered.
            return report with
            {
                PartitioningNote = DmgError.Corrupt(
                    "The partitioning could not be read: decoding the sectors that hold the "
                    + "partition table failed.",
                    exception.Message),
            };
        }

        if (!read.TryGetValue(out VolumeMap? map))
        {
            return report with { PartitioningNote = read.Error };
        }

        return report with
        {
            Partitioning = map.Scheme,
            Volumes = [.. map.Volumes.Select(Describe)],
        };
    }

    private static VolumeUsage Describe(DiskVolume volume) => new(
        volume.Number,
        volume.Partition.TypeName,
        string.IsNullOrEmpty(volume.Partition.Name) ? null : volume.Partition.Name,
        volume.Partition.IsFreeSpace ? "free space" : volume.Filesystem.Name,
        string.IsNullOrEmpty(volume.Filesystem.VolumeLabel) ? null : volume.Filesystem.VolumeLabel,
        volume.ByteLength,
        volume.Partition.IsFreeSpace,
        volume.IsMountable,
        Refusal(volume));

    private static string? Refusal(DiskVolume volume)
    {
        if (volume.IsMountable || volume.Partition.IsFreeSpace)
        {
            return null;
        }

        return volume.ProbeFailure?.Message ?? volume.Filesystem.MountRefusal?.Message;
    }

    /// <summary>
    /// The codec inventory: which codecs, how many chunks each, commonest first -
    /// counted from the chunk table, with no byte of the data fork read.
    /// </summary>
    private IReadOnlyList<CodecUsage> CountCodecs(DmgImage image)
    {
        Dictionary<uint, int> counts = [];

        foreach (MishBlock region in image.Regions)
        {
            foreach (ChunkDescriptor chunk in region.Chunks)
            {
                if (!Carries(chunk))
                {
                    continue;
                }

                uint entryType = (uint)chunk.EntryType;
                counts[entryType] = counts.TryGetValue(entryType, out int seen) ? seen + 1 : 1;
            }
        }

        return
        [
            .. _codecs.Survey(counts.Keys)
                .Select(codec => new CodecUsage(
                    codec.Name,
                    codec.EntryType,
                    counts[codec.EntryType],
                    codec.IsSupported))
                .OrderByDescending(codec => codec.Chunks)
                .ThenBy(codec => codec.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// True for a chunk that stands for sectors. The terminator and the comment
    /// entries are table punctuation and counting them would inflate every number
    /// on the screen by the number of regions.
    /// </summary>
    private static bool Carries(ChunkDescriptor chunk) => !chunk.IsTerminator && !chunk.IsComment;

    private static ImageReport Locked(string path, long fileBytes, ImageProbeContext probe)
    {
        Result<EncryptedDmgHeader> header = EncryptedDmgHeader.Parse(probe.Header, probe.Length);
        EncryptedDmgHeader? parsed = header.GetValueOrDefault();

        return new ImageReport
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            FileBytes = fileBytes,
            Format = ImageFormat.Encrypted,
            Container = "encrcdsa v2 (locked)",
            Summary = "An encrypted disk image. Supply a passphrase to describe what is inside it.",
            Encryption = new EncryptionReport(
                true,
                parsed is null ? "encrcdsa v2" : Algorithm(parsed),
                WasUnlocked: false),
            DecodedBytes = parsed is null ? 0 : (ulong)Math.Max(0, parsed.DataSize),
            SectorCount = parsed is null ? 0 : (ulong)Math.Max(0, parsed.DataSize) / 512,
            PartitioningNote = DmgError.Usage(
                "The partitioning was not read: the image is encrypted and no passphrase was given.",
                "Add --password-stdin or --password-env VAR, or let dmg prompt for it."),
        };
    }

    private static EncryptionReport Unlocked(EncryptedDmgHeader header) =>
        new(true, Algorithm(header), WasUnlocked: true);

    private static string Algorithm(EncryptedDmgHeader header) =>
        $"AES-{header.EncryptionKeyBits} (encrcdsa v{header.Version})";

    private static string Container(ImageFormatDetection detection) => detection.Format switch
    {
        ImageFormat.Udif => detection.Trailer is { } trailer
            ? $"UDIF v{trailer.Version}"
            : "UDIF",
        ImageFormat.Raw => "raw sector image (no UDIF container)",
        ImageFormat.Encrypted => "encrcdsa",
        _ => detection.ProbeName,
    };

    private static string Join(IEnumerable<string> names)
    {
        string[] all = [.. names];

        return all.Length switch
        {
            0 => "an unknown codec",
            1 => all[0],
            _ => string.Join(", ", all[..^1]) + " and " + all[^1],
        };
    }

    private static Result<FileStream> Open(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Result<FileStream>.Failure(DmgError.Usage(
                    $"'{path}' is a folder, not a disk image file.",
                    "A .sparsebundle is a folder; this build cannot read one."));
            }

            if (!File.Exists(path))
            {
                return Result<FileStream>.Failure(DmgError.Usage($"There is no file at '{path}'."));
            }

            return Result<FileStream>.Success(new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return Result<FileStream>.Failure(DmgError.Usage(
                $"Could not open '{path}'.",
                exception.Message));
        }
    }
}
