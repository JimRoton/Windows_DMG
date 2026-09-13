namespace Dmg.Core.Projection;

/// <summary>
/// Serialises access to an <see cref="IProjectedContent"/> so it can be handed to
/// something that calls it from several threads at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not optional.</b> The Windows Projected File System
/// (<a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>) does
/// not call a provider on one thread. It dispatches callbacks from its own pool,
/// and a user opening two files in Explorer, or one file while a directory listing
/// is still being collected, produces overlapping calls as a matter of course.
/// </para>
/// <para>
/// <b>What breaks without it.</b> <c>ExFatReader</c> reads through a
/// <c>VolumeReader</c>, and a <c>VolumeReader</c> seeks a shared
/// <see cref="Stream"/> and then reads from it. Those two steps are not one
/// operation. Two threads interleaving them - A seeks, B seeks, A reads - hand A
/// the bytes B asked for, with no exception and nothing in any log. It is the
/// worst shape of bug this tool could have: silent, data-dependent, and
/// indistinguishable from a corrupt image.
/// </para>
/// <para>
/// <b>Why a decorator rather than locks inside the reader.</b> The same reason
/// encryption is a decorator and not a mode inside the parser: the reader is
/// correct as a single-threaded thing and is tested as one, and the code that
/// needs concurrency is somewhere else entirely. Composing them keeps each half
/// simple - and keeps the cost where it is needed, since <c>dmg extract</c> and
/// <c>dmg info</c> have no threads and pay nothing.
/// </para>
/// <para>
/// <b>One lock, not a reader-writer.</b> Every operation here ends in a read from
/// one shared stream, so nothing genuinely runs in parallel anyway and a
/// reader-writer lock would only add cost and a way to get it wrong. If profiling
/// on a real workload ever shows this as the bottleneck, the fix is a reader per
/// thread, not a cleverer lock.
/// </para>
/// </remarks>
public sealed class SynchronizedProjectedContent : IProjectedContent
{
    private readonly IProjectedContent _inner;
    private readonly Lock _gate = new();

    /// <summary>Wraps <paramref name="inner"/> so its calls cannot overlap.</summary>
    /// <param name="inner">The content to serialise access to.</param>
    public SynchronizedProjectedContent(IProjectedContent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<ProjectedItem>> List(string relativePath)
    {
        lock (_gate)
        {
            return _inner.List(relativePath);
        }
    }

    /// <inheritdoc />
    public Result<ProjectedItem?> Find(string relativePath)
    {
        lock (_gate)
        {
            return _inner.Find(relativePath);
        }
    }

    /// <inheritdoc />
    public Result<byte[]> Read(string relativePath, long offset, int count)
    {
        lock (_gate)
        {
            return _inner.Read(relativePath, offset, count);
        }
    }
}
