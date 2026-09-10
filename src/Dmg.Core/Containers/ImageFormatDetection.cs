namespace Dmg.Core.Containers;

/// <summary>
/// What the probe chain concluded about a file it is prepared to open.
/// </summary>
/// <remarks>
/// <para>
/// Only produced for a format this build can actually read - <see cref="ImageFormat.Udif"/>
/// and <see cref="ImageFormat.Raw"/> today. Everything else comes back as a failed
/// <see cref="Result{T}"/> carrying an error that names the format, because there is
/// no honest way to describe a file's contents when the next step is a refusal.
/// </para>
/// <para>
/// <see cref="Trailer"/> is the parsed <c>koly</c> for a UDIF image and null for a
/// raw one. It is carried here so that identifying an image and then opening it does
/// not parse the trailer twice - the probe already did the work, and the second parse
/// would be a second chance to disagree with the first.
/// </para>
/// </remarks>
/// <param name="Format">The format that was recognised.</param>
/// <param name="ProbeName">Which probe claimed the file, for diagnostics and tests.</param>
/// <param name="FileLength">The real length of the file on disk, in bytes.</param>
/// <param name="SectorCount">
/// How many 512-byte sectors the decoded disk has: the koly's <c>SectorCount</c> for
/// UDIF, the file's own sector count for raw.
/// </param>
/// <param name="Summary">One line naming the format and its size, for <c>dmg info</c>.</param>
/// <param name="Trailer">The parsed koly trailer, for UDIF images only.</param>
public sealed record ImageFormatDetection(
    ImageFormat Format,
    string ProbeName,
    long FileLength,
    ulong SectorCount,
    string Summary,
    KolyTrailer? Trailer = null)
{
    /// <summary>
    /// The decoded size in bytes, checked. A koly can declare a sector count whose
    /// multiplication overflows, so this is a <see cref="Result{T}"/> rather than a
    /// property - the same rule the trailer itself follows.
    /// </summary>
    public Result<ulong> DecodedLengthInBytes() =>
        BigEndian.SectorsToBytes(SectorCount, "sector count");

    /// <inheritdoc />
    public override string ToString() => Summary;
}
