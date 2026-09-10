using System.IO.Compression;
using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// The bomb guard. A chunk gets exactly the bytes it declared, whatever its payload
/// would like to become, and a chunk that wants more is refused rather than trimmed.
/// The tests are deliberately at the registry level: the rule is enforced once, for
/// every codec, and that is the property worth pinning.
/// </summary>
public sealed class DecompressionBombTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    /// <summary>A zlib payload that inflates to <paramref name="plainLength"/> zeros.</summary>
    private static byte[] ZlibBomb(int plainLength)
    {
        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(new byte[plainLength]);
        }

        return output.ToArray();
    }

    [Fact]
    public void AZlibChunkThatInflatesToThousandsOfTimesItsDeclaredLengthIsRefused()
    {
        // 8 MiB of payload behind a chunk that claims a single 512-byte sector:
        // sixteen thousand times what it is allowed to produce, from 8 KiB of input.
        byte[] bomb = ZlibBomb(8 * 1024 * 1024);
        byte[] destination = new byte[SectorSize];

        Assert.True(bomb.Length < 16 * 1024);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Zlib, bomb, destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("zlib", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedBombDoesNotLeaveASilentlyTruncatedChunkBehind()
    {
        // The failure mode this guards: stopping at the cap, returning success, and
        // handing the caller 512 bytes of an 8 MiB chunk as though that were the
        // image. The result must be a failure, so no caller can mistake the buffer
        // for a decoded chunk.
        byte[] bomb = ZlibBomb(4 * 1024 * 1024);
        byte[] destination = new byte[4 * SectorSize];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Zlib, bomb, destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(0, result.GetValueOrDefault());

        // And nothing was written outside the sector the chunk declared.
        Assert.All(destination[SectorSize..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnAdcChunkThatExpandsPastItsDeclaredLengthIsRefused()
    {
        // One literal byte, then repeated 67-byte matches at offset 1: 130 bytes of
        // input that would run for kilobytes.
        List<byte> tokens = [0x80, 0xAB];

        for (int i = 0; i < 64; i++)
        {
            tokens.AddRange([0x7F, 0x00, 0x00]);
        }

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.AppleAdc, [.. tokens], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("ADC", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARawChunkCarryingMoreThanItDeclaredIsRefused()
    {
        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, new byte[64 * SectorSize], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void TheCapIsTheDeclaredLengthNotTheBufferTheCallerHappenedToOffer()
    {
        // A generous buffer must not become a licence to decode into it. The chunk
        // declares one sector, the buffer holds sixteen, the payload wants four.
        byte[] plain = new byte[4 * SectorSize];
        Array.Fill(plain, (byte)0x5A);

        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(plain);
        }

        byte[] destination = new byte[16 * SectorSize];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Zlib, output.ToArray(), destination, 1);

        Assert.False(result.Ok);
        Assert.All(destination[SectorSize..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, SectorSize)]
    [InlineData(2048, 2048 * SectorSize)]
    public void APlausibleSectorCountConvertsToBytes(long sectors, int expected)
    {
        Result<int> length = ChunkDecoderRegistry.DecodedLength(sectors);

        Assert.True(length.Ok);
        Assert.Equal(expected, length.Value);
    }

    [Fact]
    public void TheCeilingIsAcceptedAndOneSectorPastItIsNot()
    {
        long atCeiling = ChunkDecoderRegistry.MaxDecodedChunkBytes / SectorSize;

        Assert.True(ChunkDecoderRegistry.DecodedLength(atCeiling).Ok);

        Result<int> past = ChunkDecoderRegistry.DecodedLength(atCeiling + 1);

        Assert.False(past.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, past.Error.Code);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 512)]
    [InlineData(1L << 40)]
    public void AnImpossibleSectorCountIsRefusedWithoutAllocatingOrThrowing(long sectors)
    {
        Result<int> length = ChunkDecoderRegistry.DecodedLength(sectors);

        Assert.False(length.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, length.Error.Code);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 40)]
    [InlineData(-1L)]
    public void DecodingWithAnImpossibleSectorCountFailsBeforeTheCodecRuns(long sectors)
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw, "raw");
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], sectors);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Equal(0, decoder.CallCount);
    }

    [Fact]
    public void AChunkAtTheCeilingStillDecodesNormally()
    {
        // The guard rejects the implausible, not the merely large.
        int sectors = 64;
        byte[] plain = new byte[sectors * SectorSize];
        Array.Fill(plain, (byte)0x7E);

        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(plain);
        }

        byte[] destination = new byte[plain.Length];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Zlib, output.ToArray(), destination, sectors);

        Assert.True(result.Ok);
        Assert.Equal(plain, destination);
    }
}
