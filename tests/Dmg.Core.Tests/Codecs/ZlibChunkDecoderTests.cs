using System.IO.Compression;
using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// zlib chunks. The inflate is the BCL's; what is tested here is the framing UDIF
/// puts around it - a chunk must inflate to exactly the sectors it declared - and
/// that no shape of hostile input escapes as an exception.
/// </summary>
public sealed class ZlibChunkDecoderTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    private static byte[] Deflate(byte[] plain)
    {
        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(plain);
        }

        return output.ToArray();
    }

    private static byte[] Pattern(int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)((i * 17) ^ (i >> 3)))];

    [Fact]
    public void ItClaimsEntryType80000005()
    {
        Assert.Equal(ChunkEntryTypeCodes.Zlib, ZlibChunkDecoder.Instance.EntryType);
        Assert.Equal("zlib", ZlibChunkDecoder.Instance.Name);
        Assert.True(ZlibChunkDecoder.Instance.ReadsDataFork);
    }

    [Fact]
    public void ACompressedChunkRoundTrips()
    {
        byte[] plain = Pattern(4 * SectorSize);
        byte[] destination = new byte[plain.Length];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, Deflate(plain), destination, 4);

        Assert.True(result.Ok);
        Assert.Equal(plain.Length, result.Value);
        Assert.Equal(plain, destination);
    }

    [Fact]
    public void AHighlyCompressibleChunkRoundTrips()
    {
        // The realistic shape: a sector run of zeros that zlib squeezes to nothing.
        byte[] plain = new byte[64 * SectorSize];
        byte[] compressed = Deflate(plain);
        byte[] destination = new byte[plain.Length];
        Array.Fill(destination, (byte)0xEE);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, compressed, destination, 64);

        Assert.True(result.Ok);
        Assert.All(destination, b => Assert.Equal(0, b));
        Assert.True(compressed.Length < plain.Length / 10);
    }

    [Fact]
    public void AChunkThatInflatesShortIsCorruption()
    {
        byte[] compressed = Deflate(Pattern(SectorSize - 32));

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, compressed, new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("zlib", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChunkThatInflatesLongIsCorruptionRatherThanATruncatedRead()
    {
        // Declares one sector, carries two. The extra sector must not be silently
        // dropped: what came back would not be the chunk the image described.
        byte[] compressed = Deflate(Pattern(2 * SectorSize));

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, compressed, new byte[2 * SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ATruncatedStreamIsCorruptionNotAnException()
    {
        byte[] compressed = Deflate(Pattern(4 * SectorSize));
        byte[] truncated = compressed[..(compressed.Length / 2)];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, truncated, new byte[4 * SectorSize], 4);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AFlippedByteInTheMiddleOfTheStreamIsCorruption()
    {
        byte[] compressed = Deflate(Pattern(8 * SectorSize));
        compressed[compressed.Length / 2] ^= 0xFF;

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, compressed, new byte[8 * SectorSize], 8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void RandomBytesWithNoZlibHeaderAreCorruption()
    {
        byte[] garbage = [.. Enumerable.Range(0, 512).Select(i => (byte)(i * 37))];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, garbage, new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ARawDeflateStreamWithoutTheZlibWrapperIsRejected()
    {
        using MemoryStream output = new();

        using (DeflateStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(Pattern(SectorSize));
        }

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, output.ToArray(), new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnEmptyPayloadForANonEmptyChunkIsCorruption()
    {
        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AZeroSectorChunkMustAlsoBeEmptyOnceInflated()
    {
        byte[] empty = Deflate([]);

        Result<int> ok = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, empty, new byte[SectorSize], 0);

        Assert.True(ok.Ok);
        Assert.Equal(0, ok.Value);

        Result<int> bad = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, Deflate(Pattern(16)), new byte[SectorSize], 0);

        Assert.False(bad.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, bad.Error.Code);
    }

    [Fact]
    public void EveryTruncationOfAValidStreamFailsCleanly()
    {
        // Sweep the whole stream rather than one arbitrary cut point: no prefix of a
        // valid chunk may throw or hang, and any prefix that does decode must have
        // produced the real sectors.

        byte[] plain = Pattern(2 * SectorSize);
        byte[] compressed = Deflate(plain);

        for (int length = 0; length < compressed.Length; length++)
        {
            byte[] destination = new byte[plain.Length];

            Result<int> result = ZlibChunkDecoder.Instance.Decode(
                compressed.AsSpan(0, length), destination);

            if (result.Ok)
            {
                // The only prefixes that can succeed are the ones that cut into the
                // four-byte Adler-32 trailer: the deflate data is all there, so the
                // sectors are the right sectors. See the note on ZlibChunkDecoder.
                Assert.True(length >= compressed.Length - 4, $"prefix of {length} bytes decoded");
                Assert.Equal(plain, destination);
            }
            else
            {
                Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
            }
        }
    }

    [Fact]
    public void OnlyTheDeclaredSectorsAreWritten()
    {
        byte[] plain = Pattern(SectorSize);
        byte[] destination = new byte[3 * SectorSize];
        Array.Fill(destination, (byte)0x42);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.Zlib, Deflate(plain), destination, 1);

        Assert.True(result.Ok);
        Assert.Equal(plain, destination[..SectorSize]);
        Assert.All(destination[SectorSize..], b => Assert.Equal(0x42, b));
    }
}
