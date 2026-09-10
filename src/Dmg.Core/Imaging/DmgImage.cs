using Dmg.Core.Containers;

namespace Dmg.Core.Imaging;

/// <summary>
/// A UDIF container that has been parsed but not decoded: the trailer, the block
/// map regions, and the extent index that says which chunk holds which sector.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of "opening a DMG" - steps 3 and 4 of the mount sequence -
/// and it costs one read of the trailer, one of the property list, and no bytes at
/// all of the data fork. Everything expensive happens later, per read, behind
/// <see cref="DmgBlockStream"/>.
/// </para>
/// <para>
/// It is separate from the stream because two callers want it without wanting to
/// read sectors: <c>dmg info</c> surveys the chunk table to report an image's
/// codecs, and the partition reader wants <see cref="Length"/> before it decides
/// whether to open anything. It is immutable and safe to share.
/// </para>
/// <para>
/// <b>Where a chunk's bytes actually live.</b> Three offsets add up:
/// <c>koly.DataForkOffset</c> locates the fork inside the file, the owning mish
/// block's <c>DataOffset</c> locates the region inside the fork, and the chunk's
/// own <c>CompressedOffset</c> locates the chunk inside the region. Every image
/// <c>hdiutil</c> writes leaves the middle one at zero, which is why it is so easy
/// to forget; <see cref="DataForkOffsetOf"/> is the one place that adds all three,
/// so nothing else has to remember.
/// </para>
/// </remarks>
public sealed class DmgImage
{
    private readonly ulong[] _regionDataOffsets;

    private DmgImage(
        KolyTrailer trailer,
        IReadOnlyList<MishBlock> regions,
        ExtentIndex index,
        ulong[] regionDataOffsets,
        long length)
    {
        Trailer = trailer;
        Regions = regions;
        Index = index;
        _regionDataOffsets = regionDataOffsets;
        Length = length;
    }

    /// <summary>The koly trailer this image was opened from.</summary>
    public KolyTrailer Trailer { get; }

    /// <summary>The mish block map regions, in blkx order.</summary>
    public IReadOnlyList<MishBlock> Regions { get; }

    /// <summary>Which chunk holds which sector, checked to tile the disk exactly.</summary>
    public ExtentIndex Index { get; }

    /// <summary>
    /// The size of the decoded disk in bytes - <c>koly.SectorCount * 512</c>, and
    /// therefore <see cref="DmgBlockStream.Length"/>.
    /// </summary>
    public long Length { get; }

    /// <summary>The size of the decoded disk in 512-byte sectors.</summary>
    public ulong SectorCount => Trailer.SectorCount;

    /// <summary>
    /// Parses the container at the end of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">
    /// A readable, seekable stream over the whole image. When the image is
    /// encrypted this is the decrypting decorator, not the file: nothing here
    /// knows that encryption exists.
    /// </param>
    /// <remarks>
    /// The stream position is left wherever the last read put it. Callers that go
    /// on to build a <see cref="DmgBlockStream"/> do not care, because every read
    /// through the stream seeks first.
    /// </remarks>
    public static Result<DmgImage> Open(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead || !source.CanSeek)
        {
            return Result<DmgImage>.Failure(DmgError.Internal(
                "A UDIF image must be opened from a readable, seekable stream.",
                $"CanRead={source.CanRead}, CanSeek={source.CanSeek}."));
        }

        Result<KolyTrailer> read = KolyTrailer.Read(source);

        if (!read.TryGetValue(out KolyTrailer? trailer))
        {
            return read.CastFailure<DmgImage>();
        }

        Result<PlistValue> plist = PlistReader.Read(source, trailer.XmlOffset, trailer.XmlLength);

        if (!plist.TryGetValue(out PlistValue? root))
        {
            return plist.CastFailure<DmgImage>();
        }

        Result<IReadOnlyList<BlkxEntry>> extracted = BlkxReader.Extract(root);

