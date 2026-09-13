using Dmg.Core.Projection;

namespace Dmg.Core.Tests.Projection;

/// <summary>
/// The lock that makes the projection safe to hand to ProjFS.
/// </summary>
/// <remarks>
/// <para>
/// The bug this prevents is silent: two threads interleaving a seek and a read on
/// one stream swap each other's bytes, with no exception thrown and nothing to see
/// afterwards except wrong data. A test that merely called the decorator once would
/// not notice it existed, so the inner content here reports overlap directly - it
/// counts callers inside itself and remembers whether that count ever exceeded one.
/// </para>
/// <para>
/// The unsynchronised case is asserted too. Without it, these tests would still
/// pass if the decorator's locks were deleted, and a test that passes when the
/// thing under test is removed is not a test.
/// </para>
/// </remarks>
public sealed class SynchronizedProjectedContentTests
{
    private const int Threads = 8;
    private const int CallsPerThread = 50;

    [Fact]
    public void ConcurrentCallersNeverOverlapInsideTheContent()
    {
        OverlapDetectingContent inner = new();
        SynchronizedProjectedContent content = new(inner);

        Hammer(content);

        Assert.False(
            inner.SawOverlap,
            "Two callers were inside the content at once, which is the interleaved seek-and-read "
            + "that hands one caller another's bytes.");

        Assert.Equal(Threads * CallsPerThread * 3, inner.TotalCalls);
    }

    [Fact]
    public void TheUnsynchronisedContentDoesOverlap()
    {
        // Proves the detector detects. If this ever stops overlapping, the test
        // above has become vacuous and would pass with the decorator's locks gone.
        //
        // The overlap is arranged, not raced for. An earlier version of this test
        // simply hammered the content from several threads and trusted them to
        // interleave; that passed alone and failed under full-suite load, which is
        // the worst outcome a test can have - people learn to re-run it rather than
        // believe it. Here the first caller in waits on a rendezvous until a second
        // one arrives, so the overlap is a certainty rather than a probability.
        using RendezvousContent inner = new(expected: 2);

        Parallel.For(0, 2, _ => inner.Find("anything"));

        Assert.True(
            inner.SawOverlap,
            "The unsynchronised content never overlapped, so this suite can no longer tell "
            + "whether the decorator's lock does anything.");
    }

    [Fact]
    public void TheDecoratorPreventsTheRendezvousFromEverCompleting()
    {
        // The same arrangement with the lock in place. The second caller cannot get
        // in while the first is waiting, so the rendezvous times out instead of
        // completing - and that timeout is the proof of serialisation, from the
        // opposite direction to the counter-based test above.
        using RendezvousContent inner = new(expected: 2);
        SynchronizedProjectedContent content = new(inner);

        Parallel.For(0, 2, _ => content.Find("anything"));

        Assert.False(
            inner.SawOverlap,
            "Two callers met inside a content the decorator was supposed to be serialising.");

        Assert.True(
            inner.TimedOut,
            "The rendezvous completed, which means the two callers were inside at once.");
    }

    [Fact]
    public void ResultsArePassedThroughUnchanged()
    {
        OverlapDetectingContent inner = new();
        SynchronizedProjectedContent content = new(inner);

        Assert.True(content.List(string.Empty).Ok);
        Assert.True(content.Find("anything").Ok);

        Result<byte[]> read = content.Read("anything", 0, 4);

        Assert.True(read.TryGetValue(out byte[]? bytes), read.Ok ? "" : read.Error.ToString());
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public void WrappingNothingIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new SynchronizedProjectedContent(null!));

    private static void Hammer(IProjectedContent content)
    {
        Parallel.For(0, Threads, _ =>
        {
            for (int call = 0; call < CallsPerThread; call++)
            {
                content.List(string.Empty);
                content.Find("anything");
                content.Read("anything", 0, 4);
            }
        });
    }

    /// <summary>
    /// Content whose callers must meet inside it, so that overlap is arranged
    /// rather than raced for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each caller signals its arrival and then waits for the others. Without a
    /// lock around it they all meet and <see cref="SawOverlap"/> is set; with one,
    /// the first caller waits alone until <see cref="Timeout"/> expires and
    /// <see cref="TimedOut"/> records that nobody joined it. Both outcomes are
    /// deterministic, which a spin-and-hope version was not.
    /// </para>
    /// <para>
    /// The timeout is what stops the serialised case deadlocking the suite: the
    /// second caller is by definition unable to arrive, so waiting forever would
    /// hang rather than fail.
    /// </para>
    /// </remarks>
    private sealed class RendezvousContent(int expected) : IProjectedContent, IDisposable
    {
        /// <summary>How long a caller waits for the others before giving up.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        private readonly CountdownEvent _arrived = new(expected);

        private int _inside;
        private int _overlapped;
        private int _timedOut;

        internal bool SawOverlap => Volatile.Read(ref _overlapped) != 0;

        internal bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        public Result<IReadOnlyList<ProjectedItem>> List(string relativePath) =>
            Enter(() => Result<IReadOnlyList<ProjectedItem>>.Success([]));

        public Result<ProjectedItem?> Find(string relativePath) =>
            Enter(() => Result<ProjectedItem?>.Success(null));

        public Result<byte[]> Read(string relativePath, long offset, int count) =>
            Enter(() => Result<byte[]>.Success(new byte[count]));

        public void Dispose() => _arrived.Dispose();

        private T Enter<T>(Func<T> body)
        {
            if (Interlocked.Increment(ref _inside) > 1)
            {
                Interlocked.Exchange(ref _overlapped, 1);
            }

            try
            {
                _arrived.Signal();

                if (!_arrived.Wait(Timeout))
                {
                    Interlocked.Exchange(ref _timedOut, 1);
                }

                return body();
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }

    /// <summary>
    /// Content that notices when two callers are inside it at the same time.
    /// </summary>
    /// <remarks>
    /// The body holds the "inside" state open long enough for an overlap to be
    /// observable - a <see cref="Thread.SpinWait"/> rather than a sleep, so the
    /// suite stays fast while still leaving a window a second thread can enter.
    /// </remarks>
    private sealed class OverlapDetectingContent : IProjectedContent
    {
        private int _inside;
        private int _overlapped;
        private int _calls;

        internal bool SawOverlap => Volatile.Read(ref _overlapped) != 0;

        internal int TotalCalls => Volatile.Read(ref _calls);

        public Result<IReadOnlyList<ProjectedItem>> List(string relativePath) =>
            Enter(() => Result<IReadOnlyList<ProjectedItem>>.Success([]));

        public Result<ProjectedItem?> Find(string relativePath) =>
            Enter(() => Result<ProjectedItem?>.Success(null));

        public Result<byte[]> Read(string relativePath, long offset, int count) =>
            Enter(() => Result<byte[]>.Success(new byte[count]));

        private T Enter<T>(Func<T> body)
        {
            Interlocked.Increment(ref _calls);

            if (Interlocked.Increment(ref _inside) > 1)
            {
                Interlocked.Exchange(ref _overlapped, 1);
            }

            try
            {
                Thread.SpinWait(200);

                return body();
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}
