using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// S5.2's chunk cache: a configurable byte budget that never changes what a read
/// produces, only how much I/O and decoding it costs to produce it.
/// </summary>
/// <remarks>
/// The cache lives entirely behind <see cref="DmgBlockStream"/>'s public
/// <see cref="Stream"/> surface, so it is exercised the same way the rest of the
/// class is: through reads, and through <see cref="CountingStream"/> for the
/// claims a return value cannot show on its own.
/// </remarks>
public sealed class DmgBlockStreamCacheTests
{
    private static SyntheticUdifImage RepeatedAccessImage() => SyntheticUdif.Build(
        SyntheticUdif.ChunkSpec.Zlib(8),
        SyntheticUdif.ChunkSpec.Zlib(6),
        SyntheticUdif.ChunkSpec.Raw(4),
        SyntheticUdif.ChunkSpec.Zlib(10),
        SyntheticUdif.ChunkSpec.Zero(20),
        SyntheticUdif.ChunkSpec.Raw(3));

    /// <summary>
    /// The required S5.2 test: a cache too small to hold anything and a cache too
    /// large to ever evict anything must read back byte-for-byte identical disks.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L * 1024 * 1024 * 1024)]
    public void CacheSizeNeverChangesWhatARandomAccessPatternReads(long cacheCapacityBytes)
    {
        SyntheticUdifImage image = RepeatedAccessImage();
        using DmgBlockStream stream = Open(image, cacheCapacityBytes);

        // Deliberately not a single forward pass: seek back and forth across
        // chunk boundaries, including re-visiting chunks already left behind, so
        // eviction (or its absence) has a chance to matter.
        int[] pieceSizes = [4096, 1, 512, 2000, 300, 4096, 700];
        var random = new Random(Seed: 12345);
        byte[] assembled = new byte[image.Length];

        long[] positions = [0, image.Length / 3, image.Length / 2, 0, image.Length - 4096, image.Length / 4];

        foreach (long start in positions)
        {
            stream.Position = Math.Clamp(start, 0, Math.Max(0, image.Length - 4096));
            int pieceSize = pieceSizes[random.Next(pieceSizes.Length)];
            byte[] piece = new byte[Math.Min(pieceSize, image.Length - stream.Position)];
            int read = stream.Read(piece);
            Assert.Equal(piece.Length, read);
            Assert.Equal(image.Decoded.AsSpan((int)(stream.Position - read), read).ToArray(), piece);
        }

        // And a full, ordinary forward pass must still come out exact.
        stream.Position = 0;
        stream.ReadExactly(assembled);
        Assert.Equal(image.Decoded, assembled);
    }

    [Fact]
    public void ADefaultCacheIsSixtyFourMebibytes()
    {
        Assert.Equal(64L * 1024 * 1024, DmgBlockStream.DefaultCacheCapacityBytes);
    }

    [Fact]
    public void ANegativeCacheCapacityIsRefused()
    {
        SyntheticUdifImage image = RepeatedAccessImage();
        using var source = image.OpenSource();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DmgBlockStream.Open(source, leaveOpen: true, cacheCapacityBytes: -1));
    }

    [Fact]
    public void ACacheBigEnoughForEveryChunkDecodesEachChunkOnlyOnce()
    {
        SyntheticUdifImage image = RepeatedAccessImage();
        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            counting,
            leaveOpen: true,
            cacheCapacityBytes: 1L * 1024 * 1024 * 1024);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        // Opening itself reads the trailer and the property list; only chunk
        // reads from here on are the point of this test.
        counting.Reset();

        // Two full passes. With everything cached, the second pass must touch
        // the source no more than the first did - actually not at all, since
        // every stored chunk is already resident.
        byte[] first = new byte[image.Length];
        stream.Position = 0;
        stream.ReadExactly(first);
        int readsAfterFirstPass = counting.Reads;

        byte[] second = new byte[image.Length];
        stream.Position = 0;
        stream.ReadExactly(second);

        Assert.Equal(image.Decoded, first);
        Assert.Equal(image.Decoded, second);
        Assert.Equal(readsAfterFirstPass, counting.Reads);
    }

    [Fact]
    public void ACacheOfZeroDecodesEveryChunkEveryTime()
    {
        SyntheticUdifImage image = RepeatedAccessImage();
        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            counting,
            leaveOpen: true,
            cacheCapacityBytes: 0);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        counting.Reset();

        byte[] first = new byte[image.Length];
        stream.Position = 0;
        stream.ReadExactly(first);
        int readsAfterFirstPass = counting.Reads;

        Assert.True(readsAfterFirstPass > 0);

        byte[] second = new byte[image.Length];
        stream.Position = 0;
        stream.ReadExactly(second);

        Assert.Equal(image.Decoded, first);
        Assert.Equal(image.Decoded, second);

        // Nothing survived from the first pass, so the second pass re-reads the
        // data fork exactly as much as the first one did.
        Assert.Equal(readsAfterFirstPass, counting.Reads - readsAfterFirstPass);
    }

    [Fact]
    public void EvictionMakesARevisitedChunkReReadNotJustReDecodedWrong()
    {
        // A cache sized for exactly one chunk at a time: alternating between two
        // chunks must evict the other every time, so both come back correct
        // however many times they are re-visited.
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Zlib(8),
            SyntheticUdif.ChunkSpec.Zlib(8));

        int oneChunkBytes = image.ChunkStart(1) - image.ChunkStart(0);

        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            counting,
            leaveOpen: true,
            cacheCapacityBytes: oneChunkBytes);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        counting.Reset();

        for (int round = 0; round < 4; round++)
        {
            byte[] chunk0 = new byte[oneChunkBytes];
            stream.Position = 0;
            stream.ReadExactly(chunk0);
            Assert.Equal(image.Decoded[..oneChunkBytes], chunk0);

            byte[] chunk1 = new byte[image.Length - oneChunkBytes];
            stream.Position = oneChunkBytes;
            stream.ReadExactly(chunk1);
            Assert.Equal(image.Decoded[oneChunkBytes..], chunk1);
        }

        // The cache could hold only one chunk at a time, so every single one of
        // the eight visits above (4 rounds * 2 chunks) had to re-read the source.
        Assert.Equal(8, counting.Reads);
    }

    private static DmgBlockStream Open(SyntheticUdifImage image, long cacheCapacityBytes)
    {
        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            cacheCapacityBytes: cacheCapacityBytes);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        return opened.Value!;
    }
}
