using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The map from a sector to the chunk that defines it, and the one place the whole
/// image is checked for self-consistency: the chunks must tile the disk exactly.
/// </summary>
public sealed class ExtentIndexTests
{
    [Fact]
    public void TheRegionsOfARealImageTileItsDiskExactly()
    {
        ExtentIndex index = Built(RealRegions(), 67647);

        Assert.Equal(4, index.Count);
        Assert.Equal(0ul, index.Extents[0].StartSector);
        Assert.Equal(67647ul, index.Extents[^1].EndSectorExclusive);
        Assert.Equal(67647ul, index.TotalSectors);
    }

    [Theory]
    [InlineData(0ul, 0)]        // the protective MBR, from region 0
    [InlineData(1ul, 1)]        // the FAT32 payload, from region 1
    [InlineData(1236ul, 1)]
    [InlineData(1237ul, 1)]
    [InlineData(2047ul, 1)]
    [InlineData(2048ul, 1)]
    [InlineData(67646ul, 1)]
    public void EverySectorOfARealImageResolvesToTheRegionThatDefinesIt(ulong sector, int regionIndex)
    {
        ExtentIndex index = Built(RealRegions(), 67647);

        Assert.True(index.TryFind(sector, out Extent extent));
        Assert.Equal(regionIndex, extent.RegionIndex);
        Assert.True(extent.Contains(sector));
    }

    [Fact]
    public void TheFirstSectorOfTheSecondRegionIsNotTheFirstSectorOfTheDisk()
    {
        // Sector 0 belongs to the MBR region; the FAT32 region's own first chunk
        // has SectorNumber 0 and lands at absolute sector 1. If the index were
        // built from relative sectors, both would claim sector 0 and this would be
        // an overlap failure instead.
        ExtentIndex index = Built(RealRegions(), 67647);

        Assert.True(index.TryFind(0, out Extent first));
        Assert.Equal(0, first.RegionIndex);

        Assert.True(index.TryFind(1, out Extent second));
        Assert.Equal(1, second.RegionIndex);
        Assert.Equal(1ul, second.StartSector);
    }

