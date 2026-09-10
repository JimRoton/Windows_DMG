using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The chunk table: 40 bytes an entry, a terminator, comment entries to skip, and
/// the relative-versus-absolute sector trap.
/// </summary>
public sealed class ChunkDescriptorTests
{
    [Fact]
    public void ReadsTheChunkTableOfAnHdiutilImage()
    {
        IReadOnlyList<byte[]> payloads = RealImageSamples.MishPayloads();

        MishBlock mbr = Parsed(payloads[0]);
        Assert.Single(mbr.Chunks); // one zlib chunk; the second entry is the terminator
        Assert.Equal(ChunkEntryType.Zlib, mbr.Chunks[0].EntryType);
        Assert.Equal(0ul, mbr.Chunks[0].SectorNumber);
        Assert.Equal(1ul, mbr.Chunks[0].SectorCount);
        Assert.Equal(31ul, mbr.Chunks[0].CompressedLength);

        MishBlock fat = Parsed(payloads[1]);
        Assert.Equal(3, fat.Chunks.Count);
        Assert.Equal(ChunkEntryType.Zlib, fat.Chunks[0].EntryType);
        Assert.Equal(ChunkEntryType.Ignore, fat.Chunks[1].EntryType);
        Assert.Equal(ChunkEntryType.Ignore, fat.Chunks[2].EntryType);
        Assert.Equal(70671ul, fat.Chunks[0].CompressedLength);
    }

    [Fact]
    public void ASectorNumberIsRelativeToItsMishBlockNotToTheDisk()
    {
        // The FAT32 region of a real image starts at sector 1 and its first chunk
        // is at relative sector 0. Absolute sector 0 is the protective MBR, which
        // belongs to a different region entirely. Getting this wrong puts every
        // region but the first at the wrong place on the disk.
        MishBlock fat = Parsed(RealImageSamples.MishPayloads()[1]);

        Assert.Equal(1ul, fat.FirstSectorNumber);
        Assert.Equal(0ul, fat.Chunks[0].SectorNumber);

        Assert.True(fat.AbsoluteStartSectorOf(fat.Chunks[0]).TryGetValue(out ulong absolute));
        Assert.Equal(1ul, absolute);
        Assert.NotEqual(fat.Chunks[0].SectorNumber, absolute);

        Assert.True(fat.AbsoluteStartSectorOf(fat.Chunks[2]).TryGetValue(out ulong third));
        Assert.Equal(2048ul, third);          // 1 + 2047
        Assert.Equal(2047ul, fat.Chunks[2].SectorNumber);
    }

    [Fact]
    public void TheAbsoluteSectorOfEveryChunkInASyntheticRegionIsOffsetByFirstSectorNumber()
    {
        byte[] block = new UdifBuilder.Mish { FirstSectorNumber = 4096, SectorCount = 30 }
            .With(
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.ZeroFill, 10, 10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Zlib, 20, 10),
                UdifBuilder.Chunk.End(30))
            .ToArray();

        MishBlock mish = Parsed(block);

        ulong[] absolute = [.. mish.Chunks.Select(chunk =>
        {
            Assert.True(mish.AbsoluteStartSectorOf(chunk).TryGetValue(out ulong sector));
            return sector;
        })];

