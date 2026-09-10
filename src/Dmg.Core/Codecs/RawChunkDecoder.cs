namespace Dmg.Core.Codecs;

/// <summary>
/// Raw chunks, <c>0x00000001</c>: the data fork bytes are the sectors, with no
/// codec in between. UDRW and UDTO images are made of nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The decode is a copy. The interesting part is the length check: a raw chunk's
/// <c>CompressedLength</c> must equal <c>SectorCount * 512</c> exactly, because
/// there is no compression to explain a difference. Anything else is a corrupt
/// image, and it is worth catching here rather than later.
/// </para>
/// <para>
/// A short source is the dangerous one. If it were accepted, the tail of the chunk
/// would be whatever the destination buffer happened to hold - either zeros, which
/// silently corrupts the mounted filesystem, or a previous chunk's bytes, which
/// leaks them into a region of the image that should never have contained them.
/// A long source is less dangerous but no more explicable, so both fail.
/// </para>
/// </remarks>
public sealed class RawChunkDecoder : IChunkDecoder
{
    private RawChunkDecoder()
    {
    }

    /// <summary>The shared instance.</summary>
    public static RawChunkDecoder Instance { get; } = new();

    /// <inheritdoc />
    public uint EntryType => ChunkEntryType.Raw;

    /// <inheritdoc />
    public string Name => "raw";

    /// <summary>True: a raw chunk is entirely data fork bytes.</summary>
    public bool ReadsDataFork => true;

    /// <summary>
    /// Copies the chunk's bytes into <paramref name="destination"/>, insisting that
    /// there are exactly as many as the chunk declared sectors for.
    /// </summary>
    public Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
        {
            return Result<int>.Failure(ChunkDecodeErrors.Malformed(
                Name,
                $"A raw chunk carries {source.Length} bytes but declares "
                + $"{destination.Length}; the two must match exactly."));
        }

        source.CopyTo(destination);
        return Result<int>.Success(destination.Length);
    }
}
