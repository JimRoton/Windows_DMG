using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// Raw chunks are a copy, so the tests are mostly about the length rule:
/// CompressedLength must equal SectorCount * 512, and a mismatch is corruption
/// rather than something to paper over.
/// </summary>
public sealed class RawChunkDecoderTests
{
    private const int SectorSize = ChunkDecoderRegistry.BytesPerSector;

    private static byte[] Pattern(int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31) + 7))];

    [Fact]
    public void ItClaimsEntryTypeOneAndReadsTheDataFork()
    {
        Assert.Equal(ChunkEntryType.Raw, RawChunkDecoder.Instance.EntryType);
        Assert.Equal("raw", RawChunkDecoder.Instance.Name);
        Assert.True(RawChunkDecoder.Instance.ReadsDataFork);
    }

    [Fact]
    public void AFullChunkIsCopiedByteForByte()
    {
        byte[] source = Pattern(2 * SectorSize);
        byte[] destination = new byte[2 * SectorSize];

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, source, destination, 2);

        Assert.True(result.Ok);
        Assert.Equal(source.Length, result.Value);
        Assert.Equal(source, destination);
    }

    [Fact]
    public void OnlyTheDeclaredSectorsAreWritten()
    {
        byte[] source = Pattern(SectorSize);
        byte[] destination = new byte[3 * SectorSize];
        Array.Fill(destination, (byte)0x77);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, source, destination, 1);

        Assert.True(result.Ok);
        Assert.Equal(source, destination[..SectorSize]);
        Assert.All(destination[SectorSize..], b => Assert.Equal(0x77, b));
    }

    [Fact]
    public void AShortChunkIsCorruptRatherThanZeroPadded()
    {
        // The tail would otherwise be whatever the buffer already held.
        byte[] source = Pattern(SectorSize - 1);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, source, new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("raw", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverLongChunkIsCorruptRatherThanTruncated()
    {
        byte[] source = Pattern(SectorSize + 1);

        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, source, new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnEmptyChunkIsCorruptWhenSectorsWereDeclared()
    {
        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, [], new byte[SectorSize], 1);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AFailedRawChunkLeavesNoPartialCopyClaim()
    {
        // A rejected chunk must not report bytes written; callers key off the count.
        byte[] destination = new byte[SectorSize];

        Result<int> result = RawChunkDecoder.Instance.Decode(Pattern(4), destination);

        Assert.False(result.Ok);
        Assert.Equal(0, result.GetValueOrDefault());
    }

    [Fact]
    public void AZeroSectorRawChunkWithNoPayloadIsFine()
    {
        Result<int> result = ChunkDecoderRegistry.Default.Decode(
            ChunkEntryType.Raw, [], new byte[SectorSize], 0);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void AWholeSyntheticRawImageRoundTrips()
    {
        // Three chunks of an image, decoded in order into one buffer, must
        // reassemble the original bytes exactly.
        byte[] original = Pattern(6 * SectorSize);
        byte[] rebuilt = new byte[original.Length];

        for (int chunk = 0; chunk < 3; chunk++)
        {
            int offset = chunk * 2 * SectorSize;

            Result<int> result = ChunkDecoderRegistry.Default.Decode(
                ChunkEntryType.Raw,
                original.AsSpan(offset, 2 * SectorSize),
                rebuilt.AsSpan(offset),
                2);

            Assert.True(result.Ok);
        }

        Assert.Equal(original, rebuilt);
    }
}
