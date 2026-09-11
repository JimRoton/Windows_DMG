using System.Buffers;
using Dmg.Core.Codecs;
using Dmg.Core.Containers;

namespace Dmg.Core.Imaging;

/// <summary>
/// The decoded disk inside a UDIF image, as an ordinary read-only
/// <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam of the whole product.</b> Everything above it - the VHD
/// writer, <c>extract</c>, <c>verify</c>'s hasher, the mount - believes it is
/// talking to a plain disk and knows nothing about DMG. The abstraction is
/// deliberately <see cref="Stream"/> rather than an interface of our own, because
/// every consumer already speaks <see cref="Stream"/>: <c>SHA256.HashData</c>
/// accepts one, a file copy accepts one, a test harness accepts one. See
/// <c>docs/adr/ADR-004</c>.
/// </para>
/// <para>
/// Everything below it is symmetrical. The stream reads from whatever
/// <see cref="Stream"/> it was handed, which for an encrypted image is the
/// decrypting decorator rather than the file. Nothing here knows that encryption
/// exists.
/// </para>
/// <para>
/// <b>The read path.</b>
/// </para>
/// <code>
/// Read(buffer)
///   └─ for each extent the read touches:
///        binary search the extent index for the sector      →  chunk #217
///        implied payload (zero fill / ignore)?              →  fill in place, no I/O
///        already decoded?                                   →  copy out
///        otherwise  read CompressedLength from the source
///                   decode through ChunkDecoderRegistry
///                   copy out
/// </code>
/// <para>
/// <b>Implied chunks never allocate.</b> Zero-fill and ignore extents are decoded
/// straight into the caller's own buffer. That is not only faster - it is what
/// makes a large sparse image readable at all, because such an extent can declare
/// far more sectors than the 64 MiB ceiling a stored chunk is held to, and
/// materialising it would be refused before it could be served.
/// </para>
/// <para>
/// <b>Failures during a read are thrown, not returned</b>, because
/// <see cref="Stream.Read(Span{byte})"/> has nowhere to put a
/// <see cref="Result{T}"/>. They arrive as <see cref="DmgStreamException"/>, which
/// is an <see cref="IOException"/> carrying the original <see cref="DmgError"/> and
/// its exit code. Opening is still a <see cref="Result{T}"/>.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Like every other <see cref="Stream"/>, one instance
/// serves one reader at a time; it has a single <see cref="Position"/> and
/// concurrent reads would fight over it. S5.4's sequential-read prefetch runs a
/// decode of the next chunk on a background thread of its own, but that is an
/// internal implementation detail with its own locking around the source stream
/// and the chunk cache - it does not make it safe to call in from a second
/// thread of the caller's.
/// </para>
/// </remarks>
public sealed class DmgBlockStream : Stream
{
    /// <summary>The sector size UDIF counts in. Not configurable; the format says 512.</summary>
    public const int BytesPerSector = ChunkDecoderRegistry.BytesPerSector;

    /// <summary>
    /// The most compressed bytes one chunk may claim before the image is refused.
    /// </summary>
    /// <remarks>
    /// <c>CompressedLength</c> is eight bytes read straight out of the image and it
    /// sizes a read buffer, so it needs a ceiling of its own. The decoded side has
    /// a separate one inside <see cref="ChunkDecoderRegistry"/>; a chunk has to
    /// satisfy both.
    /// </remarks>
    public const long MaxCompressedChunkBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The chunk cache's byte budget when a caller does not choose one. Chosen to
    /// hold a comfortable run of decoded chunks - enough that ordinary sequential
    /// or lightly-backtracking access rarely re-decodes - without holding an
    /// entire large disk in memory.
    /// </summary>
    public const long DefaultCacheCapacityBytes = 64L * 1024 * 1024;

    private readonly Stream _source;
    private readonly object _sourceLock = new();
    private readonly bool _leaveOpen;
    private readonly ChunkDecoderRegistry _registry;
    private readonly Extent[] _extents;
    private readonly ulong[] _extentStarts;
    private readonly ChunkCache _cache;
    private readonly bool _prefetchEnabled;

