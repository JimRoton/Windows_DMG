namespace Dmg.Core.Codecs;

/// <summary>
/// The two chunk types whose payload is implied rather than stored: zero fill
/// (<c>0x00000000</c>) and ignore / free (<c>0x00000002</c>). Both emit zeros and
/// neither reads a byte of the data fork.
/// </summary>
/// <remarks>
/// <para>
/// They are one class because they decode identically. They stay two instances
/// because the distinction is real in the chunk table - zero fill is a run the
/// image deliberately zeroed, ignore is space the filesystem never allocated - and
/// <c>dmg info</c> should be able to say which one an image is full of.
/// </para>
/// <para>
/// A large sparse image is mostly these. Producing their bytes must cost a
/// <see cref="Span{T}.Clear"/> and no I/O at all, which is what
/// <see cref="IChunkDecoder.ReadsDataFork"/> tells the caller.
/// </para>
/// <para>
/// The source span is ignored rather than rejected. Real images do sometimes carry
/// a non-zero <c>CompressedLength</c> on a zero-fill chunk, and refusing to mount
/// over that would be pedantry: the chunk's meaning does not depend on those bytes,
/// so they are never read.
/// </para>
/// </remarks>
public sealed class ZeroChunkDecoder : IChunkDecoder
{
    private ZeroChunkDecoder(uint entryType, string name)
    {
        EntryType = entryType;
        Name = name;
    }

    /// <summary>The decoder for zero-fill chunks, <c>0x00000000</c>.</summary>
    public static ZeroChunkDecoder ZeroFill { get; } = new(ChunkEntryType.ZeroFill, "zero-fill");

    /// <summary>The decoder for ignore / free chunks, <c>0x00000002</c>.</summary>
    public static ZeroChunkDecoder Ignore { get; } = new(ChunkEntryType.Ignore, "ignore");

    /// <inheritdoc />
    public uint EntryType { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Always false: there is nothing in the data fork to read.</summary>
    public bool ReadsDataFork => false;

    /// <summary>
    /// Fills <paramref name="destination"/> with zeros. The source is not read.
    /// </summary>
    public Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        _ = source;
        destination.Clear();
        return Result<int>.Success(destination.Length);
    }
}
