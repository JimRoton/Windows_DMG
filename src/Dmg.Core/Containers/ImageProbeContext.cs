namespace Dmg.Core.Containers;

/// <summary>
/// Everything a probe is allowed to look at: the front of the file, the back of
/// the file, how long it really is, and where it came from.
/// </summary>
/// <remarks>
/// <para>
/// The context does all of the reading, once, before any probe runs. That is the
/// point of it. A chain of four probes that each seek and read would touch the
/// disk four times and, worse, would give four places for a bounds check to be
/// forgotten; here there are exactly two reads, both clamped to
/// <see cref="Length"/>, and every probe downstream is a pure function over two
/// spans. Pure functions are what let the awkward cases - a nine-byte file, a file
/// that is exactly 512 bytes, a file whose last 512 bytes are also its first - be
/// tested without a filesystem.
/// </para>
/// <para>
/// <see cref="Header"/> and <see cref="Trailer"/> are as long as the file allows
/// and no longer. A short file yields short spans rather than a failure, so
/// "truncated" is something the probes decide about, not something the reader
/// decides for them.
/// </para>
/// <para>
/// <see cref="Stream"/> is carried for the probe that eventually needs to read
/// more than the ends - it is left positioned wherever the last read put it, so a
/// caller that goes on to parse the image seeks first, as the parsers already do.
/// </para>
/// </remarks>
public sealed class ImageProbeContext
{
    /// <summary>
    /// How much of the front of the file every probe gets to see: 64 KiB.
    /// </summary>
    /// <remarks>
    /// Sized by the furthest signature this tool looks for rather than by taste.
    /// ISO 9660 puts its volume descriptor at 32 KiB, which is both the deepest
    /// check here and a real case - a renamed .iso is a common thing to be handed.
    /// Everything else lives in the first few hundred bytes.
    /// </remarks>
    public const int HeaderBytes = 64 * 1024;

    /// <summary>How much of the back of the file is read: one koly trailer's worth.</summary>
    public const int TrailerBytes = KolyTrailer.Size;

    private readonly byte[] _header;
    private readonly byte[] _trailer;

    private ImageProbeContext(Stream stream, long length, string? sourcePath, byte[] header, byte[] trailer)
    {
        Stream = stream;
        Length = length;
        SourcePath = sourcePath;
        _header = header;
        _trailer = trailer;
    }

    /// <summary>The stream the image was opened from. Seekable and readable.</summary>
    public Stream Stream { get; }

    /// <summary>The real length of the file in bytes, read once and trusted thereafter.</summary>
    public long Length { get; }

    /// <summary>
    /// Where the bytes came from, when the caller knew. Null for an in-memory
    /// stream. Only the sparse-bundle check needs it, and only to say the name back.
    /// </summary>
    public string? SourcePath { get; }

    /// <summary>The first <see cref="HeaderBytes"/> bytes, or the whole file if it is shorter.</summary>
    public ReadOnlySpan<byte> Header => _header;

    /// <summary>
    /// The last <see cref="TrailerBytes"/> bytes, or the whole file if it is shorter.
    /// Empty for an empty file.
    /// </summary>
    public ReadOnlySpan<byte> Trailer => _trailer;

    /// <summary>True when the file holds a whole number of 512-byte sectors and at least one.</summary>
    public bool IsWholeSectors => Length >= BigEndian.SectorSize && Length % BigEndian.SectorSize == 0;

    /// <summary>The number of whole 512-byte sectors in the file.</summary>
    public long SectorCount => Length / BigEndian.SectorSize;

    /// <summary>
    /// Reads the two windows a probe chain needs.
    /// </summary>
    /// <param name="stream">A readable, seekable stream over the whole image.</param>
    /// <param name="sourcePath">The path it came from, if any.</param>
    /// <returns>
    /// The context, or <see cref="DmgExitCode.InternalError"/> if the stream cannot
    /// be used at all, or <see cref="DmgExitCode.CorruptImage"/> if a read inside the
    /// file's own length failed - a file that shrank underneath us, or a device
    /// error.
    /// </returns>
    public static Result<ImageProbeContext> Create(Stream stream, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return Result<ImageProbeContext>.Failure(DmgError.Internal(
                "An image must be probed from a readable, seekable stream.",
                $"CanRead={stream.CanRead}, CanSeek={stream.CanSeek}."));
        }

        long length;

        try
        {
            length = stream.Length;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Result<ImageProbeContext>.Failure(DmgError.Internal(
                "Could not determine the size of the image file.",
                exception.Message));
        }

        if (length < 0)
        {
            return Result<ImageProbeContext>.Failure(DmgError.Internal(
                "The image stream reported a negative length.",
                $"Stream.Length = {length}."));
        }

        // Both windows are clamped to what actually exists, and they are allowed to
        // overlap: in a 300-byte file the header and the trailer are the same 300
        // bytes, which is the correct answer rather than an edge case to reject.
        int headerLength = (int)Math.Min(length, HeaderBytes);
        int trailerLength = (int)Math.Min(length, TrailerBytes);

        byte[] header = new byte[headerLength];
        byte[] trailer = new byte[trailerLength];

        try
        {
            if (headerLength > 0)
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.ReadExactly(header);
            }

            if (trailerLength > 0)
            {
                stream.Seek(length - trailerLength, SeekOrigin.Begin);
                stream.ReadExactly(trailer);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return Result<ImageProbeContext>.Failure(
                DmgExitCode.CorruptImage,
                "Could not read the image file.",
                exception.Message);
        }

        return Result<ImageProbeContext>.Success(
            new ImageProbeContext(stream, length, sourcePath, header, trailer));
    }

    /// <summary>The file's name for a message, falling back to a neutral noun.</summary>
    public string Describe()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return "this image";
        }

        return $"'{SourcePath}'";
    }
}
