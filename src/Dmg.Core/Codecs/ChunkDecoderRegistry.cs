using System.Diagnostics.CodeAnalysis;

namespace Dmg.Core.Codecs;

/// <summary>
/// The one place that maps a blkx <c>EntryType</c> to the code that decodes it -
/// and the one place that answers "can this build read that?" without decoding
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, deliberately in the same object.
/// </para>
/// <para>
/// <b>Describing.</b> <see cref="Describe"/> and <see cref="Survey"/> answer for
/// any entry type, including ones no decoder exists for and ones nobody has ever
/// seen. <c>dmg info</c> is built on this: it must be able to say "this image uses
/// zlib and bzip2; bzip2 is not supported" about an image it will never open, and
/// it must do that from the chunk table alone, without reading the data fork.
/// </para>
/// <para>
/// <b>Decoding.</b> <see cref="Decode"/> looks up the decoder, bounds the output,
/// runs it, and checks the result. The bound is here rather than in each codec so
/// that there is exactly one implementation of the rule that a chunk may not expand
/// past the sectors it declares.
/// </para>
/// <para>
/// <b>That bound is the decompression-bomb guard, and it is absolute.</b> A chunk
/// declaring one sector gets 512 bytes of destination and nothing more, whatever its
/// payload inflates to; a chunk declaring an implausible number of sectors is
/// refused by <see cref="DecodedLength"/> before anyone allocates for it. When a
/// codec's input wants to produce more than the declaration allows, the decode fails
/// with <see cref="DmgExitCode.CorruptImage"/>. It never stops at the cap and
/// reports success: a silently truncated chunk is a plausible-looking image that
/// nobody wrote, which is worse than no image at all.
/// </para>
/// <para>
/// Instances are immutable and safe to share; <see cref="Default"/> is the one
/// most callers want.
/// </para>
/// </remarks>
public sealed class ChunkDecoderRegistry
{
    /// <summary>The sector size UDIF counts in. Not configurable; the format says 512.</summary>
    public const int BytesPerSector = 512;

    /// <summary>
    /// The largest a single chunk may claim to decode to: 64 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chunk's declared length is attacker-controlled - it is eight bytes read
    /// straight out of the image - and it decides how much memory a caller allocates
    /// before a single byte is decoded. A <c>SectorCount</c> of 2^40 is four bytes of
    /// typing and a terabyte of allocation, so there has to be a number above which
    /// the image is simply not believed.
    /// </para>
    /// <para>
    /// 64 MiB is that number, with room to spare: <c>hdiutil</c> writes chunks of
    /// about a megabyte, and the largest seen in a real image while building this was
    /// 608 KiB. A legitimate image that trips this ceiling would be remarkable, and
    /// it fails with an explanation rather than an OutOfMemoryException.
    /// </para>
    /// </remarks>
    public const int MaxDecodedChunkBytes = 64 * 1024 * 1024;

    private readonly Dictionary<uint, IChunkDecoder> _decoders;
    private readonly uint[] _supportedEntryTypes;

    /// <summary>
    /// Builds a registry over the given decoders.
    /// </summary>
    /// <param name="decoders">
    /// The decoders to register. Each must claim a distinct
    /// <see cref="IChunkDecoder.EntryType"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="decoders"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Two decoders claim the same entry type, or one claims a structural entry -
    /// both are wiring bugs in this code, not anything an image can cause, so they
    /// throw rather than returning a <see cref="Result"/>.
    /// </exception>
    public ChunkDecoderRegistry(IEnumerable<IChunkDecoder> decoders)
    {
        ArgumentNullException.ThrowIfNull(decoders);

        _decoders = [];

        foreach (IChunkDecoder decoder in decoders)
        {
            ArgumentNullException.ThrowIfNull(decoder, nameof(decoders));

            if (ChunkEntryType.IsStructural(decoder.EntryType))
            {
                throw new ArgumentException(
                    $"{decoder.Name} claims entry type 0x{decoder.EntryType:X8}, which is a "
                    + "structural marker and carries no payload.",
                    nameof(decoders));
            }

            if (!_decoders.TryAdd(decoder.EntryType, decoder))
            {
                throw new ArgumentException(
                    $"Two decoders claim entry type 0x{decoder.EntryType:X8}: "
                    + $"{_decoders[decoder.EntryType].Name} and {decoder.Name}.",
                    nameof(decoders));
            }
        }

        _supportedEntryTypes = [.. _decoders.Keys.Order()];
    }

