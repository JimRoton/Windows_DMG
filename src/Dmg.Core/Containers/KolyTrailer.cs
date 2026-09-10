namespace Dmg.Core.Containers;

/// <summary>
/// The 512-byte <c>koly</c> trailer that closes every UDIF file, and the first
/// thing read when opening one.
/// </summary>
/// <remarks>
/// <para>
/// A UDIF file is read from the back: the trailer says where the XML property
/// list lives, where the compressed data fork lives, and how many 512-byte
/// sectors the decoded disk has. Everything else in the container is reached
/// through those three numbers, which is exactly why they are the numbers a
/// hostile image will lie about.
/// </para>
/// <para>
/// <b>Bad magic is not corruption.</b> A file that simply is not a UDIF image -
/// a raw disk image, a zip, an NDIF - gets <see cref="DmgExitCode.UnsupportedFormat"/>
/// so the caller can go and try another format. A file that says <c>koly</c> and
/// then contradicts itself gets <see cref="DmgExitCode.CorruptImage"/>: it claimed
/// to be one of ours and it is broken.
/// </para>
/// </remarks>
/// <param name="Version">Trailer version. 4 for everything this tool supports.</param>
/// <param name="Flags">Image flags, passed through uninterpreted.</param>
/// <param name="RunningDataForkOffset">Offset of this segment's data within the whole image.</param>
/// <param name="DataForkOffset">Byte offset of the data fork; 0 in single-segment images.</param>
/// <param name="DataForkLength">Byte length of the data fork.</param>
/// <param name="ResourceForkOffset">Legacy resource fork offset; 0 in v4.</param>
/// <param name="ResourceForkLength">Legacy resource fork length; 0 in v4.</param>
/// <param name="SegmentNumber">1-based index of this segment in a multi-part image.</param>
/// <param name="SegmentCount">Number of segments in the image; 1 unless split.</param>
/// <param name="XmlOffset">Byte offset of the XML property list.</param>
/// <param name="XmlLength">Byte length of the XML property list.</param>
/// <param name="ImageVariant">Image variant word, passed through uninterpreted.</param>
/// <param name="SectorCount">Total size of the decoded disk in 512-byte sectors.</param>
public sealed record KolyTrailer(
    uint Version,
    uint Flags,
    ulong RunningDataForkOffset,
    ulong DataForkOffset,
    ulong DataForkLength,
    ulong ResourceForkOffset,
    ulong ResourceForkLength,
    uint SegmentNumber,
    uint SegmentCount,
    ulong XmlOffset,
    ulong XmlLength,
    uint ImageVariant,
    ulong SectorCount)
{
    /// <summary>The fixed size of the trailer, and the only legal <c>HeaderSize</c>.</summary>
    public const int Size = 512;

    /// <summary>The trailer signature, <c>koly</c>.</summary>
    public const uint Magic = 0x6B6F6C79;

    /// <summary>The only trailer version this build understands.</summary>
    public const uint SupportedVersion = 4;

    private const int SignatureOffset = 0x000;
    private const int VersionOffset = 0x004;
    private const int HeaderSizeOffset = 0x008;
    private const int FlagsOffset = 0x00C;
    private const int RunningDataForkOffsetOffset = 0x010;
    private const int DataForkOffsetOffset = 0x018;
    private const int DataForkLengthOffset = 0x020;
    private const int ResourceForkOffsetOffset = 0x028;
    private const int ResourceForkLengthOffset = 0x030;
    private const int SegmentNumberOffset = 0x038;
    private const int SegmentCountOffset = 0x03C;
    private const int XmlOffsetOffset = 0x0D8;
    private const int XmlLengthOffset = 0x0E0;
    private const int ImageVariantOffset = 0x1E8;
    private const int SectorCountOffset = 0x1EC;

    /// <summary>True when the image is split across several files.</summary>
    public bool IsMultiPart => SegmentCount > 1;

    /// <summary>
    /// The decoded disk size in bytes. A <see cref="Result{T}"/> rather than a
    /// property because <c>SectorCount * 512</c> is checked arithmetic and a
    /// hand-built trailer may hold a value that overflows it.
    /// </summary>
    public Result<ulong> DecodedLengthInBytes() =>
        BigEndian.SectorsToBytes(SectorCount, "koly.SectorCount");

    /// <summary>
    /// Reads the trailer from the last 512 bytes of <paramref name="stream"/>.
    /// Leaves the stream position at the end of the trailer.
    /// </summary>
    public static Result<KolyTrailer> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return Result<KolyTrailer>.Failure(DmgError.Internal(
                "A UDIF image must be opened from a seekable stream.",
                $"CanRead={stream.CanRead}, CanSeek={stream.CanSeek}."));
        }

        long length;

        try
        {
            length = stream.Length;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Result<KolyTrailer>.Failure(DmgError.Internal(
                "Could not determine the size of the image file.",
                exception.Message));
        }

        if (length < Size)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This file is too small to be a UDIF disk image.",
                $"{length} bytes; a koly trailer alone is {Size}.");
        }

        byte[] buffer = new byte[Size];

        try
        {
            stream.Seek(length - Size, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "Could not read the koly trailer at the end of the image.",
                exception.Message);
        }

        return Parse(buffer, length);
    }

    /// <summary>
    /// Parses and validates a trailer.
    /// </summary>
    /// <param name="trailer">
    /// The 512 bytes at <c>fileLength - 512</c>. A longer span is accepted; only
    /// the first 512 bytes are read.
    /// </param>
    /// <param name="fileLength">
    /// The size of the whole file, which every declared offset is checked against.
    /// </param>
    public static Result<KolyTrailer> Parse(ReadOnlySpan<byte> trailer, long fileLength)
    {
        if (fileLength < Size)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This file is too small to be a UDIF disk image.",
                $"{fileLength} bytes; a koly trailer alone is {Size}.");
        }

        if (trailer.Length < Size)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "The koly trailer is truncated.",
                $"Got {trailer.Length} bytes, expected {Size}.");
        }

        if (!BigEndian.TryReadUInt32(trailer, SignatureOffset, out uint signature) || signature != Magic)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This is not a UDIF disk image: the koly trailer is missing.",
                $"Expected 'koly' in the last {Size} bytes, found " +
                $"'{BigEndian.DescribeFourCharCode(signature)}'.");
        }

        if (!BigEndian.TryReadUInt32(trailer, VersionOffset, out uint version)
            || !BigEndian.TryReadUInt32(trailer, HeaderSizeOffset, out uint headerSize)
            || !BigEndian.TryReadUInt32(trailer, FlagsOffset, out uint flags)
            || !BigEndian.TryReadUInt64(trailer, RunningDataForkOffsetOffset, out ulong runningDataForkOffset)
            || !BigEndian.TryReadUInt64(trailer, DataForkOffsetOffset, out ulong dataForkOffset)
            || !BigEndian.TryReadUInt64(trailer, DataForkLengthOffset, out ulong dataForkLength)
            || !BigEndian.TryReadUInt64(trailer, ResourceForkOffsetOffset, out ulong resourceForkOffset)
            || !BigEndian.TryReadUInt64(trailer, ResourceForkLengthOffset, out ulong resourceForkLength)
            || !BigEndian.TryReadUInt32(trailer, SegmentNumberOffset, out uint segmentNumber)
            || !BigEndian.TryReadUInt32(trailer, SegmentCountOffset, out uint segmentCount)
            || !BigEndian.TryReadUInt64(trailer, XmlOffsetOffset, out ulong xmlOffset)
            || !BigEndian.TryReadUInt64(trailer, XmlLengthOffset, out ulong xmlLength)
            || !BigEndian.TryReadUInt32(trailer, ImageVariantOffset, out uint imageVariant)
            || !BigEndian.TryReadUInt64(trailer, SectorCountOffset, out ulong sectorCount))
        {
            // Unreachable while trailer.Length >= 512, but the compiler does not
            // know that and neither should a future editor of these offsets.
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "The koly trailer is truncated.",
                $"A field ran past the end of the {trailer.Length}-byte trailer.");
        }

        if (version != SupportedVersion)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"This UDIF image is version {version}; this build reads version {SupportedVersion}.",
                "koly.Version");
        }

        if (headerSize != Size)
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "The koly trailer declares a header size other than 512 bytes.",
                $"koly.HeaderSize = {headerSize}.");
        }

        ulong fileBytes = (ulong)fileLength;
        ulong beforeTrailer = fileBytes - Size;

        if (!BigEndian.RangeFitsWithin(xmlOffset, xmlLength, beforeTrailer))
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "The koly trailer points at an XML property list outside the file.",
                $"XMLOffset={xmlOffset}, XMLLength={xmlLength}, but only {beforeTrailer} " +
                "bytes precede the trailer.");
        }

        if (!BigEndian.RangeFitsWithin(dataForkOffset, dataForkLength, fileBytes))
        {
            return Result<KolyTrailer>.Failure(
                DmgExitCode.CorruptImage,
                "The koly trailer points at a data fork outside the file.",
                $"DataForkOffset={dataForkOffset}, DataForkLength={dataForkLength}, " +
                $"file is {fileBytes} bytes.");
        }

        Result<ulong> decodedLength = BigEndian.SectorsToBytes(sectorCount, "koly.SectorCount");

        if (!decodedLength.Ok)
        {
            return decodedLength.CastFailure<KolyTrailer>();
        }

        return Result<KolyTrailer>.Success(new KolyTrailer(
            version,
            flags,
            runningDataForkOffset,
            dataForkOffset,
            dataForkLength,
            resourceForkOffset,
            resourceForkLength,
            segmentNumber,
            segmentCount,
            xmlOffset,
            xmlLength,
            imageVariant,
            sectorCount));
    }
}
