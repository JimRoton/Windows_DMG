using System.Diagnostics;
using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// S5.5: a throughput floor for the ordinary case - a sequential read through a
/// realistically mixed image - cheap enough to run on every build, so a
/// regression that makes reading pathologically slow (an accidental re-decode of
/// every chunk on every call, quadratic behaviour in the extent lookup, a lock
/// held for the whole read instead of just the I/O) fails CI instead of only
/// showing up as a complaint from whoever mounts a large image on real hardware.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recorded baseline.</b> Measured on the sandboxed macOS worktree these
/// stories were built in (.NET 10, Debug configuration, three quarters zlib /
/// one quarter raw stored chunks, 12 MiB decoded, three runs): <b>1447, 1489,
/// 1560 MB/s</b> sequential. That is almost entirely inflate and buffer copies -
/// the synthetic image lives in a <see cref="MemoryStream"/>, so there is no
/// real disk or filesystem cache underneath it to also measure.
/// </para>
/// <para>
/// <see cref="RegressionFloorMegabytesPerSecond"/> is set an order of magnitude
/// below that baseline on purpose: this suite runs on whatever hardware CI
/// happens to schedule it on, potentially shared and potentially throttled, and
/// the point is to catch an accidental algorithmic regression - something that
/// makes this 10x+ slower - not to notice normal machine-to-machine variance.
/// A real regression in cache or prefetch behaviour (re-decoding every chunk on
/// every read, holding a lock across a whole read instead of just the I/O) costs
/// far more than 10x, so the floor still catches it.
/// </para>
/// </remarks>
public sealed class DmgBlockStreamThroughputTests
{
    /// <summary>
    /// See the class remarks for how this was chosen relative to the measured
    /// baseline. Comfortably conservative, not a tight bound.
    /// </summary>
    private const double RegressionFloorMegabytesPerSecond = 50.0;

    /// <summary>
    /// A realistically mixed image, sized to keep the whole benchmark - building
    /// it and reading it - well under CI's patience: 12 MiB decoded is enough to
    /// swamp per-call fixed costs without making the suite noticeably slower.
    /// </summary>
    private static SyntheticUdifImage ThroughputImage()
    {
        const int chunkSectors = 256; // 128 KiB decoded per chunk.
        const int chunkCount = 96; // 96 * 128 KiB = 12 MiB decoded.

        List<SyntheticUdif.ChunkSpec> chunks = new(chunkCount);

        for (int index = 0; index < chunkCount; index++)
        {
            // Three quarters zlib, one quarter raw - closer to a typical hdiutil
            // UDZO image than an all-one-codec image would be.
            chunks.Add(index % 4 == 3
                ? SyntheticUdif.ChunkSpec.Raw(chunkSectors)
                : SyntheticUdif.ChunkSpec.Zlib(chunkSectors));
        }

        return SyntheticUdif.Build(chunks, regionCount: 1);
    }

    [Fact]
    public void SequentialReadThroughputStaysAboveTheRegressionFloor()
    {
        SyntheticUdifImage image = ThroughputImage();

        Result<DmgBlockStream> opened = DmgBlockStream.Open(image.OpenSource(), leaveOpen: false);
        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        using DmgBlockStream stream = opened.Value!;

        // A realistic caller-sized copy buffer, not a byte at a time and not the
        // whole disk in one call - CopyTo's own default is smaller than this.
        byte[] buffer = new byte[1024 * 1024];
        long totalRead = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            totalRead += read;
        }

        stopwatch.Stop();

        // Correctness first: a throughput number over a partial or wrong read
        // would be meaningless.
        Assert.Equal(image.Length, totalRead);

        double megabytesPerSecond =
            totalRead / (1024.0 * 1024.0) / stopwatch.Elapsed.TotalSeconds;

        Assert.True(
            megabytesPerSecond >= RegressionFloorMegabytesPerSecond,
            $"Sequential read throughput was {megabytesPerSecond:F1} MB/s over " +
            $"{totalRead / (1024.0 * 1024.0):F1} MiB in {stopwatch.Elapsed.TotalMilliseconds:F0} ms, " +
            $"below the {RegressionFloorMegabytesPerSecond:F0} MB/s regression floor.");
    }
}
