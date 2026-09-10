namespace Dmg.Core.Containers;

/// <summary>
/// One chunk placed on the decoded disk: an absolute sector range and where its
/// bytes come from.
/// </summary>
/// <param name="StartSector">Absolute start sector on the decoded disk.</param>
/// <param name="SectorCount">Length of the run in 512-byte sectors.</param>
/// <param name="EntryType">The codec, or zero-fill/ignore.</param>
/// <param name="CompressedOffset">Byte offset into the data fork.</param>
/// <param name="CompressedLength">Bytes to read and hand to the codec.</param>
/// <param name="RegionIndex">Which blkx region this chunk came from, for messages.</param>
public readonly record struct Extent(
    ulong StartSector,
    ulong SectorCount,
    ChunkEntryType EntryType,
    ulong CompressedOffset,
    ulong CompressedLength,
    int RegionIndex)
{
    /// <summary>
    /// The sector one past the end of this extent.
    /// </summary>
    /// <exception cref="OverflowException">
    /// The extent's own arithmetic overflows. <see cref="ExtentIndex.Build"/>
    /// rejects such an extent before it can reach an index, so this can only fire
    /// on one built by hand - which is a bug, not a corrupt image.
    /// </exception>
    public ulong EndSectorExclusive => checked(StartSector + SectorCount);

    /// <summary>True when the sector falls inside this extent.</summary>
    public bool Contains(ulong sector) =>
        sector >= StartSector && sector - StartSector < SectorCount;

    /// <inheritdoc />
    public override string ToString() =>
        $"[{StartSector}, {StartSector + SectorCount}) {EntryType} from region {RegionIndex}";
}

/// <summary>
/// Every chunk of every region, sorted by absolute start sector, with the promise
/// that they tile <c>[0, koly.SectorCount)</c> exactly.
/// </summary>
/// <remarks>
/// <para>
/// A flat sorted array and a binary search, not a tree: the whole point of this
/// structure is to answer "which chunk holds sector N" on the read path, and a
/// contiguous array of small structs beats anything with pointers in it for that.
/// A large image runs to a few thousand extents.
/// </para>
/// <para>
/// <b>The tiling is checked once, here.</b> A gap means some sector of the disk
/// has no definition; an overlap means two chunks both claim to define one. Either
/// way the image is <see cref="DmgExitCode.CorruptImage"/> and the caller finds out
/// at open time rather than discovering it halfway through a mount.
/// </para>
/// </remarks>
public sealed class ExtentIndex
{
    private readonly Extent[] _extents;
    private readonly ulong[] _starts;

    private ExtentIndex(Extent[] extents, ulong[] starts, ulong totalSectors)
    {
        _extents = extents;
        _starts = starts;
        TotalSectors = totalSectors;
    }

    /// <summary>The size of the decoded disk, from the koly trailer.</summary>
    public ulong TotalSectors { get; }

    /// <summary>The extents, ordered by start sector.</summary>
    public IReadOnlyList<Extent> Extents => _extents;

    /// <summary>How many extents the disk is made of.</summary>
    public int Count => _extents.Length;

    /// <summary>
    /// Builds the index from the parsed regions and checks that they tile the disk.
    /// </summary>
    /// <param name="regions">The mish blocks, in blkx order.</param>
    /// <param name="totalSectors">The koly trailer's <c>SectorCount</c>.</param>
    public static Result<ExtentIndex> Build(IReadOnlyList<MishBlock> regions, ulong totalSectors)
    {
        ArgumentNullException.ThrowIfNull(regions);

        var extents = new List<Extent>();

        for (int index = 0; index < regions.Count; index++)
        {
            MishBlock region = regions[index];

            foreach (ChunkDescriptor chunk in region.Chunks)
            {
                if (chunk.SectorCount == 0)
                {
                    // Covers nothing. Real images do write these; they are not an
                    // error, they simply have no place in a map of the disk.
                    continue;
                }

                Result<ulong> start = region.AbsoluteStartSectorOf(chunk);

                if (!start.TryGetValue(out ulong startSector))
                {
                    return start.CastFailure<ExtentIndex>();
                }

                Result<ulong> end = BigEndian.Add(startSector, chunk.SectorCount, "extent");

                if (!end.Ok)
                {
                    return end.CastFailure<ExtentIndex>();
                }

                extents.Add(new Extent(
                    startSector,
                    chunk.SectorCount,
                    chunk.EntryType,
                    chunk.CompressedOffset,
                    chunk.CompressedLength,
                    index));
            }
        }

        Extent[] sorted = [.. extents.OrderBy(extent => extent.StartSector)];

        Result validated = Validate(sorted, totalSectors);

        if (!validated.Ok)
        {
            return validated.CastFailure<ExtentIndex>();
        }

        ulong[] starts = new ulong[sorted.Length];

        for (int index = 0; index < sorted.Length; index++)
        {
            starts[index] = sorted[index].StartSector;
        }

        return Result<ExtentIndex>.Success(new ExtentIndex(sorted, starts, totalSectors));
    }

    /// <summary>
    /// The extent holding <paramref name="sector"/>, by binary search. False when
    /// the sector is past the end of the disk.
    /// </summary>
    public bool TryFind(ulong sector, out Extent extent)
    {
        int index = Array.BinarySearch(_starts, sector);

        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index >= 0 && _extents[index].Contains(sector))
        {
            extent = _extents[index];
            return true;
        }

        extent = default;
        return false;
    }

    /// <summary>The extent holding <paramref name="sector"/>, or a failure naming it.</summary>
    public Result<Extent> Find(ulong sector) => TryFind(sector, out Extent extent)
        ? Result<Extent>.Success(extent)
        : Result<Extent>.Failure(
            DmgExitCode.CorruptImage,
            "The image has no data for a sector that was asked for.",
            $"Sector {sector} of a {TotalSectors}-sector disk.");

    private static Result Validate(Extent[] sorted, ulong totalSectors)
    {
        ulong expected = 0;

        foreach (Extent extent in sorted)
        {
            if (extent.StartSector < expected)
            {
                return Result.Failure(
                    DmgExitCode.CorruptImage,
                    "Two regions of the image claim the same sectors.",
                    $"Region {extent.RegionIndex} starts at sector {extent.StartSector}, " +
                    $"inside a run that already reaches sector {expected}.");
            }

            if (extent.StartSector > expected)
            {
                return Result.Failure(
                    DmgExitCode.CorruptImage,
                    "The image leaves part of the disk undefined.",
                    $"Nothing describes sectors {expected}..{extent.StartSector}; " +
                    $"the next run belongs to region {extent.RegionIndex}.");
            }

            expected = extent.StartSector + extent.SectorCount; // Checked during Build.
        }

        if (expected == totalSectors)
        {
            return Result.Success();
        }

        return expected < totalSectors
            ? Result.Failure(
                DmgExitCode.CorruptImage,
                "The image describes less of the disk than it claims to be.",
                $"The regions end at sector {expected}; the trailer says {totalSectors}.")
            : Result.Failure(
                DmgExitCode.CorruptImage,
                "The image describes more of the disk than it claims to be.",
                $"The regions end at sector {expected}; the trailer says {totalSectors}.");
    }
}
