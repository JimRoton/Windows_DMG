using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// Zero fill and ignore: the sectors an image never stored. The tests care about
/// three things - the destination comes back all zeros, every byte of it is
/// written, and the data fork is never consulted.
/// </summary>
public sealed class ZeroChunkDecoderTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    public static TheoryData<uint> ZeroEmittingEntryTypes => new(
        ChunkEntryTypeCodes.ZeroFill,
        ChunkEntryTypeCodes.Ignore);

    [Fact]
    public void ZeroFillClaimsEntryTypeZero()
    {
        Assert.Equal(ChunkEntryTypeCodes.ZeroFill, ZeroChunkDecoder.ZeroFill.EntryType);
        Assert.Equal("zero-fill", ZeroChunkDecoder.ZeroFill.Name);
    }

    [Fact]
    public void IgnoreClaimsEntryTypeTwo()
    {
        Assert.Equal(ChunkEntryTypeCodes.Ignore, ZeroChunkDecoder.Ignore.EntryType);
        Assert.Equal("ignore", ZeroChunkDecoder.Ignore.Name);
    }

    [Fact]
    public void NeitherDecoderReadsTheDataFork()
    {
        Assert.False(ZeroChunkDecoder.ZeroFill.ReadsDataFork);
        Assert.False(ZeroChunkDecoder.Ignore.ReadsDataFork);
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void TheDestinationIsFilledWithZerosEndToEnd(uint entryType)
    {
        byte[] destination = new byte[4 * SectorSize];
        Array.Fill(destination, (byte)0xCC);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(entryType, [], destination, 4);

        Assert.True(result.Ok);
        Assert.Equal(destination.Length, result.Value);
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void APreviouslyUsedBufferIsScrubbedNotAppendedTo(uint entryType)
    {
        // The realistic failure this guards: a buffer reused across chunks that
        // still holds the previous chunk's plaintext.
        byte[] destination = new byte[SectorSize];
        Array.Fill(destination, (byte)0xFF);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(entryType, [], destination, 1);

        Assert.True(result.Ok);
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void PayloadBytesOfferedForAZeroChunkAreIgnoredNotEmitted(uint entryType)
    {
        // Some images declare a CompressedLength on a zero-fill chunk. Whatever is
        // there, none of it may reach the output.
        byte[] source = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        byte[] destination = new byte[SectorSize];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(entryType, source, destination, 1);

        Assert.True(result.Ok);
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void OnlyTheDeclaredSectorsAreTouched(uint entryType)
    {
        byte[] destination = new byte[3 * SectorSize];
        Array.Fill(destination, (byte)0x5A);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(entryType, [], destination, 1);

        Assert.True(result.Ok);
        Assert.Equal(SectorSize, result.Value);
        Assert.All(destination[..SectorSize], b => Assert.Equal(0, b));
        Assert.All(destination[SectorSize..], b => Assert.Equal(0x5A, b));
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void AZeroSectorChunkIsAcceptedAndWritesNothing(uint entryType)
    {
        byte[] destination = new byte[SectorSize];
        Array.Fill(destination, (byte)0x11);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(entryType, [], destination, 0);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
        Assert.All(destination, b => Assert.Equal(0x11, b));
    }

    [Theory]
    [MemberData(nameof(ZeroEmittingEntryTypes))]
    public void BothTypesAreSupportedByTheDefaultRegistry(uint entryType)
    {
        Assert.True(ChunkDecoderRegistry.Default.IsSupported(entryType));
        Assert.True(ChunkDecoderRegistry.Default.Describe(entryType).IsSupported);
    }
}
