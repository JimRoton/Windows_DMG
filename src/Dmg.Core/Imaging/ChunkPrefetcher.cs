namespace Dmg.Core.Imaging;

/// <summary>
/// Runs at most one background chunk decode at a time, on one dedicated thread,
/// for <see cref="DmgBlockStream"/>'s S5.4 sequential-read prefetch.
/// </summary>
/// <remarks>
/// <para>
/// Created lazily by the stream the first time it detects a sequential access
/// pattern, so a stream that is only ever read randomly - most of the boundary
/// and fixture tests, for instance - never spins up a thread it has no use for.
/// </para>
/// <para>
/// The queue depth is exactly one and requests are dropped, not queued, while a
/// prefetch is already running: sequential reading keeps re-deriving "the next
/// chunk" every step, so a dropped request for a chunk the caller has not reached
/// yet is simply asked for again shortly afterward. There is never a reason to
/// pile up stale work.
/// </para>
/// <para>
/// Errors from the decode delegate are swallowed. A prefetch is purely a
/// speed-up - if the chunk turns out to be unreadable or unsupported, the
/// authoritative failure is the one <see cref="DmgBlockStream"/> raises when the
/// caller's own read actually reaches that chunk.
/// </para>
/// </remarks>
internal sealed class ChunkPrefetcher : IDisposable
{
    private readonly Action<int> _prefetch;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Thread _thread;
    private int? _pendingOrdinal;
    private bool _busy;
    private bool _stopping;

    internal ChunkPrefetcher(Action<int> prefetch)
    {
        _prefetch = prefetch;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DmgBlockStream.Prefetch",
        };
        _thread.Start();
    }

    /// <summary>
    /// Asks for <paramref name="ordinal"/> to be decoded in the background.
    /// Ignored if a prefetch is already in flight.
    /// </summary>
    internal void Request(int ordinal)
    {
        lock (_gate)
        {
            if (_busy || _stopping)
            {
                return;
            }

            _busy = true;
            _pendingOrdinal = ordinal;
        }

        Signal();
    }

    /// <summary>
    /// Stops accepting new work and waits for any in-flight prefetch to finish,
    /// so the stream never disposes the source out from under a read that is
    /// still touching it.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
        }

        Signal();
        _thread.Join(TimeSpan.FromSeconds(5));
        _signal.Dispose();
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A request and a Dispose raced to wake the thread; one release is
            // all the loop needs either way.
        }
    }

    private void Run()
    {
        while (true)
        {
            _signal.Wait();

            int ordinal;

            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                if (_pendingOrdinal is not { } value)
                {
                    _busy = false;
                    continue;
                }

                ordinal = value;
                _pendingOrdinal = null;
            }

            try
            {
                _prefetch(ordinal);
            }
            catch
            {
                // Best effort only - see remarks on the type.
            }
            finally
            {
                lock (_gate)
                {
                    _busy = false;
                }
            }
        }
    }
}
