using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// Apple ADC. Half of these are hand-built token streams that pin one rule each;
/// the other half are two chunks lifted verbatim out of a UDCO image
/// <c>hdiutil</c> produced, with the sectors they must expand to taken from the
/// same image converted to raw. Those two are what settled the encoding of the long
/// match token, which the format note had wrong.
/// </summary>
public sealed class AdcChunkDecoderTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    /// <summary>
    /// A real 43-byte ADC chunk: literals, long matches, and short matches, with the
    /// long matches at offset 1 - overlapping runs the decoder is still writing.
    /// </summary>
    private const string AppleChunkCompressedA =
        "80007F00007F00007F00007F00007F00007F000068001683FEFFFFEE00038001" +
        "004A8102526E004F8155AA";

    private const string AppleChunkPlainA =
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000FE" +
        "FFFFEEFEFFFF0100000002520000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000055AA";

    /// <summary>A real 92-byte ADC chunk: the image's GPT header sector.</summary>
    private const string AppleChunkCompressedB =
        "934546492050415254000001005C000000FF180B2D0006000D0C008102520C08" +
        "8022100F81E1510C1890F3512F5D507B3D4CAE22B4ED621838A002102F808010" +
        "0383EBDA734310427F00007F00007F00007F00007F00007F00002000";

    private const string AppleChunkPlainB =
        "4546492050415254000001005C000000FF180B2D000000000100000000000000" +
        "02520000000000002200000000000000E151000000000000F3512F5D507B3D4C" +
        "AE22B4ED621838A002000000000000008000000080000000EBDA734300000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static TheoryData<string, string> AppleProducedChunks => new()
    {
        { AppleChunkCompressedA, AppleChunkPlainA },
        { AppleChunkCompressedB, AppleChunkPlainB },
    };

    /// <summary>A literal-run token and the bytes it carries.</summary>
    private static byte[] Literal(params byte[] bytes) =>
        [(byte)(0x80 | (bytes.Length - 1)), .. bytes];

    /// <summary>A long-match token: 4 to 67 bytes, offset 1 to 65536.</summary>
    private static byte[] LongMatch(int length, int offset)
    {
        int encodedOffset = offset - 1;
        return [(byte)(0x40 | (length - 4)), (byte)(encodedOffset >> 8), (byte)encodedOffset];
    }

    /// <summary>A short-match token: 3 to 18 bytes, offset 1 to 1024.</summary>
    private static byte[] ShortMatch(int length, int offset)
    {
        int encodedOffset = offset - 1;
        return [(byte)(((length - 3) << 2) | (encodedOffset >> 8)), (byte)encodedOffset];
    }

    private static Result<int> Decode(byte[] source, byte[] destination) =>
        AdcChunkDecoder.Instance.Decode(source, destination);

    [Fact]
    public void ItClaimsEntryType80000004()
    {
        Assert.Equal(ChunkEntryTypeCodes.AppleAdc, AdcChunkDecoder.Instance.EntryType);
        Assert.Equal("ADC", AdcChunkDecoder.Instance.Name);
        Assert.True(AdcChunkDecoder.Instance.ReadsDataFork);
    }

    [Theory]
    [MemberData(nameof(AppleProducedChunks))]
    public void AChunkFromAnHdiutilProducedImageExpandsToItsSector(string compressed, string plain)
    {
        byte[] source = Convert.FromHexString(compressed);
        byte[] expected = Convert.FromHexString(plain);
        byte[] destination = new byte[SectorSize];

        Assert.Equal(SectorSize, expected.Length);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.AppleAdc, source, destination, 1);

        Assert.True(result.Ok, result.Ok ? null : result.Error.ToString());
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void ALiteralRunIsCopiedVerbatim()
    {
        byte[] source = Literal(1, 2, 3, 4);
        byte[] destination = new byte[4];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Value);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination);
    }

    [Fact]
    public void ALiteralRunCanCarryItsMaximumOf128Bytes()
    {
        byte[] payload = [.. Enumerable.Range(0, 128).Select(i => (byte)i)];
        byte[] destination = new byte[128];

        Result<int> result = Decode(Literal(payload), destination);

        Assert.True(result.Ok);
        Assert.Equal(payload, destination);
    }

    [Fact]
    public void AShortMatchCopiesFromTheOutputAlreadyProduced()
    {
        // "abcd" then a 3-byte match 4 back: abcd abc
        byte[] source = [.. Literal((byte)'a', (byte)'b', (byte)'c', (byte)'d'), .. ShortMatch(3, 4)];
        byte[] destination = new byte[7];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.Equal("abcdabc"u8.ToArray(), destination);
    }

    [Fact]
    public void AShortMatchReachesItsFullLengthAndOffset()
    {
        byte[] prefix = [.. Enumerable.Range(0, 1024).Select(i => (byte)i)];
        byte[] source = [.. BuildLiterals(prefix), .. ShortMatch(18, 1024)];
        byte[] destination = new byte[prefix.Length + 18];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.Equal(prefix, destination[..prefix.Length]);
        Assert.Equal(prefix[..18], destination[prefix.Length..]);
    }

    [Fact]
    public void ALongMatchIsAThreeByteTokenWithASixteenBitOffset()
    {
        // The encoding the format note originally got wrong. Reaching 300 bytes back
        // is impossible with a 10-bit offset shared with the short match.
        byte[] prefix = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];
        byte[] source = [.. BuildLiterals(prefix), .. LongMatch(67, 300)];
        byte[] destination = new byte[prefix.Length + 67];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.Equal(prefix[..67], destination[prefix.Length..]);
    }

    [Fact]
    public void AnOverlappingMatchRepeatsTheBytesItIsStillWriting()
    {
        // The reason the copy is a byte-at-a-time loop: offset 1, length 67, reading
        // output this very token is producing.
        byte[] source = [.. Literal(0xAB), .. LongMatch(67, 1)];
        byte[] destination = new byte[68];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.All(destination, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void AnOverlappingMatchWithATwoBytePeriodAlternates()
    {
        // A block move would produce "ab" then whatever was in the buffer; the
        // byte-at-a-time loop produces ababab...
        byte[] source = [.. Literal((byte)'a', (byte)'b'), .. ShortMatch(8, 2)];
        byte[] destination = new byte[10];

        Result<int> result = Decode(source, destination);

        Assert.True(result.Ok);
        Assert.Equal("ababababab"u8.ToArray(), destination);
    }

    [Fact]
    public void AMatchReachingBeforeTheStartOfTheChunkIsCorruption()
    {
        byte[] source = [.. Literal(1, 2), .. ShortMatch(3, 3)];

        Result<int> result = Decode(source, new byte[5]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("ADC", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchAsTheVeryFirstTokenIsCorruption()
    {
        // Nothing has been produced yet, so any back-reference is out of bounds.
        Result<int> result = Decode([.. ShortMatch(3, 1)], new byte[3]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ALiteralRunThatRunsPastTheEndOfTheChunkIsTruncation()
    {
        // Claims 16 bytes follow, provides 2.
        byte[] source = [0x8F, 1, 2];

        Result<int> result = Decode(source, new byte[16]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AMatchTokenMissingItsOffsetBytesIsTruncation()
    {
        Assert.False(Decode([0x40, 0x00], new byte[64]).Ok);
        Assert.False(Decode([0x00], new byte[64]).Ok);
    }

    [Fact]
    public void AnEmptyChunkThatOwesSectorsIsTruncation()
    {
        Result<int> result = Decode([], new byte[SectorSize]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ATokenStreamThatStopsShortIsTruncation()
    {
        byte[] source = Literal(1, 2, 3);

        Result<int> result = Decode(source, new byte[SectorSize]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ALiteralRunOverflowingTheDeclaredLengthIsCorruption()
    {
        byte[] source = Literal(1, 2, 3, 4, 5, 6, 7, 8);

        Result<int> result = Decode(source, new byte[4]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AMatchOverflowingTheDeclaredLengthIsCorruption()
    {
        byte[] source = [.. Literal(0xAB), .. LongMatch(67, 1)];

        Result<int> result = Decode(source, new byte[10]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    /// <summary>
    /// The overflow guard's other edge: a match that lands exactly on the last byte
    /// of the declared length must succeed. This is the case a boundary mutation of
    /// the guard - <c>&gt;</c> loosened to <c>&gt;=</c>, say - would not be caught
    /// by: the test above overflows by 57 bytes, so both the real check and an
    /// off-by-one version of it reject it for the same reason. Only a match sized
    /// to land exactly at the end distinguishes "fits" from "one byte too many".
    /// </summary>
    [Fact]
    public void AMatchThatExactlyFillsTheRemainingLengthSucceeds()
    {
        // 1 literal byte + a 7-byte match reaching back to it = 8, the whole
        // destination, with nothing left over.
        byte[] source = [.. Literal(0xAB), .. LongMatch(7, 1)];

        Result<int> result = Decode(source, new byte[8]);

        Assert.True(result.TryGetValue(out int written));
        Assert.Equal(8, written);
    }

    [Fact]
    public void TokensLeftOverAfterTheSectorsAreFilledAreCorruption()
    {
        // Everything the chunk promised has been produced and there is still input.
        byte[] source = [.. Literal(1, 2, 3, 4), .. Literal(5, 6, 7, 8)];

        Result<int> result = Decode(source, new byte[4]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AZeroLengthChunkNeedsNoTokens()
    {
        Result<int> result = Decode([], []);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void EveryTruncationOfARealChunkFailsWithoutThrowing()
    {
        byte[] source = Convert.FromHexString(AppleChunkCompressedB);

        for (int length = 0; length < source.Length; length++)
        {
            Result<int> result = AdcChunkDecoder.Instance.Decode(
                source.AsSpan(0, length), new byte[SectorSize]);

            Assert.False(result.Ok);
            Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        }
    }

    [Fact]
    public void EveryCorruptionOfARealChunkIsEitherRejectedOrContained()
    {
        // Flip each byte in turn. Whatever the tokens turn into, the decoder either
        // refuses the chunk or fills exactly the declared sector - never more, never
        // an exception.
        byte[] original = Convert.FromHexString(AppleChunkCompressedB);

        for (int i = 0; i < original.Length; i++)
        {
            foreach (byte mask in (byte[])[0x01, 0x40, 0x80, 0xFF])
            {
                byte[] mutated = [.. original];
                mutated[i] ^= mask;

                byte[] destination = new byte[SectorSize];
                Result<int> result = Decode(mutated, destination);

                if (result.Ok)
                {
                    Assert.Equal(SectorSize, result.Value);
                }
                else
                {
                    Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
                }
            }
        }
    }

    [Fact]
    public void RandomBytesNeverThrowAndNeverOverfill()
    {
        Random random = new(20260909);

        for (int trial = 0; trial < 500; trial++)
        {
            byte[] source = new byte[random.Next(0, 300)];
            random.NextBytes(source);

            byte[] destination = new byte[SectorSize];
            Result<int> result = Decode(source, destination);

            if (result.Ok)
            {
                Assert.Equal(SectorSize, result.Value);
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
        byte[] source = Convert.FromHexString(AppleChunkCompressedA);
        byte[] destination = new byte[3 * SectorSize];
        Array.Fill(destination, (byte)0x33);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryTypeCodes.AppleAdc, source, destination, 1);

        Assert.True(result.Ok);
        Assert.All(destination[SectorSize..], b => Assert.Equal(0x33, b));
    }

    /// <summary>Splits a payload into as many 128-byte literal runs as it needs.</summary>
    private static byte[] BuildLiterals(byte[] payload)
    {
        List<byte> tokens = [];

        for (int offset = 0; offset < payload.Length; offset += 128)
        {
            int run = Math.Min(128, payload.Length - offset);
            tokens.Add((byte)(0x80 | (run - 1)));
            tokens.AddRange(payload.AsSpan(offset, run));
        }

        return [.. tokens];
    }
}