    // S5.4: created lazily, the first time a sequential run is long enough to act
    // on. A stream that is only ever read randomly never pays for the thread.
    private ChunkPrefetcher? _prefetcher;
    private int _lastAccessedOrdinal = -1;
    private int _sequentialStreak;

    private long _position;
    private bool _disposed;

    private DmgBlockStream(
        Stream source,
        bool leaveOpen,
        DmgImage image,
        ChunkDecoderRegistry registry,
        ChunkCache cache,
        bool prefetchEnabled)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        _registry = registry;
        _cache = cache;
        _prefetchEnabled = prefetchEnabled;
        Image = image;

        // A flat copy of the extents, and their start sectors alongside. The index
        // in Containers answers "which extent holds this sector"; the read path
        // also needs "which number is that extent", for the cache key and for
        // prefetching its successor, so the ordinals are kept here rather than
        // pushed into the container layer.
        _extents = [.. image.Index.Extents];
        _extentStarts = new ulong[_extents.Length];

        for (int ordinal = 0; ordinal < _extents.Length; ordinal++)
        {
            _extentStarts[ordinal] = _extents[ordinal].StartSector;
        }
    }

    /// <summary>The parsed container this stream decodes.</summary>
    public DmgImage Image { get; }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <summary>
    /// Always false. Writes to the image are out of scope permanently - writes to a
    /// mounted volume go through Windows into the VHD, never back through here.
    /// </summary>
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => Image.Length;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _position = value;
        }
    }

    /// <summary>
    /// Opens the image at the end of <paramref name="source"/> and returns a stream
    /// over its decoded sectors.
    /// </summary>
    /// <param name="source">
    /// A readable, seekable stream over the whole image - the file, or the
    /// decrypting decorator over it.
    /// </param>
    /// <param name="leaveOpen">
    /// True to leave <paramref name="source"/> open when this stream is disposed.
    /// </param>
    /// <param name="registry">
    /// The codecs to decode with. Defaults to <see cref="ChunkDecoderRegistry.Default"/>;
    /// tests substitute their own.
    /// </param>
    /// <param name="cacheCapacityBytes">
    /// The most decoded bytes the chunk cache may hold at once. Defaults to
    /// <see cref="DefaultCacheCapacityBytes"/>; zero disables caching without
    /// disabling the stream - S5.2 requires reads to come out identical either
    /// way, only slower at zero.
    /// </param>
    /// <param name="enablePrefetch">
    /// Whether a detected sequential-read run may decode the next chunk on a
    /// background thread ahead of being asked for it (S5.4). A cache too small to
    /// hold the next chunk disables prefetching for it regardless of this flag -
    /// there would be nowhere to keep the result.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cacheCapacityBytes"/> is negative.
    /// </exception>
    public static Result<DmgBlockStream> Open(
        Stream source,
        bool leaveOpen = false,
        ChunkDecoderRegistry? registry = null,
        long cacheCapacityBytes = DefaultCacheCapacityBytes,
        bool enablePrefetch = true)
    {
        ArgumentNullException.ThrowIfNull(source);

        Result<DmgImage> opened = DmgImage.Open(source);

        return opened.TryGetValue(out DmgImage? image)
            ? Create(source, image, leaveOpen, registry, cacheCapacityBytes, enablePrefetch)
            : opened.CastFailure<DmgBlockStream>();
    }

    /// <summary>
    /// Wraps an image that has already been parsed - for a caller that opened one
    /// to inspect it and then decided to read it.
    /// </summary>
    /// <param name="source">The stream <paramref name="image"/> was parsed from.</param>
    /// <param name="image">The parsed container.</param>
    /// <param name="leaveOpen">True to leave <paramref name="source"/> open on dispose.</param>
    /// <param name="registry">The codecs to decode with.</param>
    /// <param name="cacheCapacityBytes">
    /// The most decoded bytes the chunk cache may hold at once. See
    /// <see cref="Open"/> for the zero-disables-caching contract.
    /// </param>
    /// <param name="enablePrefetch">
    /// Whether a detected sequential-read run may prefetch the next chunk on a
    /// background thread. See <see cref="Open"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cacheCapacityBytes"/> is negative.
    /// </exception>
    public static Result<DmgBlockStream> Create(
        Stream source,
        DmgImage image,
        bool leaveOpen = false,
        ChunkDecoderRegistry? registry = null,
        long cacheCapacityBytes = DefaultCacheCapacityBytes,
        bool enablePrefetch = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheCapacityBytes);

        if (!source.CanRead || !source.CanSeek)
        {
            return Result<DmgBlockStream>.Failure(DmgError.Internal(
                "A DMG block stream must read from a readable, seekable stream.",
                $"CanRead={source.CanRead}, CanSeek={source.CanSeek}."));
        }

        return Result<DmgBlockStream>.Success(new DmgBlockStream(
            source,
            leaveOpen,
            image,
            registry ?? ChunkDecoderRegistry.Default,
            new ChunkCache(cacheCapacityBytes),
            enablePrefetch));
    }

    /// <summary>
    /// Does nothing. A read-only stream has nothing buffered to flush, and
    /// throwing would break consumers that flush indiscriminately.
    /// </summary>
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Reads decoded sectors into <paramref name="buffer"/>, crossing as many chunk
    /// boundaries as it takes.
    /// </summary>
    /// <returns>
    /// The number of bytes read: the whole of <paramref name="buffer"/> unless the
    /// end of the disk is nearer than that, and 0 at or past the end. A read that
    /// starts past <see cref="Length"/> returns 0 rather than failing - that is
    /// what every other seekable stream does.
    /// </returns>
    /// <exception cref="DmgStreamException">
    /// A chunk could not be read or decoded. The <see cref="DmgError"/> it carries
    /// says which, and why.
    /// </exception>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long remaining = Length - _position;

        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        int wanted = (int)Math.Min(buffer.Length, remaining);
        int filled = 0;

        while (filled < wanted)
        {
            int ordinal = FindOrdinal(_position);
            Extent extent = _extents[ordinal];

            long extentStart = (long)extent.StartSector * BytesPerSector;
            long extentLength = (long)extent.SectorCount * BytesPerSector;
            long offsetInExtent = _position - extentStart;

            int copy = (int)Math.Min(wanted - filled, extentLength - offsetInExtent);

            FillFromExtent(ordinal, extent, offsetInExtent, buffer.Slice(filled, copy));

            filled += copy;
            _position += copy;
        }

        return filled;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    /// <summary>
    /// Reads synchronously and hands back a completed task. Decoding is CPU work
    /// against an already-open stream; queueing it would buy latency and nothing
    /// else.
    /// </summary>
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<int>(exception);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="IOException">
    /// The requested position is before the beginning of the stream.
    /// </exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "Seek origin must be Begin, Current or End."),
        };

        if (target < 0)
        {
            throw new IOException(
                "An attempt was made to move the position before the beginning of the stream.");
        }

        // Seeking past the end is legal and does not fail; the read that follows
        // returns 0. That is the contract every seekable stream keeps.
        _position = target;
        return _position;
    }

    /// <summary>Always throws: the image is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("A DMG block stream is read-only.");

    /// <summary>Always throws: the image is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A DMG block stream is read-only.");

    /// <summary>Always throws: the image is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("A DMG block stream is read-only.");

    /// <summary>Always throws: the image is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void WriteByte(byte value) =>
        throw new NotSupportedException("A DMG block stream is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            // Stop and join the prefetch thread before touching the source: it
            // may be mid-read through the very lock that guards it, and a
            // background read racing a Dispose of the source would be undefined
            // behaviour on the underlying stream.
            _prefetcher?.Dispose();

            if (disposing && !_leaveOpen)
            {
                _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Which extent holds the byte at <paramref name="position"/>, by binary search
    /// over the start sectors.
    /// </summary>
    /// <remarks>
    /// The extent index promises the extents tile <c>[0, SectorCount)</c> exactly -
    /// it refused to build otherwise - so a position inside the stream always lands
    /// in one. Not finding one means the promise was broken, which is a bug here
    /// rather than anything an image can cause; it is still reported as a failure
    /// rather than an unhandled exception.
    /// </remarks>
    private int FindOrdinal(long position)
    {
        ulong sector = (ulong)position / BytesPerSector;
        int ordinal = Array.BinarySearch(_extentStarts, sector);

        if (ordinal < 0)
        {
            ordinal = ~ordinal - 1;
        }

        if (ordinal >= 0 && _extents[ordinal].Contains(sector))
        {
            return ordinal;
        }

        throw new DmgStreamException(DmgError.Internal(
            "The image has no data for a sector inside the disk it describes.",
            $"Sector {sector} of a {Image.SectorCount}-sector disk, at byte {position}."));
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from one extent, starting
    /// <paramref name="offsetInExtent"/> bytes into it.
    /// </summary>
    private void FillFromExtent(
        int ordinal,
        Extent extent,
        long offsetInExtent,
        Span<byte> destination)
    {
        uint entryType = (uint)extent.EntryType;

        // Implied payload - zero fill, ignore. The decoder is handed the caller's
        // own buffer, so nothing is allocated, nothing is read, and the extent may
        // be arbitrarily large. Note that the decoder still produces the bytes: we
        // do not assume here that "implied" means "zeros".
        if (_registry.TryGetDecoder(entryType, out IChunkDecoder? decoder) && !decoder.ReadsDataFork)
        {
            Result<int> implied = decoder.Decode(default, destination);

            if (!implied.TryGetValue(out int produced) || produced != destination.Length)
            {
                throw new DmgStreamException(implied.Ok
                    ? DmgError.Internal(
                        $"The {decoder.Name} decoder filled the wrong number of bytes.",
                        $"Wanted {destination.Length}, got {produced}.")
                    : implied.Error);
            }

            return;
        }

        byte[] chunk = DecodedChunk(ordinal, extent);
        chunk.AsSpan((int)offsetInExtent, destination.Length).CopyTo(destination);
    }

    /// <summary>
    /// The fully decoded bytes of one stored chunk - from the cache if S5.2 (or a
    /// same-chunk read a moment ago, or S5.4's prefetch) already put it there,
    /// decoded fresh otherwise.
    /// </summary>
    private byte[] DecodedChunk(int ordinal, Extent extent)
    {
        if (_cache.TryGet(ordinal, out byte[] cached))
        {
            NoteSequentialAccess(ordinal);
            return cached;
        }

        byte[] decoded = Decode(extent);
        _cache.Set(ordinal, decoded);

        NoteSequentialAccess(ordinal);
        return decoded;
    }

    /// <summary>
    /// S5.4: tracks whether chunk access is walking the extents in order, and
    /// once two accesses in a row have, asks the background prefetcher for the
    /// one after this one.
    /// </summary>
    /// <remarks>
    /// Waiting for a streak of two rather than acting on the very first access
    /// keeps a single random seek from spawning a needless prefetch thread - the
    /// common case for the boundary and codec-conformance suites, which seek
    /// around a small image far more than they stream through a large one.
    /// </remarks>
    private void NoteSequentialAccess(int ordinal)
    {
        bool sequential = ordinal == _lastAccessedOrdinal + 1;
        _sequentialStreak = sequential ? _sequentialStreak + 1 : 1;
        _lastAccessedOrdinal = ordinal;

        if (!_prefetchEnabled || _sequentialStreak < 2)
        {
            return;
        }

        int nextOrdinal = ordinal + 1;

        if (nextOrdinal >= _extents.Length || _cache.Contains(nextOrdinal))
        {
            return;
        }

        Extent next = _extents[nextOrdinal];
        uint entryType = (uint)next.EntryType;

        if (_registry.TryGetDecoder(entryType, out IChunkDecoder? decoder) && !decoder.ReadsDataFork)
        {
            // Implied payload: producing it is not I/O or decompression, just a
            // fill of a caller's buffer we do not have yet. Nothing to gain by
            // doing it early.
            return;
        }

        Result<int> declared = ChunkDecoderRegistry.DecodedLength((long)next.SectorCount);

        if (!declared.TryGetValue(out int declaredLength) || !_cache.CanHold(declaredLength))
        {
            // Cache can't hold it (or the length is malformed and Decode will say
            // so properly, synchronously, when the caller gets there) - disabled,
            // per S5.4's contract, rather than decoding something just to drop it.
            return;
        }

        (_prefetcher ??= new ChunkPrefetcher(PrefetchChunk)).Request(nextOrdinal);
    }

    /// <summary>
    /// Decodes one chunk on the prefetch thread and caches the result. A failure
    /// here (an unsupported codec, a corrupt chunk) propagates to
    /// <see cref="ChunkPrefetcher"/>, which swallows it by design - see its
    /// remarks: the caller's own read reaches the same chunk and reports the
    /// failure properly, synchronously, when it does.
    /// </summary>
    private void PrefetchChunk(int ordinal)
    {
        if (_disposed || _cache.Contains(ordinal) || (uint)ordinal >= (uint)_extents.Length)
        {
            return;
        }

        byte[] decoded = Decode(_extents[ordinal]);

        if (!_disposed)
        {
            _cache.Set(ordinal, decoded);
        }
    }

    /// <summary>
    /// Reads one chunk's compressed bytes from the source and runs them through the
    /// codec registry.
    /// </summary>
    private byte[] Decode(Extent extent)
    {
        uint entryType = (uint)extent.EntryType;

        Result<int> capacity = ChunkDecoderRegistry.DecodedLength((long)extent.SectorCount);

        if (!capacity.TryGetValue(out int declaredLength))
        {
            throw new DmgStreamException(capacity.Error);
        }

        byte[] destination = new byte[declaredLength];

        bool reads = _registry.TryGetDecoder(entryType, out IChunkDecoder? decoder)
            && decoder.ReadsDataFork
            && extent.CompressedLength > 0;

        if (!reads)
        {
            // Either the chunk stores nothing, or there is no decoder for it. In the
            // second case the registry produces the error that names the codec.
            Unwrap(_registry.Decode(entryType, default, destination, (long)extent.SectorCount));
            return destination;
        }

        if (extent.CompressedLength > MaxCompressedChunkBytes)
        {
            throw new DmgStreamException(DmgError.Corrupt(
                "A chunk declares more compressed bytes than this build will read at once.",
                $"CompressedLength={extent.CompressedLength}, ceiling {MaxCompressedChunkBytes}."));
        }

        int sourceLength = (int)extent.CompressedLength;
        byte[] rented = ArrayPool<byte>.Shared.Rent(sourceLength);

        try
        {
            ReadDataFork(extent, rented.AsSpan(0, sourceLength));

            Unwrap(_registry.Decode(
                entryType,
                rented.AsSpan(0, sourceLength),
                destination,
                (long)extent.SectorCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return destination;
    }

    /// <summary>Reads an extent's compressed bytes out of the source stream.</summary>
    /// <remarks>
    /// Locked: <see cref="_source"/> has one <see cref="Stream.Position"/>, and
    /// S5.4's background prefetch thread can be in here at the same moment as the
    /// caller's own read. The lock only spans the seek-and-read - decoding, which
    /// is the part actually worth doing off-thread, happens outside it.
    /// </remarks>
    private void ReadDataFork(Extent extent, Span<byte> buffer)
    {
        Result<long> located = Image.DataForkOffsetOf(extent);

        if (!located.TryGetValue(out long offset))
        {
            throw new DmgStreamException(located.Error);
        }

        try
        {
            lock (_sourceLock)
            {
                _source.Seek(offset, SeekOrigin.Begin);
                _source.ReadExactly(buffer);
            }
        }
        catch (Exception exception)
            when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new DmgStreamException(
                DmgError.Corrupt(
                    "A chunk's compressed bytes run past the end of the image.",
                    $"Wanted {buffer.Length} bytes at offset {offset}: {exception.Message}"),
                exception);
        }
    }

    /// <summary>Turns a failed decode into the exception a <see cref="Stream"/> must throw.</summary>
    private static void Unwrap(Result<int> decoded)
    {
        if (!decoded.Ok)
        {
            throw new DmgStreamException(decoded.Error);
        }
    }
}
