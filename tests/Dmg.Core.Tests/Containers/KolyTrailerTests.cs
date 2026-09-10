using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The koly trailer is the entry point to a UDIF file, so it is also the first
/// place a hostile image gets to lie. Every offset it declares is checked against
/// the real file length before anything else is read.
/// </summary>
public sealed class KolyTrailerTests
{
    [Fact]
    public void ParsesATrailerWrittenByHdiutil()
    {
        Result<KolyTrailer> result = KolyTrailer.Parse(
            RealImageSamples.Koly(),
            RealImageSamples.FileLength);

        Assert.True(result.TryGetValue(out KolyTrailer? koly), result.Ok ? "" : result.Error.ToString());

        Assert.Equal(4u, koly.Version);
        // hdiutil leaves both segment fields zero on a single-part image; it does
        // not write 1 of 1. Anything treating SegmentCount as a count must cope.
        Assert.Equal(0u, koly.SegmentNumber);
        Assert.Equal(0u, koly.SegmentCount);
        Assert.False(koly.IsMultiPart);
        Assert.Equal(0ul, koly.DataForkOffset);
        Assert.Equal(70702ul, koly.DataForkLength);
        Assert.Equal(70702ul, koly.XmlOffset);
        Assert.Equal(3600ul, koly.XmlLength);
        Assert.Equal(67647ul, koly.SectorCount);

        Assert.True(koly.DecodedLengthInBytes().TryGetValue(out ulong bytes));
        Assert.Equal(67647ul * 512, bytes);
    }

    [Fact]
    public void TheRealTrailerPutsTheXmlFlushAgainstItself()
    {
        // XMLOffset + XMLLength == fileLength - 512 exactly in an hdiutil image,
        // so the bounds check has to be inclusive or every real image is rejected.
        KolyTrailer koly = Parsed(RealImageSamples.Koly(), RealImageSamples.FileLength);

        Assert.Equal((ulong)RealImageSamples.FileLength - 512, koly.XmlOffset + koly.XmlLength);
    }

    [Fact]
    public void ReadsTheTrailerFromTheEndOfAStream()
    {
        byte[] image = new byte[4096];
        RealImageSamples.Koly().CopyTo(image, image.Length - 512);

        // The sample's offsets belong to a 74,814-byte file, so only the fields that
        // do not depend on file length are meaningful here; point them somewhere legal.
        UdifBuilder.PutUInt64(image, image.Length - 512 + 0xD8, 0);
        UdifBuilder.PutUInt64(image, image.Length - 512 + 0xE0, 16);
        UdifBuilder.PutUInt64(image, image.Length - 512 + 0x020, 16);

        using var stream = new MemoryStream(image);
        Result<KolyTrailer> result = KolyTrailer.Read(stream);

        Assert.True(result.TryGetValue(out KolyTrailer? koly));
        Assert.Equal(67647ul, koly.SectorCount);
    }

    [Fact]
    public void AFileWithoutAKolyTrailerIsUnsupportedNotCorrupt()
    {
        byte[] trailer = new UdifBuilder.Koly { Signature = 0x504B0304 }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void ABadMagicSaysWhatWasThereInstead()
    {
        byte[] trailer = new UdifBuilder.Koly { Signature = 0x6B6F6C78 }.ToArray(); // "kolx"

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Contains("kolx", result.Error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailerIsNeverSearchedForInAFileTooShortToHoldOne()
    {
        Result<KolyTrailer> result = KolyTrailer.Parse(new UdifBuilder.Koly().ToArray(), 511);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(0xD8)]
    [InlineData(0x1EB)]
    [InlineData(511)]
    public void ATrailerTruncatedAtAnyFieldBoundaryIsRejected(int keep)
    {
        byte[] trailer = new UdifBuilder.Koly().ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer.AsSpan(0, keep), 4096);

        Assert.False(result.Ok);
        Assert.NotEqual(DmgExitCode.Success, result.Error.Code);
    }

    [Fact]
    public void AHeaderSizeOtherThan512IsCorruption()
    {
        byte[] trailer = new UdifBuilder.Koly { HeaderSize = 511 }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("512", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTrailerVersionIsUnsupported()
    {
        byte[] trailer = new UdifBuilder.Koly { Version = 5 }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(4096ul, 0ul)]                       // starts after the trailer
    [InlineData(3000ul, 1000ul)]                    // ends after the trailer
    [InlineData(0ul, ulong.MaxValue)]               // an absurd length
    [InlineData(ulong.MaxValue, 1ul)]               // offset + length wraps
    [InlineData(ulong.MaxValue, ulong.MaxValue)]
    public void AnXmlRangeOutsideTheFileIsCorruption(ulong offset, ulong length)
    {
        byte[] trailer = new UdifBuilder.Koly { XmlOffset = offset, XmlLength = length }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("XML", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4097ul, 0ul)]
    [InlineData(4000ul, 97ul)]
    [InlineData(0ul, ulong.MaxValue)]
    [InlineData(ulong.MaxValue, ulong.MaxValue)]
    public void ADataForkRangeOutsideTheFileIsCorruption(ulong offset, ulong length)
    {
        byte[] trailer = new UdifBuilder.Koly { DataForkOffset = offset, DataForkLength = length }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("data fork", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataForkMayEndExactlyAtTheEndOfTheFile()
    {
        byte[] trailer = new UdifBuilder.Koly { DataForkOffset = 0, DataForkLength = 4096 }.ToArray();

        Assert.True(KolyTrailer.Parse(trailer, 4096).Ok);
    }

    [Fact]
    public void ASectorCountThatOverflowsTheByteLengthIsCorruption()
    {
        // 2^55 sectors * 512 bytes is exactly 2^64: wraps to zero unchecked.
        byte[] trailer = new UdifBuilder.Koly { SectorCount = 1ul << 55 }.ToArray();

        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AMultiPartImageReportsItsSegments()
    {
        byte[] trailer = new UdifBuilder.Koly { SegmentNumber = 2, SegmentCount = 3 }.ToArray();

        KolyTrailer koly = Parsed(trailer, 4096);

        Assert.True(koly.IsMultiPart);
        Assert.Equal(2u, koly.SegmentNumber);
        Assert.Equal(3u, koly.SegmentCount);
    }

    [Fact]
    public void AStreamShorterThanATrailerIsUnsupported()
    {
        using var stream = new MemoryStream(new byte[100]);

        Result<KolyTrailer> result = KolyTrailer.Read(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void ANonSeekableStreamIsAProgrammingErrorNotACorruptImage()
    {
        using var stream = new NonSeekableStream(new byte[4096]);

        Result<KolyTrailer> result = KolyTrailer.Read(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void AnEmptyStreamIsUnsupported()
    {
        using var stream = new MemoryStream();

        Assert.Equal(DmgExitCode.UnsupportedFormat, KolyTrailer.Read(stream).Error.Code);
    }

    private static KolyTrailer Parsed(ReadOnlySpan<byte> trailer, long fileLength)
    {
        Result<KolyTrailer> result = KolyTrailer.Parse(trailer, fileLength);
        Assert.True(result.Ok);
        return result.Value!;
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
