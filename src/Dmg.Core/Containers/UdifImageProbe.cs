namespace Dmg.Core.Containers;

/// <summary>
/// The UDIF probe: a <c>koly</c> trailer in the last 512 bytes, wired straight to
/// <see cref="KolyTrailer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The claim is made on the four-byte signature alone, and the parse happens
/// afterwards. That division is the whole point of splitting recognition from
/// description. A file that says <c>koly</c> and then contradicts itself is a
/// damaged UDIF image, and the user needs to be told that; if the probe only
/// claimed files whose trailers parsed, a damaged image would fall through to the
/// raw probe and be silently reinterpreted as a sector stream, which is the worst
/// outcome available - it would mount, and the contents would be wrong.
/// </para>
/// <para>
/// Nothing else in the container is touched. Whether the property list is readable,
/// whether the blkx tables are sane, whether the chunks decode: all later, all
/// somebody else's story. This probe answers one question and the answer is worth
/// having before any of that work is started.
/// </para>
/// </remarks>
public sealed class UdifImageProbe : IImageFormatProbe
{
    /// <summary>The shared instance. The probe is stateless.</summary>
    public static UdifImageProbe Instance { get; } = new();

    private UdifImageProbe()
    {
    }

    /// <inheritdoc />
    public ImageFormat Format => ImageFormat.Udif;

    /// <inheritdoc />
    public string Name => "UDIF";

    /// <inheritdoc />
    public bool Recognises(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // A koly trailer is exactly the last 512 bytes, so a file shorter than that
        // cannot have one however promising its bytes look.
        return image.Length >= KolyTrailer.Size
            && image.Trailer.Length >= KolyTrailer.Size
            && BigEndian.TryReadUInt32(image.Trailer, 0, out uint signature)
            && signature == KolyTrailer.Magic;
    }

    /// <inheritdoc />
    public Result<ImageFormatDetection> Describe(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Result<KolyTrailer> parsed = KolyTrailer.Parse(image.Trailer, image.Length);

        if (!parsed.TryGetValue(out KolyTrailer? koly))
        {
            return parsed.CastFailure<ImageFormatDetection>();
        }

        string segments = koly.IsMultiPart
            ? $", segment {koly.SegmentNumber} of {koly.SegmentCount}"
            : string.Empty;

        string summary =
            $"UDIF image (koly v{koly.Version}){segments}: {koly.SectorCount} sectors, "
            + $"{image.Length} bytes on disk.";

        return Result<ImageFormatDetection>.Success(new ImageFormatDetection(
            ImageFormat.Udif,
            Name,
            image.Length,
            koly.SectorCount,
            summary,
            koly));
    }
}