    [Fact]
    public void ASectorPastTheEndOfTheDiskIsNotFound()
    {
        ExtentIndex index = Built(RealRegions(), 67647);

        Assert.False(index.TryFind(67647, out _));
        Assert.False(index.TryFind(ulong.MaxValue, out _));

        Result<Extent> result = index.Find(67647);
        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("67647", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AGapBetweenRegionsIsCorruption()
    {
        MishBlock[] regions =
        [
            Region(firstSector: 0, sectorCount: 10, (0, 10)),
            Region(firstSector: 20, sectorCount: 10, (0, 10)),
        ];

        Result<ExtentIndex> result = ExtentIndex.Build(regions, 30);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("undefined", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("10..20", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AGapInsideOneRegionIsCorruption()
    {
        Result<ExtentIndex> result = ExtentIndex.Build([Region(0, 30, (0, 10), (20, 10))], 30);

        Assert.False(result.Ok);
        Assert.Contains("undefined", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingRegionsAreCorruption()
    {
        MishBlock[] regions =
        [
            Region(firstSector: 0, sectorCount: 20, (0, 20)),
            Region(firstSector: 10, sectorCount: 20, (0, 20)),
        ];

        Result<ExtentIndex> result = ExtentIndex.Build(regions, 30);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("same sectors", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoRegionsStartingAtTheSameSectorAreCorruption()
    {
        MishBlock[] regions = [Region(0, 10, (0, 10)), Region(0, 10, (0, 10))];

        Result<ExtentIndex> result = ExtentIndex.Build(regions, 10);

        Assert.False(result.Ok);
        Assert.Contains("same sectors", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiskThatStartsAtSectorOneIsAGapAtTheFront()
    {
        Result<ExtentIndex> result = ExtentIndex.Build([Region(1, 10, (0, 10))], 11);

        Assert.False(result.Ok);
        Assert.Contains("undefined", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionsThatStopShortOfTheDeclaredSizeAreCorruption()
    {
        Result<ExtentIndex> result = ExtentIndex.Build([Region(0, 10, (0, 10))], 100);

        Assert.False(result.Ok);
        Assert.Contains("less of the disk", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("100", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionsThatRunPastTheDeclaredSizeAreCorruption()
    {
        Result<ExtentIndex> result = ExtentIndex.Build([Region(0, 100, (0, 100))], 10);

        Assert.False(result.Ok);
        Assert.Contains("more of the disk", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRegionsAtAllForANonEmptyDiskIsCorruption()
    {
        Result<ExtentIndex> result = ExtentIndex.Build([], 10);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnEmptyDiskIsAnEmptyIndexRatherThanAFailure()
    {
        ExtentIndex index = Built([], 0);

        Assert.Equal(0, index.Count);
        Assert.False(index.TryFind(0, out _));
    }

    [Fact]
    public void ZeroLengthChunksAreDroppedRatherThanBreakingTheTiling()
    {
        // hdiutil writes chunks that cover no sectors; they are not corruption,
        // they just have no place on a map of the disk.
        MishBlock region = Region(0, 20, (0, 10), (10, 0), (10, 10), (20, 0));

        ExtentIndex index = Built([region], 20);

        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void RegionsMayArriveOutOfOrder()
    {
        MishBlock[] regions =
        [
            Region(firstSector: 100, sectorCount: 50, (0, 50)),
            Region(firstSector: 0, sectorCount: 100, (0, 100)),
        ];

        ExtentIndex index = Built(regions, 150);

        Assert.Equal(0ul, index.Extents[0].StartSector);
        Assert.Equal(100ul, index.Extents[1].StartSector);
    }

    [Fact]
    public void TheBinarySearchAgreesWithALinearScanAcrossAWholeDisk()
    {
        // 500 extents of varying length, tiling [0, 62750).
        var chunks = new List<(ulong Start, ulong Count)>();
        ulong at = 0;

        for (ulong index = 0; index < 500; index++)
        {
            ulong count = (index % 5) + 1;
            chunks.Add((at, count));
            at += count;
        }

        ExtentIndex index2 = Built([Region(0, at, [.. chunks])], at);

        for (ulong sector = 0; sector < at; sector++)
        {
            Assert.True(index2.TryFind(sector, out Extent found));

            Extent expected = index2.Extents.Single(extent => extent.Contains(sector));
            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public void AnExtentCarriesWhereItsBytesComeFrom()
    {
        MishBlock region = Region(0, 10, (0, 10));

        Extent extent = Built([region], 10).Extents[0];

        Assert.Equal(ChunkEntryType.Raw, extent.EntryType);
        Assert.Equal(4096ul, extent.CompressedOffset);
        Assert.Equal(5120ul, extent.CompressedLength);
        Assert.Equal(0, extent.RegionIndex);
    }

    [Fact]
    public void AnExtentThatWouldRunOffTheEndOfTheSectorSpaceIsRefused()
    {
        // MishBlock.Parse would not let this combination through, so it is built
        // directly: Build does not assume its input came from the parser.
        var region = new MishBlock(
            Version: 1,
            FirstSectorNumber: ulong.MaxValue - 5,
            SectorCount: 10,
            DataOffset: 0,
            BuffersNeeded: 0,
            BlockDescriptors: 0,
            ChecksumType: 0,
            ChunkCount: 1,
            Chunks: [new ChunkDescriptor(ChunkEntryType.Raw, 0, 8, 1, 0, 0)]);

        Result<ExtentIndex> result = ExtentIndex.Build([region], ulong.MaxValue);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    private static IReadOnlyList<MishBlock> RealRegions()
    {
        var regions = new List<MishBlock>();

        foreach (byte[] payload in RealImageSamples.MishPayloads())
        {
            Result<MishBlock> result = MishBlock.Parse(payload, "region");
            Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
            regions.Add(result.Value!);
        }

        return regions;
    }

    private static MishBlock Region(
        ulong firstSector,
        ulong sectorCount,
        params (ulong Start, ulong Count)[] chunks)
    {
        var mish = new UdifBuilder.Mish { FirstSectorNumber = firstSector, SectorCount = sectorCount };

        foreach ((ulong start, ulong count) in chunks)
        {
            mish.With(new UdifBuilder.Chunk(
                UdifBuilder.Chunk.Raw,
                start,
                count,
                CompressedOffset: 4096,
                CompressedLength: 5120));
        }

        mish.With(UdifBuilder.Chunk.End(sectorCount));

        Result<MishBlock> result = MishBlock.Parse(mish.ToArray(), "region");
        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        return result.Value!;
    }

    private static ExtentIndex Built(IReadOnlyList<MishBlock> regions, ulong totalSectors)
    {
        Result<ExtentIndex> result = ExtentIndex.Build(regions, totalSectors);
        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        return result.Value!;
    }
}
