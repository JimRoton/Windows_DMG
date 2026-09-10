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
        FakeChunkDecoder decoder = new(ChunkEntryType.Zlib, "zlib");
        ChunkDecoderRegistry registry = new([decoder]);

        Assert.True(registry.IsSupported(ChunkEntryType.Zlib));
        Assert.True(registry.TryGetDecoder(ChunkEntryType.Zlib, out IChunkDecoder? found));
        Assert.Same(decoder, found);
        Assert.Equal([ChunkEntryType.Zlib], registry.SupportedEntryTypes);
    }

    [Fact]
    public void AnUnregisteredDecoderIsReportedAsUnsupported()
    {
        ChunkDecoderRegistry registry = new([]);

        Assert.False(registry.IsSupported(ChunkEntryType.Bzip2));
        Assert.False(registry.TryGetDecoder(ChunkEntryType.Bzip2, out IChunkDecoder? found));
        Assert.Null(found);
    }

    [Fact]
    public void DescribingAnImagesCodecsNeverTouchesADecoder()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Zlib, "zlib");
        ChunkDecoderRegistry registry = new([decoder]);

        IReadOnlyList<ChunkCodecInfo> survey = registry.Survey(
        [
            ChunkEntryType.Zlib,
            ChunkEntryType.Bzip2,
            ChunkEntryType.Zlib,
            ChunkEntryType.Terminator,
        ]);

        // Three distinct types, ascending, each described once.
        Assert.Equal(3, survey.Count);
        Assert.Equal(
            [ChunkEntryType.Zlib, ChunkEntryType.Bzip2, ChunkEntryType.Terminator],
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

        Result<int> result = registry.Decode(ChunkEntryType.Bzip2, [], destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains("bzip2", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodingAStructuralEntryIsCorruptionNotAnUnsupportedCodec()
    {
        ChunkDecoderRegistry registry = new([]);
        byte[] destination = new byte[SectorSize];

        Result<int> result = registry.Decode(ChunkEntryType.Terminator, [], destination, 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void TheDecoderOnlyEverSeesTheSectorsTheChunkDeclared()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw, "raw");
        ChunkDecoderRegistry registry = new([decoder]);
        byte[] destination = new byte[8 * SectorSize];

        Result<int> result = registry.Decode(ChunkEntryType.Raw, new byte[3], destination, 2);

        Assert.True(result.Ok);
        Assert.Equal(2 * SectorSize, decoder.LastDestinationLength);
        Assert.Equal(3, decoder.LastSourceLength);

        // Nothing was written past the declared length.
        Assert.All(destination[(2 * SectorSize)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ADecoderThatUnderFillsTheChunkIsCorruption()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw, "raw") { FillCount = 10 };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("raw", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecoderThatClaimsMoreThanTheChunkDeclaredIsCorruption()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw, "raw") { ClaimedWritten = SectorSize + 1 };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ADecodersOwnFailureIsPassedStraightBack()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw, "raw")
        {
            Failure = DmgError.Corrupt("Deliberate."),
        };
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal("Deliberate.", result.Error.Message);
    }

    [Fact]
    public void ANegativeSectorCountIsCorruptionRatherThanAnException()
    {
        ChunkDecoderRegistry registry = new([new FakeChunkDecoder(ChunkEntryType.Raw)]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], -1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ABufferTooSmallForTheChunkIsOurBugNotTheImages()
    {
        ChunkDecoderRegistry registry = new([new FakeChunkDecoder(ChunkEntryType.Raw)]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], 2);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void AZeroSectorChunkDecodesToNothing()
    {
        FakeChunkDecoder decoder = new(ChunkEntryType.Raw);
        ChunkDecoderRegistry registry = new([decoder]);

        Result<int> result = registry.Decode(ChunkEntryType.Raw, [], new byte[SectorSize], 0);

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
                new FakeChunkDecoder(ChunkEntryType.Zlib, "one"),
                new FakeChunkDecoder(ChunkEntryType.Zlib, "two"),
            ]));

        Assert.Contains("0x80000005", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecoderClaimingAStructuralEntryIsAWiringBug() =>
        Assert.Throws<ArgumentException>(() =>
            new ChunkDecoderRegistry([new FakeChunkDecoder(ChunkEntryType.Terminator)]));

    [Fact]
    public void TheDefaultRegistryIsShared() =>
        Assert.Same(ChunkDecoderRegistry.Default, ChunkDecoderRegistry.Default);
}

/// <summary>The naming table, which has to answer for values nobody has seen.</summary>
public sealed class ChunkEntryTypeTests
{
    [Theory]
    [InlineData(ChunkEntryType.ZeroFill, "zero-fill")]
    [InlineData(ChunkEntryType.Raw, "raw")]
    [InlineData(ChunkEntryType.Ignore, "ignore")]
    [InlineData(ChunkEntryType.AppleAdc, "ADC")]
    [InlineData(ChunkEntryType.Zlib, "zlib")]
    [InlineData(ChunkEntryType.Bzip2, "bzip2")]
    [InlineData(ChunkEntryType.Lzfse, "LZFSE")]
    [InlineData(ChunkEntryType.Lzma, "LZMA")]
    [InlineData(ChunkEntryType.Comment, "comment")]
    [InlineData(ChunkEntryType.Terminator, "terminator")]
    public void KnownEntryTypesHaveNames(uint entryType, string expected) =>
        Assert.Equal(expected, ChunkEntryType.NameOf(entryType));

    [Fact]
    public void AnUnknownEntryTypeIsNamedByItsValue() =>
        Assert.Equal("unknown (0xDEADBEEF)", ChunkEntryType.NameOf(0xDEAD_BEEF));

    [Theory]
    [InlineData(ChunkEntryType.Comment)]
    [InlineData(ChunkEntryType.Terminator)]
    public void CommentAndTerminatorAreStructural(uint entryType) =>
        Assert.True(ChunkEntryType.IsStructural(entryType));

    [Theory]
    [InlineData(ChunkEntryType.ZeroFill)]
    [InlineData(ChunkEntryType.Zlib)]
    [InlineData(0xDEAD_BEEFu)]
    public void EverythingElseIsAPayload(uint entryType) =>
        Assert.False(ChunkEntryType.IsStructural(entryType));
}