        if (!extracted.TryGetValue(out IReadOnlyList<BlkxEntry>? entries))
        {
            return extracted.CastFailure<DmgImage>();
        }

        List<MishBlock> regions = new(entries.Count);

        foreach (BlkxEntry entry in entries)
        {
            Result<MishBlock> parsed = MishBlock.Parse(entry.Data.Span, entry.DisplayName);

            if (!parsed.TryGetValue(out MishBlock? region))
            {
                return parsed.CastFailure<DmgImage>();
            }

            regions.Add(region);
        }

        Result<ExtentIndex> built = ExtentIndex.Build(regions, trailer.SectorCount);

        if (!built.TryGetValue(out ExtentIndex? index))
        {
            return built.CastFailure<DmgImage>();
        }

        Result<long> length = DecodedLength(trailer);

        if (!length.TryGetValue(out long decodedLength))
        {
            return length.CastFailure<DmgImage>();
        }

        ulong[] dataOffsets = new ulong[regions.Count];

        for (int position = 0; position < regions.Count; position++)
        {
            dataOffsets[position] = regions[position].DataOffset;
        }

        return Result<DmgImage>.Success(
            new DmgImage(trailer, regions, index, dataOffsets, decodedLength));
    }

    /// <summary>
    /// Where in <paramref name="source"/> an extent's compressed bytes begin:
    /// <c>koly.DataForkOffset + mish.DataOffset + chunk.CompressedOffset</c>.
    /// </summary>
    /// <param name="extent">An extent from <see cref="Index"/>.</param>
    /// <returns>
    /// The absolute byte offset, or <see cref="DmgExitCode.CorruptImage"/> if the
    /// three offsets do not add up to something inside a file.
    /// </returns>
    public Result<long> DataForkOffsetOf(Extent extent)
    {
        if (extent.RegionIndex < 0 || extent.RegionIndex >= _regionDataOffsets.Length)
        {
            return Result<long>.Failure(DmgError.Internal(
                "An extent names a block map region that does not exist.",
                $"RegionIndex={extent.RegionIndex}, {_regionDataOffsets.Length} region(s)."));
        }

        Result<ulong> withRegion = BigEndian.Add(
            Trailer.DataForkOffset,
            _regionDataOffsets[extent.RegionIndex],
            "data fork offset");

        if (!withRegion.TryGetValue(out ulong regionOffset))
        {
            return withRegion.CastFailure<long>();
        }

        Result<ulong> withChunk = BigEndian.Add(
            regionOffset,
            extent.CompressedOffset,
            "chunk offset in the data fork");

        if (!withChunk.TryGetValue(out ulong absolute))
        {
            return withChunk.CastFailure<long>();
        }

        return absolute > long.MaxValue
            ? Result<long>.Failure(DmgError.Corrupt(
                "A chunk claims to start past the largest offset a file can have.",
                $"Offset {absolute}."))
            : Result<long>.Success((long)absolute);
    }

    /// <summary>
    /// The decoded disk size as a <see cref="long"/>, which is what
    /// <see cref="Stream.Length"/> has to be.
    /// </summary>
    /// <remarks>
    /// <c>koly.SectorCount</c> is a <see cref="ulong"/> read straight off disk, so
    /// there are values that convert to bytes without overflowing 64 unsigned bits
    /// and still cannot be a stream length. Refusing them here means
    /// <see cref="Length"/> is never negative.
    /// </remarks>
    private static Result<long> DecodedLength(KolyTrailer trailer)
    {
        Result<ulong> bytes = trailer.DecodedLengthInBytes();

        if (!bytes.TryGetValue(out ulong decoded))
        {
            return bytes.CastFailure<long>();
        }

        return decoded > long.MaxValue
            ? Result<long>.Failure(DmgError.Corrupt(
                "The image claims to be larger than any stream could represent.",
                $"koly.SectorCount={trailer.SectorCount} is {decoded} bytes."))
            : Result<long>.Success((long)decoded);
    }
}
