using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The context does every read the probe chain makes, so every bound the chain
/// relies on is really a bound in here.
/// </summary>
public sealed class ImageProbeContextTests
{
    [Fact]
    public void ShortFilesGiveShortWindowsRatherThanFailures()
    {
        // 300 bytes is less than either window, so the header and the trailer are
        // the same 300 bytes. Overlapping windows are the correct answer, not an
        // edge case to reject: a 300-byte file really does have those bytes at both
        // ends of it.
        byte[] file = Filled(300);

        ImageProbeContext image = Context(file);

        Assert.Equal(300, image.Length);
        Assert.Equal(300, image.Header.Length);
        Assert.Equal(300, image.Trailer.Length);
        Assert.True(image.Header.SequenceEqual(image.Trailer));
    }

    [Fact]
    public void TheHeaderStopsAtSixtyFourKibibytes()
    {
        byte[] file = Filled(ImageProbeContext.HeaderBytes + 4096);

        ImageProbeContext image = Context(file);

        Assert.Equal(ImageProbeContext.HeaderBytes, image.Header.Length);
        Assert.Equal(KolyTrailer.Size, image.Trailer.Length);
    }

    [Fact]
    public void TheTrailerIsTheLastFiveHundredAndTwelveBytesAndNoOthers()
    {
        byte[] file = Filled(2048);
        file[2048 - 512] = 0xAA;
        file[2047] = 0xBB;

        ImageProbeContext image = Context(file);

        Assert.Equal(0xAA, image.Trailer[0]);
        Assert.Equal(0xBB, image.Trailer[^1]);
    }

    [Fact]
    public void AnEmptyFileHasEmptyWindowsAndIsNotAnError()
    {
        ImageProbeContext image = Context([]);

        Assert.Equal(0, image.Length);
        Assert.True(image.Header.IsEmpty);
        Assert.True(image.Trailer.IsEmpty);
        Assert.False(image.IsWholeSectors);
        Assert.Equal(0, image.SectorCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(511, false)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    [InlineData(1024, true)]
    public void WholeSectorsMeansAtLeastOneAndNoRemainder(int length, bool expected)
    {
        ImageProbeContext image = Context(Filled(length));

        Assert.Equal(expected, image.IsWholeSectors);
    }

    [Fact]
    public void AStreamThatCannotSeekIsAnInternalErrorNotACorruptImage()
    {
        // Nothing about a pipe says anything about the image in it, so blaming the
        // image would be a lie. This is the calling code's mistake.
        using NonSeekableStream stream = new([1, 2, 3]);

        Result<ImageProbeContext> result = ImageProbeContext.Create(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void TheSourcePathIsCarriedThroughForMessages()
    {
        ImageProbeContext image = Context(Filled(512), @"C:\downloads\thing.dmg");

        Assert.Equal(@"C:\downloads\thing.dmg", image.SourcePath);
        Assert.Contains("thing.dmg", image.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAPathTheDescriptionIsStillASentenceFragment()
    {
        ImageProbeContext image = Context(Filled(512));

        Assert.Equal("this image", image.Describe());
    }

    [Fact]
    public void NullStreamIsARejectedArgument() =>
        Assert.Throws<ArgumentNullException>(() => ImageProbeContext.Create(null!));

    internal static ImageProbeContext Context(byte[] file, string? sourcePath = null)
    {
        MemoryStream stream = new(file, writable: false);
        Result<ImageProbeContext> result = ImageProbeContext.Create(stream, sourcePath);

        Assert.True(result.TryGetValue(out ImageProbeContext? image), result.Ok ? "" : result.Error.ToString());

        return image;
    }

    /// <summary>A file whose every byte differs from its neighbours, so slices are visible.</summary>
    internal static byte[] Filled(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
