namespace Dmg.Core.Codecs;

/// <summary>
/// Turns the bytes of one blkx chunk into the sectors it represents.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are stateless and must be safe to share across threads: the
/// registry hands the same instance to every caller. All state for a decode lives
/// in the two spans.
/// </para>
/// <para>
/// <b>Implementations never throw on bad data.</b> A truncated token, a length that
/// runs off the end of the buffer, a back-reference before the start of the output -
/// all of those are <see cref="Result{T}"/> failures carrying
/// <see cref="DmgExitCode.CorruptImage"/>. Exceptions are for bugs in this code,
/// not for hostile input.
/// </para>
/// <para>
/// <b>Implementations do not decide how big the output may be.</b> They write into
/// the span they are given and refuse to write past its end; the size of that span
/// is the declared chunk length, and the registry is the one place that computes
/// and enforces it - see <see cref="ChunkDecoderRegistry"/>.
/// </para>
/// </remarks>
public interface IChunkDecoder
{
    /// <summary>
    /// The blkx <c>EntryType</c> this decoder claims. See
    /// <see cref="ChunkEntryTypeCodes"/> and
    /// <see cref="Dmg.Core.Containers.ChunkEntryType"/>.
    /// </summary>
    uint EntryType { get; }

    /// <summary>
    /// The codec's name as printed in errors and in <c>dmg info</c> - "zlib", "ADC",
    /// "raw".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// False when the chunk's payload is implied rather than stored - zero fill and
    /// ignore. A caller may then skip the data fork read entirely.
    /// </summary>
    bool ReadsDataFork { get; }

    /// <summary>
    /// Decodes one chunk.
    /// </summary>
    /// <param name="source">
    /// The <c>CompressedLength</c> bytes read from the data fork. Empty for the
    /// decoders whose <see cref="ReadsDataFork"/> is false.
    /// </param>
    /// <param name="destination">
    /// Exactly <c>SectorCount * 512</c> bytes, to be filled completely.
    /// </param>
    /// <returns>
    /// The number of bytes written on success - which the registry then checks
    /// against the declared length - or a failure describing what was wrong with the
    /// input.
    /// </returns>
    Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination);
}