    /// <summary>
    /// The registry every caller should use unless a test is substituting decoders:
    /// every codec this build implements, and nothing else.
    /// </summary>
    public static ChunkDecoderRegistry Default { get; } = new(CreateBuiltInDecoders());

    /// <summary>The entry types this build can actually decode, ascending.</summary>
    public IReadOnlyList<uint> SupportedEntryTypes => _supportedEntryTypes;

    /// <summary>True when a decoder is registered for <paramref name="entryType"/>.</summary>
    public bool IsSupported(uint entryType) => _decoders.ContainsKey(entryType);

    /// <summary>Looks up the decoder for an entry type.</summary>
    public bool TryGetDecoder(uint entryType, [NotNullWhen(true)] out IChunkDecoder? decoder) =>
        _decoders.TryGetValue(entryType, out decoder);

    /// <summary>
    /// Describes one entry type. Never fails: an entry type with no decoder, and one
    /// that is not in the format at all, both come back described rather than
    /// rejected.
    /// </summary>
    public ChunkCodecInfo Describe(uint entryType)
    {
        bool structural = ChunkEntryType.IsStructural(entryType);
        bool supported = _decoders.TryGetValue(entryType, out IChunkDecoder? decoder);

        return new ChunkCodecInfo(
            entryType,
            supported ? decoder!.Name : ChunkEntryType.NameOf(entryType),
            supported,
            structural);
    }

    /// <summary>
    /// Describes every distinct entry type in a chunk table, ascending - the codec
    /// inventory of an image, produced without touching the data fork.
    /// </summary>
    /// <param name="entryTypes">
    /// The entry types read from the blkx chunk descriptors. Duplicates are expected
    /// and collapsed; structural entries are described rather than dropped, so the
    /// caller decides whether to print them.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entryTypes"/> is null.</exception>
    public IReadOnlyList<ChunkCodecInfo> Survey(IEnumerable<uint> entryTypes)
    {
        ArgumentNullException.ThrowIfNull(entryTypes);

        return [.. entryTypes.Distinct().Order().Select(Describe)];
    }

    /// <summary>
    /// Decodes one chunk into <paramref name="destination"/>.
    /// </summary>
    /// <param name="entryType">The chunk's blkx entry type.</param>
    /// <param name="source">
    /// The <c>CompressedLength</c> bytes read from the data fork, empty for the
    /// codecs that read nothing.
    /// </param>
    /// <param name="destination">
    /// The buffer to fill. Must be at least <c>sectorCount * 512</c> bytes; only
    /// that many are written, and the decoder cannot see past them.
    /// </param>
    /// <param name="sectorCount">The chunk's declared <c>SectorCount</c>.</param>
    /// <returns>
    /// The number of bytes written - always the full declared length on success - or
    /// a failure. <see cref="DmgExitCode.UnsupportedFormat"/> when there is no
    /// decoder for the type, <see cref="DmgExitCode.CorruptImage"/> when the chunk's
    /// bytes do not decode to exactly what it declared.
    /// </returns>
    public Result<int> Decode(
        uint entryType,
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        long sectorCount)
    {
        // One place, one rule: how many bytes this chunk is allowed to become.
        Result<int> capacity = DecodedLength(sectorCount);

        if (!capacity.TryGetValue(out int declaredLength))
        {
            return capacity;
        }

        if (declaredLength > destination.Length)
        {
            return Result<int>.Failure(DmgError.Internal(
                "The buffer offered for a chunk decode is smaller than the chunk.",
                $"Declared {declaredLength} bytes, buffer is {destination.Length}."));
        }

        if (!_decoders.TryGetValue(entryType, out IChunkDecoder? decoder))
        {
            return Result<int>.Failure(UnsupportedEntryType(entryType));
        }

        // The decoder never sees more room than the chunk declared, so no codec can
        // write past its own sectors even if its input says it should.
        Span<byte> bounded = destination[..declaredLength];

        Result<int> decoded = decoder.Decode(source, bounded);

        if (!decoded.TryGetValue(out int written))
        {
            return decoded;
        }

        if (written != declaredLength)
        {
            return Result<int>.Failure(written > declaredLength
                ? ChunkDecodeErrors.Overflow(decoder.Name, declaredLength)
                : ChunkDecodeErrors.ShortOutput(decoder.Name, declaredLength, written));
        }

        return Result<int>.Success(written);
    }

