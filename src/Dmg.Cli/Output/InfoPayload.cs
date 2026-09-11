using Dmg.Cli.Info;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Output;

/// <summary>One codec in the JSON codec inventory.</summary>
/// <param name="Name">The codec's name.</param>
/// <param name="EntryType">The raw UDIF entry type, as <c>0x80000005</c>.</param>
/// <param name="Chunks">How many chunks use it.</param>
/// <param name="Supported">True when this build can decode it.</param>
public sealed record CodecPayload(string Name, string EntryType, int Chunks, bool Supported);

/// <summary>One partition in the JSON report.</summary>
/// <param name="Number">The partition number.</param>
/// <param name="Type">The partition type from the table.</param>
/// <param name="Name">The partition's name in the table, when it has one.</param>
/// <param name="Filesystem">What the volume turned out to be.</param>
/// <param name="Label">The volume label, when it has one.</param>
/// <param name="Bytes">The partition's size in bytes.</param>
/// <param name="FreeSpace">True for a gap rather than a volume.</param>
/// <param name="Mountable">True when Windows has a driver for it.</param>
/// <param name="Refusal">Why not, when it is not mountable.</param>
public sealed record PartitionPayload(
    int Number,
    string Type,
    string? Name,
    string Filesystem,
    string? Label,
    long Bytes,
    bool FreeSpace,
    bool Mountable,
    string? Refusal);

/// <summary>
/// What <c>dmg info --json</c> puts on stdout.
/// </summary>
/// <remarks>
/// <para>
/// Flat and boring on purpose: a script wants <c>.encrypted</c>, <c>.canDecode</c>
/// and <c>.partitions[] | select(.mountable)</c>, and every one of those should be
/// a field rather than something to be inferred from prose.
/// </para>
/// <para>
/// <c>partitioningNote</c> is the field that carries the honesty: when the
/// partitions could not be read it says why, in the same words the screen uses, and
/// <c>partitions</c> is empty rather than absent. A caller that finds an empty list
/// and no note is looking at an image with no partitions; one that finds a note is
/// looking at a question this build could not answer.
/// </para>
/// </remarks>
/// <param name="Path">The path that was read.</param>
/// <param name="FileBytes">The file's size on disk.</param>
/// <param name="Format">The image family: <c>Udif</c>, <c>Raw</c>, <c>Encrypted</c>.</param>
/// <param name="Container">The container in words.</param>
/// <param name="Encrypted">True when the file is an encrcdsa wrapper.</param>
/// <param name="Encryption">The encryption in words, or <c>none</c>.</param>
/// <param name="Unlocked">True when a passphrase opened it.</param>
/// <param name="DecodedBytes">The size of the decoded disk.</param>
/// <param name="Sectors">The decoded disk in 512-byte sectors.</param>
/// <param name="Chunks">How many chunks the block map declares.</param>
/// <param name="Codecs">The codec inventory.</param>
/// <param name="CanDecode">True when every codec in the image has a decoder here.</param>
/// <param name="Partitioning">The partition scheme, or null when it was not read.</param>
/// <param name="PartitioningNote">Why it was not read, when it was not.</param>
/// <param name="Partitions">The partitions, when they were read.</param>
/// <param name="CanMount">True when dmg could mount at least one volume from this image.</param>
public sealed record InfoPayload(
    string Path,
    long FileBytes,
    string Format,
    string Container,
    bool Encrypted,
    string Encryption,
    bool Unlocked,
    ulong DecodedBytes,
    ulong Sectors,
    int Chunks,
    IReadOnlyList<CodecPayload> Codecs,
    bool CanDecode,
    string? Partitioning,
    string? PartitioningNote,
    IReadOnlyList<PartitionPayload> Partitions,
    bool CanMount)
{
    /// <summary>Builds the payload from a report.</summary>
    /// <param name="report">What the inspector found.</param>
    public static InfoPayload From(ImageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new InfoPayload(
            report.Path,
            report.FileBytes,
            report.Format.ToString(),
            report.Container,
            report.Encryption.IsEncrypted,
            report.Encryption.Description,
            report.Encryption.WasUnlocked,
            report.DecodedBytes,
            report.SectorCount,
            report.ChunkCount,
            [
                .. report.Codecs.Select(codec => new CodecPayload(
                    codec.Name,
                    $"0x{codec.EntryType:X8}",
                    codec.Chunks,
                    codec.IsSupported)),
            ],
            report.CanDecode,
            report.Partitioning is PartitionScheme scheme ? PartitionSchemeName.Token(scheme) : null,
            report.PartitioningNote?.Message,
            [
                .. report.Volumes.Select(volume => new PartitionPayload(
                    volume.Number,
                    volume.TypeName,
                    volume.PartitionName,
                    volume.Filesystem,
                    volume.VolumeLabel,
                    volume.Bytes,
                    volume.IsFreeSpace,
                    volume.CanMount,
                    volume.Refusal)),
            ],
            report.CanMount);
    }
}
