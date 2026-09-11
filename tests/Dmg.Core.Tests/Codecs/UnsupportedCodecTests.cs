using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// bzip2, LZFSE and LZMA: the codecs this build knows about and does not implement.
/// The contract is that they fail as values rather than exceptions, that the failure
/// names the codec so the user learns something, and - the part that matters for
/// `dmg info` - that an image full of them can still be described.
/// </summary>
public sealed class UnsupportedCodecTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    public static TheoryData<uint, string> UnimplementedCodecs => new()
    {
        { ChunkEntryTypeCodes.Bzip2, "bzip2" },
        { ChunkEntryTypeCodes.Lzfse, "LZFSE" },
        { ChunkEntryTypeCodes.Lzma, "LZMA" },
    };

    [Theory]
    [MemberData(nameof(UnimplementedCodecs))]
    public void DecodingOneFailsWithUnsupportedFormatAndNamesTheCodec(uint entryType, string name)
    {
        byte[] payload = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            entryType, payload, new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Equal(3, (int)result.Error.Code);
        Assert.Contains(name, result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnimplementedCodecs))]
    public void TheFailureSaysWhichCodecsWouldHaveWorked(uint entryType, string name)
    {
        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            entryType, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error.Detail);
        Assert.Contains("zlib", result.Error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(name, result.Error.Detail!.Split("Supported")[1], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnimplementedCodecs))]
    public void NoPayloadCanTurnOneIntoAnException(uint entryType, string name)
    {
        _ = name;

        // Whatever the data fork holds - empty, huge, random - the answer is the same
        // failure, produced without reading a byte of it.
        Random random = new(4242);

        foreach (int length in (int[])[0, 1, 4096])
        {
            byte[] payload = new byte[length];
            random.NextBytes(payload);

            Result<int> result = ChunkDecoderRegistry.Default.Decode(
                entryType, payload, new byte[SectorSize], 1);

            Assert.False(result.Ok);
            Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        }
    }

    [Theory]
    [MemberData(nameof(UnimplementedCodecs))]
    public void OneIsDescribedAsPresentButUnsupported(uint entryType, string name)
    {
        ChunkCodecInfo info = ChunkDecoderRegistry.Default.Describe(entryType);

        Assert.False(info.IsSupported);
        Assert.True(info.IsRecognised);
        Assert.True(info.IsUnsupportedPayload);
        Assert.Equal(name, info.Name);
        Assert.Contains("unsupported", info.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(entryType, ChunkDecoderRegistry.Default.SupportedEntryTypes);
    }

    [Fact]
    public void TheThreeAreExactlyWhatTheDefaultRegistryCannotDecode()
    {
        IReadOnlyList<ChunkCodecInfo> unsupported = ChunkDecoderRegistry.Default.UnsupportedCodecs;

        Assert.Equal(
            ["bzip2", "LZFSE", "LZMA"],
            unsupported.Select(info => info.Name));

        Assert.All(unsupported, info => Assert.True(info.IsUnsupportedPayload));
    }

    [Fact]
    public void AnImageMadeOfUnsupportedChunksCanStillBeDescribed()
    {
        // The `dmg info` case: a UDBZ image we will never mount must still produce an
        // inventory, from the chunk table alone, without a decode being attempted.
        uint[] chunkTable =
        [
            ChunkEntryTypeCodes.Bzip2,
            ChunkEntryTypeCodes.Bzip2,
            ChunkEntryTypeCodes.ZeroFill,
            ChunkEntryTypeCodes.Terminator,
        ];

        IReadOnlyList<ChunkCodecInfo> survey = ChunkDecoderRegistry.Default.Survey(chunkTable);

        Assert.Equal(3, survey.Count);

        ChunkCodecInfo bzip2 = survey.Single(info => info.EntryType == ChunkEntryTypeCodes.Bzip2);
        Assert.True(bzip2.IsUnsupportedPayload);

        Assert.Single(survey, info => info.IsUnsupportedPayload);
        Assert.True(survey.Single(info => info.EntryType == ChunkEntryTypeCodes.ZeroFill).IsSupported);
    }

    [Fact]
    public void AMixedImageReportsExactlyWhichCodecsAreMissing()
    {
        uint[] chunkTable =
        [
            ChunkEntryTypeCodes.Zlib,
            ChunkEntryTypeCodes.Lzfse,
            ChunkEntryTypeCodes.Raw,
            ChunkEntryTypeCodes.Lzma,
        ];

        IReadOnlyList<string> missing =
        [
            .. ChunkDecoderRegistry.Default
                .Survey(chunkTable)
                .Where(info => info.IsUnsupportedPayload)
                .Select(info => info.Name),
        ];

        Assert.Equal(["LZFSE", "LZMA"], missing);
    }

    [Fact]
    public void AValueThatIsNotInTheFormatIsReportedDifferentlyFromAMissingCodec()
    {
        // "We do not implement bzip2" and "byte 0x2A appeared where an entry type
        // should be" are different problems for the person trying to read the image.
        ChunkCodecInfo unknown = ChunkDecoderRegistry.Default.Describe(0x0000_002A);

        Assert.False(unknown.IsRecognised);
        Assert.True(unknown.IsUnsupportedPayload);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            0x0000_002A, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains("0x0000002A", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("damaged", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEntryTypeTheFormatDefinesIsRecognised()
    {
        Assert.All(ChunkEntryTypeCodes.Known, type => Assert.True(ChunkEntryTypeCodes.IsRecognised(type)));
        Assert.False(ChunkEntryTypeCodes.IsRecognised(0x8000_0009));
        Assert.False(ChunkEntryTypeCodes.IsRecognised(0x0000_0003));
    }

    [Fact]
    public void TheSupportedAndUnsupportedListsTogetherCoverTheFormat()
    {
        IEnumerable<uint> covered =
        [
            .. ChunkDecoderRegistry.Default.SupportedEntryTypes,
            .. ChunkDecoderRegistry.Default.UnsupportedCodecs.Select(info => info.EntryType),
            ChunkEntryTypeCodes.Comment,
            ChunkEntryTypeCodes.Terminator,
        ];

        Assert.Equal([.. ChunkEntryTypeCodes.Known.Order()], [.. covered.Order()]);
    }
}
