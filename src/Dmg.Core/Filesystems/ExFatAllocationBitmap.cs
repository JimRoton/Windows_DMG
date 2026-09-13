using System.Globalization;

namespace Dmg.Core.Filesystems;

/// <summary>
/// An exFAT volume's allocation bitmap: one bit per cluster, set when the cluster
/// holds data.
/// </summary>
/// <remarks>
/// <para>
/// <b>This answers a different question from a zero map, and the difference
/// matters.</b> A cluster being unallocated means the filesystem is not using it.
/// It does <em>not</em> mean the cluster is zeros: on an image captured from a real
/// disk, free clusters hold whatever was deleted from them. So this must never be
/// used to decide that a range can be skipped when the output has to be a faithful
/// copy of the input - only when the output merely has to mount and read the same
/// as the original, which is what <c>dmg mount</c> needs and what <c>dmg extract</c>
/// and <c>dmg verify</c> must not accept.
/// </para>
/// <para>
/// <b>Bit <i>i</i> is cluster <i>i + 2</i>.</b> exFAT reserves cluster numbers 0
/// and 1, so the heap starts at 2 and the bitmap starts there with it. Off-by-two
/// here would report the wrong clusters free, which on the mount path means a
/// volume with holes where its data should be.
/// </para>
/// <para>
/// <b>Everything outside the heap answers "in use".</b> The boot region, the FAT
/// and the bitmap itself all live below the cluster heap and are described by no
/// bit in it. A range that reaches any of them is not free, and a range whose
/// arithmetic overflows or falls off the end is not free either - the safe answer
/// to "may I skip this?" is always no.
/// </para>
/// </remarks>
public sealed class ExFatAllocationBitmap
{
    /// <summary>The first cluster number the heap addresses. 0 and 1 are reserved.</summary>
    private const uint FirstDataCluster = 2;

    private readonly byte[] _bits;
    private readonly uint _clusterCount;
    private readonly long _bytesPerCluster;

    /// <summary>Wraps the bitmap's bytes with the geometry needed to read them.</summary>
    /// <param name="bits">The bitmap's contents, one bit per cluster.</param>
    /// <param name="clusterCount">How many clusters the heap holds.</param>
    /// <param name="bytesPerCluster">How many bytes one cluster covers.</param>
    /// <param name="heapOffsetBytes">
    /// Where the cluster heap starts, in bytes from the start of the volume.
    /// </param>
    /// <remarks>
    /// The heap offset is carried here rather than asked of every caller. A caller
    /// that has to supply it is a caller that can supply the wrong one, and on the
    /// mount path a wrong heap offset reports the wrong clusters free - which puts
    /// holes in a volume rather than merely making it slower.
    /// </remarks>
    internal ExFatAllocationBitmap(
        byte[] bits,
        uint clusterCount,
        long bytesPerCluster,
        long heapOffsetBytes)
    {
        ArgumentNullException.ThrowIfNull(bits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerCluster);
        ArgumentOutOfRangeException.ThrowIfNegative(heapOffsetBytes);

        _bits = bits;
        _clusterCount = clusterCount;
        _bytesPerCluster = bytesPerCluster;
        HeapOffsetBytes = heapOffsetBytes;
    }

    /// <summary>How many clusters the bitmap describes.</summary>
    public uint ClusterCount => _clusterCount;

    /// <summary>How many bytes one cluster covers.</summary>
    public long BytesPerCluster => _bytesPerCluster;

    /// <summary>Where the cluster heap starts, in bytes from the start of the volume.</summary>
    public long HeapOffsetBytes { get; }

    /// <summary>How many of those clusters are in use.</summary>
    /// <remarks>
    /// Counted once, on demand, so that a caller can report "this volume is 12% full"
    /// without walking the bitmap itself. The count stops at
    /// <see cref="ClusterCount"/> rather than at the end of the byte array: the last
    /// byte usually carries bits for clusters that do not exist, and counting those
    /// would overstate a nearly-empty volume.
    /// </remarks>
    public uint AllocatedClusters
    {
        get
        {
            uint used = 0;

            for (uint cluster = FirstDataCluster; cluster < FirstDataCluster + _clusterCount; cluster++)
            {
                if (IsAllocated(cluster))
                {
                    used++;
                }
            }

            return used;
        }
    }

    /// <summary>Whether <paramref name="cluster"/> is in use.</summary>
    /// <remarks>
    /// A cluster outside the heap, or outside the bytes the bitmap actually
    /// supplied, is reported as in use. Both are "we do not know", and not knowing
    /// has to mean "do not skip it".
    /// </remarks>
    public bool IsAllocated(uint cluster)
    {
        if (cluster < FirstDataCluster || cluster >= FirstDataCluster + _clusterCount)
        {
            return true;
        }

        uint index = cluster - FirstDataCluster;
        int position = (int)(index / 8);

        if (position >= _bits.Length)
        {
            return true;
        }

        return (_bits[position] & (1 << (int)(index % 8))) != 0;
    }

    /// <summary>
    /// Whether every cluster touched by <paramref name="length"/> bytes at
    /// <paramref name="volumeOffset"/> is free.
    /// </summary>
    /// <param name="volumeOffset">
    /// A byte offset from the start of the <b>volume</b> - not the disk. The caller
    /// is responsible for having windowed the two together.
    /// </param>
    /// <param name="length">How many bytes the range covers.</param>
    /// <remarks>
    /// <para>
    /// Every cluster the range touches must be free, including the ones it only
    /// partly covers: a block that is half free and half a file is not a block that
    /// can be left out.
    /// </para>
    /// <para>
    /// Ranges that start below the cluster heap - the boot sector, the FAT - always
    /// answer false, because the bitmap describes none of that and the mount would
    /// be worthless without it.
    /// </para>
    /// </remarks>
    public bool IsRangeFree(long volumeOffset, long length)
    {
        long heapOffsetBytes = HeapOffsetBytes;

        if (volumeOffset < 0 || length <= 0)
        {
            return false;
        }

        long end;

        try
        {
            end = checked(volumeOffset + length);
        }
        catch (OverflowException)
        {
            return false;
        }

        // Anything reaching below the heap includes structures the bitmap says
        // nothing about.
        if (volumeOffset < heapOffsetBytes)
        {
            return false;
        }

        long firstIndex = (volumeOffset - heapOffsetBytes) / _bytesPerCluster;
        long lastIndex = (end - 1 - heapOffsetBytes) / _bytesPerCluster;

        if (lastIndex >= _clusterCount)
        {
            // Past the end of the heap. The writer pads the disk out to whole
            // blocks, and that tail is not the bitmap's to describe.
            return false;
        }

        for (long index = firstIndex; index <= lastIndex; index++)
        {
            if (IsAllocated((uint)(index + FirstDataCluster)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A one-line summary, for verbose output.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{AllocatedClusters:N0} of {_clusterCount:N0} clusters in use");
}