    /// <summary>
    /// How many bytes a chunk of <paramref name="sectorCount"/> sectors is allowed to
    /// become - the single definition of the decompression-bomb ceiling.
    /// </summary>
    /// <param name="sectorCount">The chunk's declared <c>SectorCount</c>, straight off disk.</param>
    /// <returns>
    /// The byte length on success, or <see cref="DmgExitCode.CorruptImage"/> if the
    /// count is negative, overflows, or exceeds <see cref="MaxDecodedChunkBytes"/>.
    /// </returns>
    /// <remarks>
    /// Callers that allocate a buffer for a chunk should size it from this rather
    /// than from their own multiplication: the point of the ceiling is that the
    /// allocation never happens for a chunk that is not going to be decoded.
    /// </remarks>
    public static Result<int> DecodedLength(long sectorCount)
    {
        if (sectorCount < 0)
        {
            return Result<int>.Failure(DmgError.Corrupt(
                "A chunk declares a negative sector count.",
                $"SectorCount={sectorCount}."));
        }

        long length;

        try
        {
            length = checked(sectorCount * BytesPerSector);
        }
        catch (OverflowException)
        {
            return Result<int>.Failure(DmgError.Corrupt(
                "A chunk declares more sectors than any image could hold.",
                $"SectorCount={sectorCount} overflows when converted to bytes."));
        }

        if (length > MaxDecodedChunkBytes)
        {
            return Result<int>.Failure(DmgError.Corrupt(
                $"A chunk declares {length} bytes, past the {MaxDecodedChunkBytes}-byte "
                + "ceiling on a single chunk.",
                $"SectorCount={sectorCount}."));
        }

        return Result<int>.Success((int)length);
    }

    /// <summary>
    /// The failure for a chunk this build cannot decode. Names the codec where we
    /// know it, so the user is told "bzip2 chunks are not supported" rather than a
    /// bare hexadecimal number.
    /// </summary>
    private static DmgError UnsupportedEntryType(uint entryType) =>
        ChunkEntryType.IsStructural(entryType)
            ? DmgError.Corrupt(
                $"A {ChunkEntryType.NameOf(entryType)} entry was handed to the decoder.",
                $"EntryType 0x{entryType:X8} carries no payload and must be skipped, not decoded.")
            : DmgError.Unsupported(
                $"This image uses {ChunkEntryType.NameOf(entryType)} chunks, which this build cannot decode.",
                $"EntryType 0x{entryType:X8}.");

    /// <summary>
    /// Every codec this build implements. Stories add to this list; nothing else
    /// decides what <see cref="Default"/> contains.
    /// </summary>
    private static IEnumerable<IChunkDecoder> CreateBuiltInDecoders() =>
    [
        ZeroChunkDecoder.ZeroFill,
        ZeroChunkDecoder.Ignore,
        RawChunkDecoder.Instance,
        ZlibChunkDecoder.Instance,
        AdcChunkDecoder.Instance,
    ];
}
