using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// The registry's two promises: it can describe an image's codecs without decoding
/// anything, and when it does decode it bounds the output itself rather than
/// trusting the codec.
/// </summary>
public sealed class ChunkDecoderRegistryTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    [Fact]
    public void ARegisteredDecoderIsReportedAsSupported()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Zlib, "zlib");
        ChunkDecoderRegistry registry = new([decoder]);

        Assert.True(registry.IsSupported(ChunkEntryTypeCodes.Zlib));
        Assert.True(registry.TryGetDecoder(ChunkEntryTypeCodes.Zlib, out IChunkDecoder? found));
        Assert.Same(decoder, found);
        Assert.Equal([ChunkEntryTypeCodes.Zlib], registry.SupportedEntryTypes);
    }

    [Fact]
    public void AnUnregisteredDecoderIsReportedAsUnsupported()
    {
        ChunkDecoderRegistry registry = new([]);

        Assert.False(registry.IsSupported(ChunkEntryTypeCodes.Bzip2));
        Assert.False(registry.TryGetDecoder(ChunkEntryTypeCodes.Bzip2, out IChunkDecoder? found));
        Assert.Null(found);
    }

    [Fact]
    public void DescribingAnImagesCodecsNeverTouchesADecoder()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Zlib, "zlib");
        ChunkDecoderRegistry registry = new([decoder]);

        IReadOnlyList<ChunkCodecInfo> survey = registry.Survey(
        [
            ChunkEntryTypeCodes.Zlib,
            ChunkEntryTypeCodes.Bzip2,
            ChunkEntryTypeCodes.Zlib,
            ChunkEntryTypeCodes.Terminator,
        ]);

        // Three distinct types, ascending, each described once.
        Assert.Equal(3, survey.Count);
        Assert.Equal(
            [ChunkEntryTypeCodes.Zlib, ChunkEntryTypeCodes.Bzip2, ChunkEntryTypeCodes.Terminator],
            survey.Select(info => info.EntryType));

        Assert.True(survey[0].IsSupported);
        Assert.False(survey[1].IsSupported);
        Assert.True(survey[1].IsUnsupportedPayload);
        Assert.True(survey[2].IsStructural);
        Assert.False(survey[2].IsUnsupportedPayload);

        // The whole point: nothing was decoded to work that out.
        Assert.Equal(0, decoder.CallCount);
    }

    [Fact]
    public void AnEntryTypeNobodyHasEverSeenIsDescribedRatherThanRejected()
    {
        ChunkDecoderRegistry registry = new([]);

        ChunkCodecInfo info = registry.Describe(0x0000_002A);

        Assert.False(info.IsSupported);
        Assert.False(info.IsStructural);
        Assert.Contains("0x0000002A", info.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void SurveyingAnEmptyChunkTableProducesAnEmptyInventory()
    {
        ChunkDecoderRegistry registry = new([]);

        Assert.Empty(registry.Survey([]));
    }

    [Fact]
    public void DecodingAnUnsupportedTypeFailsWithUnsupportedFormatNamingTheCodec()
    {
        ChunkDecoderRegistry registry = new([]);
        byte[] destination = new byte[SectorSize];

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Bzip2, [], destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains("bzip2", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodingAStructuralEntryIsCorruptionNotAnUnsupportedCodec()
    {
        ChunkDecoderRegistry registry = new([]);
        byte[] destination = new byte[SectorSize];

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Terminator, [], destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void TheDecoderOnlyEverSeesTheSectorsTheChunkDeclared()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Raw, "raw");
        ChunkDecoderRegistry registry = new([decoder]);
        byte[] destination = new byte[8 * SectorSize];

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, new byte[3], destination, 2);

        Assert.True(result.Ok);
        Assert.Equal(2 * SectorSize, decoder.LastDestinationLength);
        Assert.Equal(3, decoder.LastSourceLength);

        // Nothing was written past the declared length.
        Assert.All(destination[(2 * SectorSize)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ADecoderThatUnderFillsTheChunkIsCorruption()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Raw, "raw") { FillCount = 10 };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("raw", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecoderThatClaimsMoreThanTheChunkDeclaredIsCorruption()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Raw, "raw") { ClaimedWritten = SectorSize + 1 };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ADecodersOwnFailureIsPassedStraightBack()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Raw, "raw")
        {
            Failure = DmgError.Corrupt("Deliberate."),
        };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal("Deliberate.", result.Error.Message);
    }

    [Fact]
    public void ANegativeSectorCountIsCorruptionRatherThanAnException()
    {
        ChunkDecoderRegistry registry = new([new FakeChunkDecoder(ChunkEntryTypeCodes.Raw)]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], -1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ABufferTooSmallForTheChunkIsOurBugNotTheImages()
    {
        ChunkDecoderRegistry registry = new([new FakeChunkDecoder(ChunkEntryTypeCodes.Raw)]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], 2);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void AZeroSectorChunkDecodesToNothing()
    {
        FakeChunkDecoder decoder = new(ChunkEntryTypeCodes.Raw);
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryTypeCodes.Raw, [], new byte[SectorSize], 0);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
        Assert.Equal(0, decoder.LastDestinationLength);
    }

    [Fact]
    public void TwoDecodersClaimingOneEntryTypeIsAWiringBug()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new ChunkDecoderRegistry(
            [
                new FakeChunkDecoder(ChunkEntryTypeCodes.Zlib, "one"),
                new FakeChunkDecoder(ChunkEntryTypeCodes.Zlib, "two"),
            ]));

        Assert.Contains("0x80000005", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecoderClaimingAStructuralEntryIsAWiringBug() =>
        Assert.Throws<ArgumentException>(() =>
            new ChunkDecoderRegistry([new FakeChunkDecoder(ChunkEntryTypeCodes.Terminator)]));

    [Fact]
    public void TheDefaultRegistryIsShared() =>
        Assert.Same(ChunkDecoderRegistry.Default, ChunkDecoderRegistry.Default);
}

/// <summary>The naming table, which has to answer for values nobody has seen.</summary>
public sealed class ChunkEntryTypeCodesTests
{
    [Theory]
    [InlineData(ChunkEntryTypeCodes.ZeroFill, "zero-fill")]
    [InlineData(ChunkEntryTypeCodes.Raw, "raw")]
    [InlineData(ChunkEntryTypeCodes.Ignore, "ignore")]
    [InlineData(ChunkEntryTypeCodes.AppleAdc, "ADC")]
    [InlineData(ChunkEntryTypeCodes.Zlib, "zlib")]
    [InlineData(ChunkEntryTypeCodes.Bzip2, "bzip2")]
    [InlineData(ChunkEntryTypeCodes.Lzfse, "LZFSE")]
    [InlineData(ChunkEntryTypeCodes.Lzma, "LZMA")]
    [InlineData(ChunkEntryTypeCodes.Comment, "comment")]
    [InlineData(ChunkEntryTypeCodes.Terminator, "terminator")]
    public void KnownEntryTypesHaveNames(uint entryType, string expected) =>
        Assert.Equal(expected, ChunkEntryTypeCodes.NameOf(entryType));

    [Fact]
    public void AnUnknownEntryTypeIsNamedByItsValue() =>
        Assert.Equal("unknown (0xDEADBEEF)", ChunkEntryTypeCodes.NameOf(0xDEAD_BEEF));

    [Theory]
    [InlineData(ChunkEntryTypeCodes.Comment)]
    [InlineData(ChunkEntryTypeCodes.Terminator)]
    public void CommentAndTerminatorAreStructural(uint entryType) =>
        Assert.True(ChunkEntryTypeCodes.IsStructural(entryType));

    [Theory]
    [InlineData(ChunkEntryTypeCodes.ZeroFill)]
    [InlineData(ChunkEntryTypeCodes.Zlib)]
    [InlineData(0xDEAD_BEEFu)]
    public void EverythingElseIsAPayload(uint entryType) =>
        Assert.False(ChunkEntryTypeCodes.IsStructural(entryType));
}
