using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// S5.4: sequential-read detection and its background prefetch of the next
/// chunk - required to change nothing about what a read returns, only when the
/// decode work for it happened.
/// </summary>
public sealed class DmgBlockStreamPrefetchTests
{
    private static SyntheticUdifImage SequentialImage() => SyntheticUdif.Build(
        SyntheticUdif.ChunkSpec.Zlib(4),
        SyntheticUdif.ChunkSpec.Zlib(6),
        SyntheticUdif.ChunkSpec.Raw(3),
        SyntheticUdif.ChunkSpec.Zlib(5),
        SyntheticUdif.ChunkSpec.Zero(8),
        SyntheticUdif.ChunkSpec.Raw(4),
        SyntheticUdif.ChunkSpec.Zlib(7));

    /// <summary>
    /// The required S5.4 test: a full sequential read must come out identical
    /// whether or not the background prefetcher is running.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASequentialReadProducesTheSameDiskWithPrefetchOnOrOff(bool enablePrefetch)
    {
        SyntheticUdifImage image = SequentialImage();

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            enablePrefetch: enablePrefetch);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        byte[] read = new byte[image.Length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded, read);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedSmallSequentialReadsStillProduceTheExactDisk(bool enablePrefetch)
    {
        // Small pieces mean many DecodedChunk calls per chunk and many chances
        // for a background prefetch to race a foreground decode of the same
        // ordinal; the outcome must still be exactly right.
        SyntheticUdifImage image = SequentialImage();

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            enablePrefetch: enablePrefetch);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        byte[] assembled = new byte[image.Length];
        int filled = 0;

        while (filled < assembled.Length)
        {
            int piece = Math.Min(37, assembled.Length - filled);
            int read = stream.Read(assembled.AsSpan(filled, piece));
            Assert.True(read > 0);
            filled += read;
        }

        Assert.Equal(image.Decoded, assembled);
    }

    [Fact]
    public void APrefetchThatHitsAnUnsupportedCodecDoesNotCrashTheStream()
    {
        // Chunk 2 is a codec nobody implements. Reading up through chunk 1
        // sequentially puts the stream one sequential step away from prefetching
        // it in the background; that prefetch must fail silently, and the
        // caller's own read of chunk 2 must still report the real error rather
        // than anything torn or swallowed.
        SyntheticUdifImage image = SyntheticUdif.Build(
            SyntheticUdif.ChunkSpec.Zlib(4),
            SyntheticUdif.ChunkSpec.Zlib(4),
            SyntheticUdif.ChunkSpec.Bzip2(4));

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            enablePrefetch: true);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        byte[] first = new byte[image.ChunkStart(2)];
        stream.ReadExactly(first);
        Assert.Equal(image.Decoded[..image.ChunkStart(2)], first);

        // Give a background prefetch of chunk 2 a moment to run and fail, if one
        // was started, before the foreground read below meets the same chunk.
        Thread.Sleep(50);

        DmgStreamException failure = Assert.Throws<DmgStreamException>(
            () => stream.ReadExactly(new byte[512]));

        Assert.Equal(DmgExitCode.UnsupportedFormat, failure.Code);
    }

    [Fact]
    public void ARandomAccessPatternNeverSpawnsAPrefetchThatChangesTheResult()
    {
        // Two accesses in a row are enough to look sequential; jumping straight
        // back to an earlier chunk resets the streak. Either way, correctness
        // cannot depend on it.
        SyntheticUdifImage image = SequentialImage();

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            enablePrefetch: true);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        int[] chunkStarts = Enumerable.Range(0, 7).Select(image.ChunkStart).ToArray();
        var order = new[] { 3, 3, 0, 6, 1, 1, 4, 2, 5, 0, 6 };

        foreach (int ordinal in order)
        {
            int start = chunkStarts[ordinal];
            int end = ordinal + 1 < chunkStarts.Length ? chunkStarts[ordinal + 1] : image.Length;
            int length = end - start;

            stream.Position = start;
            byte[] read = new byte[length];
            stream.ReadExactly(read);

            Assert.Equal(image.Decoded.AsSpan(start, length).ToArray(), read);
        }
    }

    /// <summary>
    /// The "disabled when the cache can't hold it" half of S5.4's contract: a
    /// zero-capacity cache never has anywhere to put a prefetched chunk, so a
    /// sequential run over it must behave exactly as if prefetch were off -
    /// including never spawning the background thread at all, which this proves
    /// indirectly by disposing promptly rather than needing the 5-second join
    /// timeout <see cref="ChunkPrefetcher.Dispose"/> would fall back to for a
    /// thread stuck on real work.
    /// </summary>
    [Fact]
    public void APrefetchIsDisabledWhenTheCacheCannotHoldTheNextChunk()
    {
        SyntheticUdifImage image = SequentialImage();

        Result<DmgBlockStream> opened = DmgBlockStream.Open(
            image.OpenSource(),
            leaveOpen: false,
            cacheCapacityBytes: 0,
            enablePrefetch: true);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        byte[] read = new byte[image.Length];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        stream.ReadExactly(read);
        sw.Stop();

        Assert.Equal(image.Decoded, read);

        // Disposing a stream whose prefetcher was never created returns
        // immediately; one stuck joining a real background thread would not.
        var disposeTimer = System.Diagnostics.Stopwatch.StartNew();
        stream.Dispose();
        disposeTimer.Stop();

        Assert.True(disposeTimer.ElapsedMilliseconds < 1000, $"Dispose took {disposeTimer.ElapsedMilliseconds} ms.");
    }
}
