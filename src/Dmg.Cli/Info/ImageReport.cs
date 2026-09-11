using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Info;

/// <summary>One codec found in an image's chunk table, and how much of it there is.</summary>
/// <param name="Name">The codec's name: <c>zlib</c>, <c>bzip2</c>, <c>zero-fill</c>.</param>
/// <param name="EntryType">The raw UDIF entry type, for a reader who wants it.</param>
/// <param name="Chunks">How many chunks use it.</param>
/// <param name="IsSupported">True when this build has a decoder for it.</param>
public sealed record CodecUsage(string Name, uint EntryType, int Chunks, bool IsSupported);

/// <summary>What is known about an image's encryption.</summary>
/// <param name="IsEncrypted">True when the file is an encrcdsa wrapper.</param>
/// <param name="Description">
/// <c>none</c>, or <c>AES-256 (encrcdsa v2)</c>, or the reason a legacy wrapper
/// cannot be opened.
/// </param>
/// <param name="WasUnlocked">
/// True when a passphrase opened it and everything below this line describes the
/// plaintext.
/// </param>
public sealed record EncryptionReport(bool IsEncrypted, string Description, bool WasUnlocked)
{
    /// <summary>The report for a file that is not encrypted at all.</summary>
    public static EncryptionReport None { get; } = new(false, "none", false);
}

/// <summary>One partition, and whether Windows could mount what is in it.</summary>
/// <param name="Number">The partition number as the partition table gives it.</param>
/// <param name="TypeName">The partition type, from the table.</param>
/// <param name="PartitionName">The name in the table, when it has one.</param>
/// <param name="Filesystem">What the volume turned out to be: <c>exFAT</c>, <c>HFS+</c>.</param>
/// <param name="VolumeLabel">The label inside the volume, when it has one.</param>
/// <param name="Bytes">The partition's size.</param>
/// <param name="IsFreeSpace">True for a gap rather than a volume.</param>
/// <param name="CanMount">True when Windows has a driver for it.</param>
/// <param name="Refusal">
/// Why not, when it cannot be mounted - the sentence a user needs, not a code.
/// </param>
public sealed record VolumeUsage(
    int Number,
    string TypeName,
    string? PartitionName,
    string Filesystem,
    string? VolumeLabel,
    long Bytes,
    bool IsFreeSpace,
    bool CanMount,
    string? Refusal);

/// <summary>
/// Everything <c>dmg info</c> found, as data rather than as text.
/// </summary>
/// <remarks>
/// <para>
/// The screen and the JSON are two renderings of this one object, so they cannot
/// disagree about what the image is - which they would within a month if each read
/// the image for itself.
/// </para>
/// <para>
/// <b>Two halves, and the line between them matters.</b> Everything down to
/// <see cref="Codecs"/> comes from the trailer, the property list and the block map:
/// no chunk is decoded, nothing is read from the data fork, and it is available for
/// every image this tool can recognise at all - including ones whose codecs it
/// cannot decode. <see cref="Partitioning"/> and <see cref="Volumes"/> are the other
/// half, and they are optional: answering "what filesystem is in partition 1"
/// requires reading the sectors that hold the partition table and each volume's
/// superblock, which for a compressed image means decoding the handful of chunks
/// those sectors live in. When that is not possible - an unsupported codec, a
/// partition table that will not parse - <see cref="PartitioningNote"/> says why and
/// the rest of the report still stands.
/// </para>
/// </remarks>
public sealed record ImageReport
{
    /// <summary>The path the user gave.</summary>
    public required string Path { get; init; }

    /// <summary>Just the file name, for the first line of the screen.</summary>
    public required string FileName { get; init; }

    /// <summary>The size of the file on disk.</summary>
    public required long FileBytes { get; init; }

    /// <summary>Which family of image this is.</summary>
    public required ImageFormat Format { get; init; }

    /// <summary>The container in words: <c>UDIF v4</c>, <c>raw sector image</c>.</summary>
    public required string Container { get; init; }

    /// <summary>What the probe chain said, in one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Whether it is encrypted, and how.</summary>
    public required EncryptionReport Encryption { get; init; }

    /// <summary>The size of the decoded disk.</summary>
    public required ulong DecodedBytes { get; init; }

    /// <summary>The decoded disk in 512-byte sectors.</summary>
    public required ulong SectorCount { get; init; }

    /// <summary>How many chunks the block map declares. Zero for a container that has none.</summary>
    public int ChunkCount { get; init; }

    /// <summary>The codecs in the chunk table, commonest first.</summary>
    public IReadOnlyList<CodecUsage> Codecs { get; init; } = [];

    /// <summary>
    /// The codecs this image uses that this build has no decoder for. Empty is the
    /// answer to "can this tool read it".
    /// </summary>
    public IReadOnlyList<CodecUsage> UnsupportedCodecs =>
        [.. Codecs.Where(codec => !codec.IsSupported)];

    /// <summary>True when every codec in the image has a decoder here.</summary>
    public bool CanDecode => UnsupportedCodecs.Count == 0;

    /// <summary>How the disk is divided, when that could be read.</summary>
    public PartitionScheme? Partitioning { get; init; }

    /// <summary>
    /// Why the partitioning is not reported, when it is not. Null when it is.
    /// </summary>
    public DmgError? PartitioningNote { get; init; }

    /// <summary>The partitions, when they could be read.</summary>
    public IReadOnlyList<VolumeUsage> Volumes { get; init; } = [];

    /// <summary>The partitions Windows could mount.</summary>
    public IReadOnlyList<VolumeUsage> Mountable =>
        [.. Volumes.Where(volume => volume.CanMount)];

    /// <summary>
    /// True when this image could be mounted on Windows by <c>dmg mount</c> - every
    /// codec decodable and at least one volume Windows has a driver for.
    /// </summary>
    public bool CanMount => CanDecode && Mountable.Count > 0;
}
