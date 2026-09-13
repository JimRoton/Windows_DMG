using Dmg.Core.Filesystems;

namespace Dmg.Core.Vhd;

/// <summary>
/// Answers "may this range be left out?" - which is a weaker question than
/// <see cref="IVhdSparseMap"/>'s, and produces a different file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not IVhdSparseMap.</b> That interface asks whether a range is
/// <i>certainly zeros</i>, and states that an implementation which cannot be
/// certain must say false, because a wrong answer writes a corrupt image. A
/// filesystem's free-space map cannot answer it: a cluster the filesystem is not
/// using still holds whatever was last written there, and on an image captured
/// from a real disk that is deleted file content, not zeros.
/// </para>
/// <para>
/// So a map of this kind produces a VHD that is <b>not a faithful copy</b> of its
/// source. Every byte the filesystem can reach is identical; the bytes it cannot
/// reach become zeros. For mounting a volume that is exactly right - the
/// filesystem never reads an unallocated cluster, so the mounted result is
/// indistinguishable - and it incidentally leaves deleted content behind rather
/// than copying it out of the image.
/// </para>
/// <para>
/// <b>It must never reach <c>dmg extract</c> or <c>dmg verify</c>.</b> Those
/// promise a faithful decode, and the whole differential-testing scheme rests on
/// their output hashing identically to what Apple's decoder produces. That is why
/// this is a separate type on a separate parameter rather than another
/// implementation of the existing interface: <c>extract</c> cannot pass one by
/// accident, because it has nothing of this type to pass.
/// </para>
/// <para>
/// <b>False is always the safe answer</b>, exactly as for the sparse map. An
/// implementation that is unsure says no and costs only time.
/// </para>
/// </remarks>
public interface IVhdDiscardableMap
{
    /// <summary>
    /// Whether the <paramref name="length"/> bytes at <paramref name="offset"/> may
    /// be left out of the written disk.
    /// </summary>
    /// <param name="offset">
    /// A byte offset from the start of <b>what is being written</b> - not from the
    /// start of the underlying disk. A caller writing a window out of a larger disk
    /// is responsible for having lined the two up.
    /// </param>
    /// <param name="length">How many bytes the range covers.</param>
    bool MayDiscard(long offset, long length);
}

/// <summary>
/// An exFAT volume's free space, as something the dynamic VHD writer can skip.
/// </summary>
/// <remarks>
/// <para>
/// The adapter over <see cref="ExFatAllocationBitmap"/>. It exists for one case:
/// <c>dmg mount --dynamic</c> on a volume too large to materialise in full, where
/// writing the free space is the difference between a mount that finishes and one
/// that is refused for want of disk.
/// </para>
/// <para>
/// <b>The coordinates line up by construction.</b> <c>dmg mount</c> writes a
/// window that begins at the volume's first byte, so what the writer calls offset
/// zero is the volume's offset zero, which is what the bitmap measures from. That
/// is why this needs no offset shim, unlike <see cref="DmgSparseMap"/>, which
/// answers in whole-disk terms and has to be translated. Handing this map to a
/// writer that is writing something other than exactly this volume would be a bug,
/// and the sort that shows up as a hole in a file rather than as an error.
/// </para>
/// </remarks>
public sealed class ExFatAllocationMap : IVhdDiscardableMap
{
    private readonly ExFatAllocationBitmap _bitmap;

    /// <summary>Wraps a volume's allocation bitmap.</summary>
    /// <param name="bitmap">The bitmap, read from the volume being written.</param>
    public ExFatAllocationMap(ExFatAllocationBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        _bitmap = bitmap;
    }

    /// <summary>How many of the volume's clusters are in use.</summary>
    public uint AllocatedClusters => _bitmap.AllocatedClusters;

    /// <summary>How many clusters the volume has.</summary>
    public uint ClusterCount => _bitmap.ClusterCount;

    /// <inheritdoc />
    public bool MayDiscard(long offset, long length) => _bitmap.IsRangeFree(offset, length);

    /// <summary>A one-line summary, for verbose output.</summary>
    public override string ToString() => _bitmap.ToString();
}
