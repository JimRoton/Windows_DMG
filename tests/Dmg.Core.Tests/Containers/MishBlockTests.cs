using System.Buffers.Binary;
using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The mish header, and the offset that docs/03 warned was the one most likely to
/// be wrong.
/// </summary>
public sealed class MishBlockTests
{
    [Fact]
    public void TheChunkCountIsAt0xC8AndTheTableAt0xCC()
    {
        // This is the settling test for the layout question. Reading the count from
        // 0xCC would give 0x80000005 - the first chunk's zlib entry type - and the
        // block sizes would not divide.
        foreach (byte[] payload in RealImageSamples.MishPayloads())
        {
            uint atC8 = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0xC8));
            uint atCc = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0xCC));

            Assert.Equal(0xCC + (int)(atC8 * 40), payload.Length);
            Assert.Equal(0x80000005u, atCc);
        }
    }

    [Fact]
    public void ParsesBothRegionsOfAnHdiutilImage()
    {
        IReadOnlyList<byte[]> payloads = RealImageSamples.MishPayloads();

        MishBlock mbr = Parsed(payloads[0]);
        Assert.Equal(1u, mbr.Version);
        Assert.Equal(0ul, mbr.FirstSectorNumber);
        Assert.Equal(1ul, mbr.SectorCount);
        Assert.Equal(0ul, mbr.DataOffset);
        Assert.Equal(2u, mbr.ChunkCount);

        MishBlock fat = Parsed(payloads[1]);
        Assert.Equal(1ul, fat.FirstSectorNumber);
        Assert.Equal(67646ul, fat.SectorCount);
        Assert.Equal(4u, fat.ChunkCount);

        // The two regions between them cover the koly's 67,647 sectors.
        Assert.True(fat.EndSectorExclusive().TryGetValue(out ulong end));
        Assert.Equal(67647ul, end);
    }

    [Fact]
    public void TheHeaderIs204BytesAndThatIsWhereTheTableStarts()
    {
        Assert.Equal(204, MishBlock.ChunkTableOffset);
        Assert.Equal(40, MishBlock.ChunkDescriptorSize);
    }

    [Fact]
    public void ParsesASynthesisedBlock()
    {
        byte[] block = new UdifBuilder.Mish { FirstSectorNumber = 1000, SectorCount = 40 }
            .With(new UdifBuilder.Chunk(UdifBuilder.Chunk.Raw, 0, 40), UdifBuilder.Chunk.End(40))
            .ToArray();

        MishBlock mish = Parsed(block);

        Assert.Equal(1000ul, mish.FirstSectorNumber);
        Assert.Equal(40ul, mish.SectorCount);
        Assert.Equal(2u, mish.ChunkCount);
        Assert.True(mish.LengthInBytes().TryGetValue(out ulong bytes));
        Assert.Equal(40ul * 512, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(0x10)]
    [InlineData(0x18)]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0xC8)]
    [InlineData(0xCB)]
    public void ABlockTruncatedBeforeTheChunkTableIsRejected(int keep)
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 1 }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block.AsSpan(0, keep), "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("truncated", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderWithNoChunksAtAllIsStructurallyValid()
    {
        // Empty is not corrupt here; whether the regions cover the disk is a
        // question for the extent index, which can see all of them at once.
        byte[] block = new UdifBuilder.Mish { SectorCount = 0 }.ToArray();

        Assert.Equal(0xCC, block.Length);
        Assert.Equal(0u, Parsed(block).ChunkCount);
    }

    [Fact]
    public void APayloadThatIsNotAMishBlockIsRejected()
    {
        byte[] block = new UdifBuilder.Mish { Signature = 0x6B6F6C79 }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region 3");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("region 3", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("koly", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownBlockVersionIsUnsupported()
    {
        byte[] block = new UdifBuilder.Mish { Version = 2 }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(100u)]
    [InlineData(uint.MaxValue)]
    public void AChunkCountLargerThanTheBlockIsRefusedBeforeAllocation(uint declared)
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 1, DeclaredChunkCount = declared }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("more chunks than it contains", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChunkCountSmallerThanTheBlockIsAccepted()
    {
        // Trailing bytes after the table are not this parser's business.
        byte[] block = new UdifBuilder.Mish { SectorCount = 1, TrailingBytes = 400 }
            .With(UdifBuilder.Chunk.End(1))
            .ToArray();

        Assert.Equal(1u, Parsed(block).ChunkCount);
    }

    [Fact]
    public void ASectorCountThatOverflowsTheByteLengthIsCorruption()
    {
        byte[] block = new UdifBuilder.Mish { SectorCount = 1ul << 55 }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ARegionThatEndsPastTheEndOfTheUniverseIsCorruption()
    {
        byte[] block = new UdifBuilder.Mish
        {
            FirstSectorNumber = ulong.MaxValue - 1,
            SectorCount = 100,
        }.ToArray();

        Result<MishBlock> result = MishBlock.Parse(block, "region");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void EveryTruncationOfARealBlockFailsCleanly()
    {
        byte[] payload = RealImageSamples.MishPayloads()[1];

        for (int keep = 0; keep < payload.Length; keep++)
        {
            Result<MishBlock> result = MishBlock.Parse(payload.AsSpan(0, keep), "region");

            if (keep < MishBlock.ChunkTableOffset)
            {
                Assert.False(result.Ok, $"A {keep}-byte block was accepted.");
            }
            else
            {
                // Past the header, only a block long enough for its declared table
                // is acceptable.
                Assert.Equal(keep >= payload.Length, result.Ok);
            }
        }
    }

    private static MishBlock Parsed(ReadOnlySpan<byte> block)
    {
        Result<MishBlock> result = MishBlock.Parse(block, "region");
        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        return result.Value!;
    }
}
