namespace Dmg.Core.Containers;

/// <summary>
/// The end of the chain, whose entire job is to say what the file actually is.
/// </summary>
/// <remarks>
/// <para>
/// <b>"This is not a DMG" is a banned sentence.</b> By the time control reaches
/// here the user has typed a path and been refused, and the only thing that makes
/// the refusal useful is naming what they typed. "That is an Apple installer
/// package - there is nothing to mount" ends the problem. "Not a DMG" starts a
/// support thread.
/// </para>
/// <para>
/// The naming comes from <see cref="ImageFormatSignatures"/>, the same table the
/// raw probe consults when deciding what not to swallow. Two formats get spelled
/// out because they are the ones people genuinely arrive with: NDIF, the Disk Copy
/// 6 format that any Mac download older than about 2003 is in, and the
/// <c>.sparsebundle</c>, which is a folder rather than a file and so fails in a way
/// that otherwise makes no sense at all.
/// </para>
/// <para>
/// When nothing matches, the refusal still carries evidence: how long the file is,
/// whether it is a whole number of sectors, and the first bytes in hex and ASCII.
/// That is enough for a bug report to be actionable without the file being sent
/// anywhere.
/// </para>
/// </remarks>
public sealed class UnknownImageProbe : IImageFormatProbe
{
    /// <summary>The shared instance. The probe is stateless.</summary>
    public static UnknownImageProbe Instance { get; } = new();

    private UnknownImageProbe()
    {
    }

    /// <inheritdoc />
    public ImageFormat Format => ImageFormat.Unrecognised;

    /// <inheritdoc />
    public string Name => "unknown";

    /// <inheritdoc />
    public bool IsTerminal => true;

    /// <inheritdoc />
    public bool Recognises(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Everything. That is what makes the chain total: no file can reach the end
        // of it without being answered for.
        return true;
    }

    /// <inheritdoc />
    public Result<ImageFormatDetection> Describe(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Length == 0)
        {
            return Result<ImageFormatDetection>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"{image.Describe()} is empty, so there is no disk image in it.",
                "The file is zero bytes long.");
        }

        ImageFormatSignature? signature = ImageFormatSignatures.Identify(
            image.Header,
            image.Length,
            image.SourcePath);

        if (signature is not null)
        {
            return Result<ImageFormatDetection>.Failure(signature.ToError());
        }

        return Result<ImageFormatDetection>.Failure(UnrecognisedError(image));
    }

    /// <summary>
    /// The last resort: nothing matched, so say what was looked at and why the two
    /// openable formats both declined.
    /// </summary>
    private static DmgError UnrecognisedError(ImageProbeContext image)
    {
        // Being off a sector boundary is the single most useful thing that can be
        // said about an unrecognised file, because it is the reason the raw probe
        // walked away from it - and it is usually a truncated download.
        string why = image.IsWholeSectors
            ? "It has no koly trailer, so it is not a UDIF image, and nothing in it identifies "
              + "another format."
            : $"It has no koly trailer, and its length is not a multiple of "
              + $"{BigEndian.SectorSize} bytes, so it cannot be a raw sector image either. "
              + "A part-sector length usually means the download was cut short.";

        return new DmgError(
            DmgExitCode.UnsupportedFormat,
            $"dmg does not recognise {image.Describe()} as a disk image. {why}",
            $"{image.Length} bytes; {ImageFormatSignatures.DescribeLeadingBytes(image.Header)}.");
    }
}
