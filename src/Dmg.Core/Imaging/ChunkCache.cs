namespace Dmg.Core.Imaging;

/// <summary>
/// A least-recently-used cache of decoded chunks, held to a byte budget rather
/// than a count of entries - chunks vary wildly in decoded size, so a fixed
/// entry count would let a run of small chunks starve a cache sized for the
/// image, or let one near-the-ceiling chunk evict everything else on its own.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by chunk ordinal - the same identity <see cref="DmgBlockStream"/> already
/// uses for the extent index and for prefetch bookkeeping. Safe for one writer and
/// one reader at once: <see cref="DmgBlockStream"/> is documented as single-reader,
/// but S5.4 adds a second caller from its background prefetch thread, so every
/// operation here takes a single lock. Chunk decode is the expensive part; the
/// lock only ever guards bookkeeping over already-decoded bytes.
/// </para>
/// <para>
/// A capacity of zero is a legitimate, supported configuration - it is one of the
/// two points S5.2 requires identical results at - and behaves as no cache at all:
/// every lookup misses and nothing is ever retained.
/// </para>
/// </remarks>
internal sealed class ChunkCache
{
    private readonly long _capacityBytes;
    private readonly object _gate = new();
    private readonly Dictionary<int, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private long _bytesUsed;

    /// <param name="capacityBytes">
    /// The most decoded bytes this cache may hold at once. Zero disables caching
    /// without disabling the stream - every chunk is simply decoded again next
    /// time it is needed.
    /// </param>
    internal ChunkCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
    }

    /// <summary>The configured byte budget.</summary>
    internal long CapacityBytes => _capacityBytes;

    /// <summary>
    /// True if a chunk of <paramref name="decodedLength"/> bytes could ever be
    /// retained - not whether it currently is. Used to decide whether prefetching
    /// a chunk is worth the background thread at all: there is no point decoding
    /// something early that the cache is only going to throw away immediately.
    /// </summary>
    internal bool CanHold(long decodedLength) =>
        _capacityBytes > 0 && decodedLength <= _capacityBytes;

    /// <summary>True if <paramref name="ordinal"/> is currently cached.</summary>
    internal bool Contains(int ordinal)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(ordinal);
        }
    }

    /// <summary>
    /// Looks up a chunk's decoded bytes, marking it most-recently-used on a hit.
    /// </summary>
    internal bool TryGet(int ordinal, out byte[] decoded)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(ordinal, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                decoded = node.Value.Decoded;
                return true;
            }
        }

        decoded = [];
        return false;
    }

    /// <summary>
    /// Remembers a chunk's decoded bytes, evicting the least-recently-used entries
    /// until the budget is met again.
    /// </summary>
    /// <remarks>
    /// A chunk larger than the whole budget - or any chunk at all, when the budget
    /// is zero - is not an error: it is simply not retained, and the caller who
    /// just decoded it goes on using the bytes they already have in hand. Nothing
    /// distinguishes "was cached" from "was not" at the read path; the cache is
    /// purely a speed-up.
    /// </remarks>
    internal void Set(int ordinal, byte[] decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);

        if (decoded.LongLength > _capacityBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(ordinal, out LinkedListNode<Entry>? existing))
            {
                _lru.Remove(existing);
                _bytesUsed -= existing.Value.Decoded.LongLength;
                _entries.Remove(ordinal);
            }

            LinkedListNode<Entry> node = new(new Entry(ordinal, decoded));
            _lru.AddFirst(node);
            _entries[ordinal] = node;
            _bytesUsed += decoded.LongLength;

            while (_bytesUsed > _capacityBytes && _lru.Last is not null)
            {
                LinkedListNode<Entry> victim = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(victim.Value.Ordinal);
                _bytesUsed -= victim.Value.Decoded.LongLength;
            }
        }
    }

    private readonly record struct Entry(int Ordinal, byte[] Decoded);
}
