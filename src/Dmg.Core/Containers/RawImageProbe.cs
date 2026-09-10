namespace Dmg.Core.Containers;

/// <summary>
/// The raw probe: no container, just sectors.
/// </summary>
/// <remarks>
/// <para>
/// This is the one probe that has to be careful about what it does <em>not</em>
/// claim. "A stream of 512-byte sectors" has no magic number and no structure - the
/// only positive evidence available is that the file divides by 512, and plenty of
/// files that are nothing to do with disks divide by 512. Claimed too eagerly, this
/// probe would take a ZIP, report it as a disk, and let the mount path go on to
/// hand Windows a volume full of nonsense.
/// </para>
/// <para>
/// So the rule here is negative: a file is raw when it is a whole number of sectors
/// <em>and</em> <see cref="ImageFormatSignatures"/> cannot name it as something
/// else. That is the same table the terminal probe uses for its message, which is
/// what keeps "the raw probe declined it" and "the terminal called it a ZIP" from
/// ever being two different opinions.
/// </para>
/// <para>
/// The residual risk is understood and accepted: an unrecognised format that
/// happens to be a multiple of 512 will be claimed as raw. There is no signature
/// left to distinguish it by, the user asked for this file to be treated as a disk
/// image, and the failure downstream - no partition table, no mountable filesystem -
/// is specific enough to be actionable.
/// </para>
/// </remarks>
public sealed class RawImageProbe : IImageFormatProbe
{
    /// <summary>The shared instance. The probe is stateless.</summary>
    public static RawImageProbe Instance { get; } = new();

    private RawImageProbe()
    {
    }

    /// <inheritdoc />
    public ImageFormat Format => ImageFormat.Raw;

    /// <inheritdoc />
    public string Name => "raw";

    /// <inheritdoc />
    public bool Recognises(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!image.IsWholeSectors)
        {
            return false;
        }

        return ImageFormatSignatures.Identify(image.Header, image.Length, image.SourcePath) is null;
    }

    /// <inheritdoc />
    public Result<ImageFormatDetection> Describe(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!image.IsWholeSectors)
        {
            // Unreachable through the chain, which only calls Describe after
            // Recognises. Reachable by a caller holding the probe directly, and a
            // sector count computed from a length that is not a whole number of
            // sectors is exactly the sort of silently-wrong number this codebase
            // refuses to produce.
            return Result<ImageFormatDetection>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"{image.Describe()} is not a whole number of 512-byte sectors, so it cannot be "
                + "read as a raw disk image.",
                $"{image.Length} bytes is {image.Length % BigEndian.SectorSize} bytes past a "
                + "sector boundary.");
        }

        ulong sectors = (ulong)image.SectorCount;

        return Result<ImageFormatDetection>.Success(new ImageFormatDetection(
            ImageFormat.Raw,
            Name,
            image.Length,
            sectors,
            $"Raw sector image: {sectors} sectors, {image.Length} bytes. No UDIF container."));
    }
}
