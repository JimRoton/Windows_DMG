using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// The <see cref="Stream"/> contract, on the one type that has to keep all of it.
/// </summary>
/// <remarks>
/// <see cref="DmgBlockStream"/> is the seam: everything above it - the VHD writer,
/// <c>extract</c>, <c>verify</c>'s hasher - is written against
/// <see cref="Stream"/> and will exercise corners of that contract nobody thought
/// about here. So the corners are tested rather than assumed: reads that stop
/// short, reads past the end, seeks to negative offsets, writes that must be
/// refused, and use after disposal.
/// </remarks>
public sealed class DmgBlockStreamTests
{
    /// <summary>A representative layout: compressed, stored, and implied chunks.</summary>
    private static SyntheticUdifImage MixedImage() => SyntheticUdif.Build(
        SyntheticUdif.ChunkSpec.Zlib(8),
        SyntheticUdif.ChunkSpec.Raw(4),
        SyntheticUdif.ChunkSpec.Zero(16),
        SyntheticUdif.ChunkSpec.Zlib(3),
        SyntheticUdif.ChunkSpec.Ignore(5),
        SyntheticUdif.ChunkSpec.Raw(1));

    [Fact]
    public void ReadsTheWholeDiskExactly()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        byte[] read = new byte[image.Length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded, read);
    }

    [Fact]
    public void LengthIsSectorCountTimesFiveHundredAndTwelve()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Equal(image.Length, stream.Length);
        Assert.Equal(image.Length / 512, (long)stream.Image.SectorCount);
        Assert.Equal(0, image.Length % 512);
    }

    [Fact]
    public void IsReadableSeekableAndPermanentlyUnwritable()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void EveryWritingMemberRefuses()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[4].AsSpan()));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [Fact]
    public void FlushIsANoOpRatherThanARefusal()
    {
        // Consumers flush indiscriminately. Throwing here would break a copy loop
        // that has done nothing wrong.
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        stream.Flush();
        Assert.True(stream.FlushAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4096)]
    public void ReadingInSmallPiecesProducesTheSameDisk(int pieceSize)
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        byte[] assembled = new byte[image.Length];
        int filled = 0;

        while (filled < assembled.Length)
        {
            int read = stream.Read(assembled.AsSpan(filled, Math.Min(pieceSize, assembled.Length - filled)));
            Assert.True(read > 0, $"Read returned {read} at offset {filled} with {assembled.Length - filled} bytes still to come.");
            filled += read;
        }

        Assert.Equal(image.Decoded, assembled);
        Assert.Equal(0, stream.Read(new byte[16]));
    }

    [Fact]
    public void AReadThatRunsOffTheEndStopsAtTheEnd()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        stream.Position = image.Length - 100;

        byte[] buffer = new byte[4096];
        int read = stream.Read(buffer);

        Assert.Equal(100, read);
        Assert.Equal(image.Decoded[^100..], buffer[..100]);
        Assert.Equal(image.Length, stream.Position);
    }

    [Fact]
    public void ReadingAtTheEndReturnsZeroRatherThanFailing()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        stream.Position = image.Length;
        Assert.Equal(0, stream.Read(new byte[64]));
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void ReadingPastTheEndReturnsZeroRatherThanFailing()
    {
        // Seeking past the end is legal on a seekable stream, and the read that
        // follows is a no-op, not an error.
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Equal(image.Length + 4096, stream.Seek(image.Length + 4096, SeekOrigin.Begin));
        Assert.Equal(0, stream.Read(new byte[64]));
    }

    [Fact]
    public void AnEmptyBufferReadsNothingAndMovesNothing()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Equal(0, stream.Read(Array.Empty<byte>().AsSpan()));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void SeekWorksFromAllThreeOrigins()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Equal(1024, stream.Seek(1024, SeekOrigin.Begin));
        Assert.Equal(1536, stream.Seek(512, SeekOrigin.Current));
        Assert.Equal(image.Length - 512, stream.Seek(-512, SeekOrigin.End));

        byte[] tail = new byte[512];
        stream.ReadExactly(tail);
        Assert.Equal(image.Decoded[^512..], tail);
    }

    [Fact]
    public void SeekingBeforeTheBeginningIsAnIoException()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-(image.Length + 1), SeekOrigin.End));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void ANegativePositionIsRefused()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }

    [Fact]
    public void AnUnknownSeekOriginIsRefused()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
    }

    [Fact]
    public void ArrayReadsValidateTheirArguments()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], 3, 2));
    }

    [Fact]
    public void CopyToProducesTheWholeDisk()
    {
        // The VHD writer will do exactly this, so it is worth proving that the
        // BCL's own copy loop is satisfied by our Read.
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        var sink = new MemoryStream();
        stream.CopyTo(sink, bufferSize: 1024);

        Assert.Equal(image.Decoded, sink.ToArray());
    }

    [Fact]
    public async Task ReadAsyncProducesTheWholeDisk()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        byte[] read = new byte[image.Length];
        await stream.ReadExactlyAsync(read, CancellationToken.None);

        Assert.Equal(image.Decoded, read);
    }

    [Fact]
    public async Task ReadAsyncHonoursCancellation()
    {
        SyntheticUdifImage image = MixedImage();
        using DmgBlockStream stream = Open(image);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            int read = await stream.ReadAsync(new byte[16].AsMemory(), cancelled.Token);
            Assert.Fail($"A cancelled read returned {read} instead of throwing.");
        });
    }

    [Fact]
    public void ReadByteWalksTheDiskOneByteAtATime()
    {
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Zlib(1),
            SyntheticUdif.ChunkSpec.Zero(1),
            SyntheticUdif.ChunkSpec.Raw(1));

        using DmgBlockStream stream = Open(image);

        for (int offset = 0; offset < image.Length; offset++)
        {
            Assert.Equal(image.Decoded[offset], (byte)stream.ReadByte());
        }

        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void ImpliedChunksAreServedWithoutTouchingTheDataFork()
    {
        // Zero-fill and ignore extents are decoded straight into the caller's
        // buffer. That is not only faster - it is what lets a sparse image declare
        // an extent far larger than the ceiling a stored chunk is held to.
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Zero(64),
            SyntheticUdif.ChunkSpec.Ignore(64));

        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);
        using DmgBlockStream stream = OpenOver(counting);

        counting.Reset();

        byte[] read = new byte[image.Length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded, read);
        Assert.All(read, value => Assert.Equal((byte)0, value));
        Assert.Equal(0, counting.Reads);
        Assert.Equal(0, counting.Seeks);
    }

    [Fact]
    public void RegionRelativeSectorNumbersAreResolvedAgainstTheirOwnRegion()
    {
        // A chunk's SectorNumber is relative to its mish block, not to the disk.
        // One region hides that; three do not.
        List<SyntheticUdif.ChunkSpec> chunks =
        [
            SyntheticUdif.ChunkSpec.Zlib(4),
            SyntheticUdif.ChunkSpec.Raw(2),
            SyntheticUdif.ChunkSpec.Zero(8),
            SyntheticUdif.ChunkSpec.Zlib(6),
            SyntheticUdif.ChunkSpec.Raw(3),
            SyntheticUdif.ChunkSpec.Zlib(1),
        ];

        SyntheticUdifImage image = SyntheticUdif.Build(chunks, regionCount: 3);

        using DmgBlockStream stream = Open(image);
        Assert.Equal(3, stream.Image.Regions.Count);

        byte[] read = new byte[image.Length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded, read);
    }

    [Fact]
    public void AnUnsupportedCodecFailsAtReadTimeNamingTheCodec()
    {
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Zlib(2),
            SyntheticUdif.ChunkSpec.Bzip2(2));

        using DmgBlockStream stream = Open(image);

        // The supported chunk still reads: refusal is per chunk, at decode time,
        // which is what lets `dmg info` describe an image it cannot mount.
        byte[] first = new byte[1024];
        stream.ReadExactly(first);
        Assert.Equal(image.Decoded[..1024], first);

        DmgStreamException failure = Assert.Throws<DmgStreamException>(
            () => stream.ReadExactly(new byte[1024]));

        Assert.Equal(DmgExitCode.UnsupportedFormat, failure.Code);
        Assert.Contains("bzip2", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<IOException>(failure);
    }

    [Fact]
    public void ATruncatedDataForkIsACorruptImageNotACrash()
    {
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Raw(4),
            SyntheticUdif.ChunkSpec.Raw(4));

        // Point the trailer's data fork at a source that stops halfway through it.
        byte[] truncated = image.Bytes;
        using var source = new MemoryStream(truncated, writable: false);

        Result<DmgImage> opened = DmgImage.Open(source);
        Assert.True(opened.TryGetValue(out DmgImage? parsed), opened.Ok ? "" : opened.Error.ToString());

        // Re-open over a source that ends immediately after the first chunk.
        using var clipped = new MemoryStream(truncated[..1024], writable: false);
        Result<DmgBlockStream> created = DmgBlockStream.Create(clipped, parsed!, leaveOpen: true);
        Assert.True(created.TryGetValue(out DmgBlockStream? stream), created.Ok ? "" : created.Error.ToString());

        using (stream)
        {
            stream!.Position = 4 * 512;
            DmgStreamException failure = Assert.Throws<DmgStreamException>(
                () => stream.ReadExactly(new byte[512]));

            Assert.Equal(DmgExitCode.CorruptImage, failure.Code);
        }
    }

    [Fact]
    public void DisposingClosesTheSourceUnlessAskedNotTo()
    {
        SyntheticUdifImage image = MixedImage();

        using (var owned = new CountingStream(image.OpenSource(), leaveOpen: true))
        {
            OpenOver(owned, leaveOpen: false).Dispose();
            Assert.True(owned.Disposed);
        }

        using var borrowed = new CountingStream(image.OpenSource(), leaveOpen: true);
        OpenOver(borrowed, leaveOpen: true).Dispose();
        Assert.False(borrowed.Disposed);
    }

    [Fact]
    public void UseAfterDisposalIsRefusedRatherThanUndefined()
    {
        SyntheticUdifImage image = MixedImage();
        DmgBlockStream stream = Open(image);
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[16]));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);

        // Disposing twice is legal, as it is for every other stream.
        stream.Dispose();
    }

    [Fact]
    public void OpeningSomethingThatIsNotAUdifImageIsAResultNotAnException()
    {
        using var nonsense = new MemoryStream(new byte[4096]);

        Result<DmgBlockStream> opened = DmgBlockStream.Open(nonsense, leaveOpen: true);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, opened.Error.Code);
    }

    [Fact]
    public void OpeningRefusesAStreamThatCannotSeek()
    {
        SyntheticUdifImage image = MixedImage();
        using var unseekable = new UnseekableStream(image.OpenSource());

        Result<DmgBlockStream> opened = DmgBlockStream.Open(unseekable, leaveOpen: true);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.InternalError, opened.Error.Code);
    }

    [Fact]
    public void OpeningRejectsANullSource()
    {
        Assert.Throws<ArgumentNullException>(() => DmgBlockStream.Open(null!));
        Assert.Throws<ArgumentNullException>(() => DmgImage.Open(null!));
    }

    /// <summary>Opens a synthetic image, failing the test if it will not open.</summary>
    private static DmgBlockStream Open(SyntheticUdifImage image) =>
        OpenOver(image.OpenSource(), leaveOpen: false);

    private static DmgBlockStream OpenOver(Stream source, bool leaveOpen = true)
    {
        Result<DmgBlockStream> opened = DmgBlockStream.Open(source, leaveOpen);
        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        return opened.Value!;
    }

    /// <summary>A stream that reads but will not seek, to prove opening refuses one.</summary>
    private sealed class UnseekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
