using Dmg.Core.Containers;
using Dmg.Core.Imaging;

namespace Dmg.Core.Vhd;

/// <summary>
/// Answers "is this range of the disk certainly all zeros?" without reading it.
/// </summary>
/// <remarks>
/// <para>
/// An optimisation, never a correctness requirement. The dynamic writer already
/// leaves out any block that reads back as zeros, so it produces the same file
/// with or without a map. What the map buys is not having to read - and, for a
/// compressed image, not having to <i>decompress</i> - forty gigabytes of nothing
/// to discover it was nothing.
/// </para>
/// <para>
/// <b>False is always a safe answer.</b> The contract is one-sided: true means the
/// range is definitely zeros, false means "read it and see". A map that is
/// conservative is slow; a map that is wrong writes a corrupt image, so an
/// implementation that cannot be certain must say false.
/// </para>
/// </remarks>
public interface IVhdSparseMap
{
    /// <summary>
    /// Whether the <paramref name="length"/> bytes of disk at
    /// <paramref name="offset"/> are known to be zeros without reading them.
    /// </summary>
    bool IsKnownZero(long offset, long length);
}

/// <summary>
/// The sparse map a DMG gives away for free: its own chunk table already says
/// which parts of the disk are zero-fill or ignored space.
/// </summary>
/// <remarks>
/// <para>
/// UDIF stores nothing at all for a <see cref="ChunkEntryType.ZeroFill"/> or
/// <see cref="ChunkEntryType.Ignore"/> chunk - the chunk table entry <i>is</i> the
/// data. On the sparse fixture that is most of the disk, so a dynamic write that
/// consults this map touches a few hundred kilobytes of container instead of
/// decoding 48 MB of zeros to throw them away.
/// </para>
/// <para>
/// <b>Sectors past the end of the index are zeros.</b> The writer rounds the disk
/// up to a whole sector and, with a small block size, up to a whole block; that
/// tail is padding this tool invented, and it is zero by construction.
/// </para>
/// </remarks>
public sealed class DmgSparseMap : IVhdSparseMap
{
    private readonly ExtentIndex _index;

    private DmgSparseMap(ExtentIndex index) => _index = index;

    /// <summary>Builds the map from an open block stream's chunk index.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static DmgSparseMap For(DmgBlockStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new DmgSparseMap(stream.Image.Index);
    }

    /// <summary>Builds the map straight from a chunk index.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public static DmgSparseMap For(ExtentIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        return new DmgSparseMap(index);
    }

    /// <inheritdoc />
    public bool IsKnownZero(long offset, long length)
    {
        if (offset < 0 || length < 0)
        {
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        // A range that does not begin on a sector boundary is not one this map can
        // reason about: the index is defined in sectors, and answering for a
        // partial sector would mean claiming something about bytes inside a chunk.
        if (offset % VhdFooter.SectorSize != 0)
        {
            return false;
        }

        ulong sector = (ulong)(offset / VhdFooter.SectorSize);
        ulong end;

        try
        {
            end = checked(sector + (ulong)((length + VhdFooter.SectorSize - 1) / VhdFooter.SectorSize));
        }
        catch (OverflowException)
        {
            return false;
        }

        while (sector < end)
        {
            // Past the last sector the image defines is the padding the writer
            // itself adds, which is zeros.
            if (sector >= _index.TotalSectors)
            {
                return true;
            }

            if (!_index.TryFind(sector, out Extent extent))
            {
                return false;
            }

            if (extent.EntryType is not (ChunkEntryType.ZeroFill or ChunkEntryType.Ignore))
            {
                return false;
            }

            sector = extent.EndSectorExclusive;
        }

        return true;
    }
}