        Assert.Equal([4096ul, 4106ul, 4116ul], absolute);
    }

    [Fact]
    public void AnAbsoluteSectorThatOverflowsIsAFailureNotAWrap()
    {
        var chunk = new ChunkDescriptor(ChunkEntryType.Raw, 0, 100, 1, 0, 0);

        Result<ulong> result = chunk.AbsoluteStartSector(ulong.MaxValue - 50);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void TheTerminatorEndsTheTableAndIsNotAChunk()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 10),
                UdifBuilder.Chunk.End(10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 10))
            .ToArray();

        MishBlock mish = Parsed(block);

        Assert.Single(mish.Chunks);
        Assert.Equal(3u, mish.ChunkCount);
    }

    [Fact]
    public void CommentEntriesAreSkipped()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 20 }
            .With(
                new UdifBuilder.Chunk(UdifBuilder.Chunk.CommentEntry, 0, 0, Comment: 0x2B626567),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 20),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.CommentEntry, 0, 0, Comment: 0x2B656E64),
                UdifBuilder.Chunk.End(20))
            .ToArray();

        MishBlock mish = Parsed(block);

        Assert.Single(mish.Chunks);
        Assert.Equal(ChunkEntryType.Raw, mish.Chunks[0].EntryType);
    }

    [Fact]
    public void ATableWithoutATerminatorIsReadToItsDeclaredCount()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 20 }
            .With(
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 10, 10))
            .ToArray();

        Assert.Equal(2, Parsed(block).Chunks.Count);
    }

    [Fact]
    public void AChunkRunningPastItsOwnRegionIsCorruption()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 11), UdifBuilder.Chunk.End(10))
            .ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("past the end of its own region", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ulong.MaxValue, 1ul)]
    [InlineData(1ul, ulong.MaxValue)]
    public void AChunkExtentThatWrapsIsCorruption(ulong sectorNumber, ulong sectorCount)
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, sectorNumber, sectorCount))
            .ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ACompressedExtentThatWrapsIsCorruption()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(new UdifBuilder.Chunk(
                UdifBuilder.Chunk.Zlib, 0, 10, CompressedOffset: ulong.MaxValue, CompressedLength: 8))
            .ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnUndecodableCodecIsParsedNotRefused()
    {
        // dmg info has to be able to say "this image is bzip2" without failing.
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(new UdifBuilder.Chunk(UdifBuilder.Chunk.Bzip2, 0, 10), UdifBuilder.Chunk.End(10))
            .ToArray();

        MishBlock mish = Parsed(block);

        Assert.Equal(ChunkEntryType.Bzip2, mish.Chunks[0].EntryType);
        Assert.False(mish.Chunks[0].IsDecodable);
        Assert.True(mish.Chunks[0].IsStored);
        Assert.Equal("Bzip2", mish.Chunks[0].DescribeEntryType());
    }

    [Fact]
    public void AnUnknownCodecIsCarriedThroughAndNamedInHex()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 10 }
            .With(new UdifBuilder.Chunk(0x8000DEAD, 0, 10), UdifBuilder.Chunk.End(10))
            .ToArray();

        MishBlock mish = Parsed(block);

        Assert.False(mish.Chunks[0].IsDecodable);
        Assert.Equal("0x8000DEAD", mish.Chunks[0].DescribeEntryType());
    }

    [Theory]
    [InlineData(0x00000000u, true)]
    [InlineData(0x00000001u, true)]
    [InlineData(0x00000002u, true)]
    [InlineData(0x80000004u, true)]
    [InlineData(0x80000005u, true)]
    [InlineData(0x80000006u, false)]
    [InlineData(0x80000007u, false)]
    [InlineData(0x80000008u, false)]
    public void TheV1CodecSetIsWhatTheReferenceSays(uint entryType, bool decodable)
    {
        var chunk = new ChunkDescriptor((ChunkEntryType)entryType, 0, 0, 1, 0, 0);

        Assert.Equal(decodable, chunk.IsDecodable);
    }

    [Fact]
    public void ADescriptorPastTheEndOfTheBlockIsRefused()
    {
        Result<ChunkDescriptor> result = ChunkDescriptor.Parse(new byte[40], 8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ATruncatedTableIsRefusedRatherThanReadShort()
    {
        // The count says four chunks; the block holds three and a half.
        byte[] block = new UdifBuilder.Mish { SectorCount = 40, DeclaredChunkCount = 4 }
            .With(
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 10, 10),
                new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 20, 10))
            .ToArray();

        byte[] truncated = [.. block, .. new byte[20]];

        Result<MishBlock> result = MishBlock.Parse(truncated, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void EveryTruncationOfARealChunkTableFailsCleanly()
    {
        byte[] payload = RealImageSamples.MishPayloads()[1];

        for (int keep = MishBlock.ChunkTableOffset; keep < payload.Length; keep++)
        {
            Result<MishBlock> result = MishBlock.Parse(payload.AsSpan(0, keep), "region");
            Assert.False(result.Ok, $"A {keep}-byte block was accepted.");
        }
    }

    private static MishBlock Parsed(ReadOnlySpan<byte> block)
    {
        Result<MishBlock> result = MishBlock.Parse(block, "region");
        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        return result.Value!;
    }
}
