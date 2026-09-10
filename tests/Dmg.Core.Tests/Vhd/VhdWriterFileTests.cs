using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers the file-writing entry point: the precheck that runs before the first
/// byte, and the promise that a failed write leaves nothing behind.
/// </summary>
public sealed class VhdWriterFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-writer-tests",
        Guid.NewGuid().ToString("N"));

    public VhdWriterFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static MemoryStream Payload(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return new MemoryStream(bytes, writable: false);
    }

    [Fact]
    public void WritesAWholeVhdToTheFile()
    {
        string path = PathFor("image.vhd");
        MemoryStream source = Payload(8 * 512);

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(source, path);

        Assert.True(written.TryGetValue(out VhdWriteResult? result), "Writing to a temp file should work.");

        byte[] file = File.ReadAllBytes(path);

        Assert.Equal(result.TotalBytes, file.Length);
        Assert.Equal(8 * 512 + 512, file.Length);
        Assert.True(VhdFooter.Parse(file.AsSpan(file.Length - 512)).Ok);
    }

    [Fact]
    public void WritesNothingAtAllWhenTheVolumeHasNoRoom()
    {
        string path = PathFor("too-big.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(
            Payload(4096),
            path,
            options: null,
            progress: null,
            new StubProbe(1024));

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InsufficientSpace, written.Error.Code);
        Assert.False(File.Exists(path), "The precheck must refuse before the file is created.");
    }

    [Fact]
    public void ReportsNoProgressWhenThePrecheckRefuses()
    {
        List<VhdWriteProgress> reports = [];

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(
            Payload(4096),
            PathFor("no-progress.vhd"),
            options: null,
            new ImmediateProgress(reports.Add),
            new StubProbe(0));

        Assert.False(written.Ok);
        Assert.Empty(reports);
    }

    [Fact]
    public void LeavesNoHalfWrittenFileBehindWhenTheImageIsTruncated()
    {
        string path = PathFor("truncated.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(
            new TruncatedStream(8192, servedBytes: 1024),
            path,
            new VhdWriteOptions { BufferSize = 512 });

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, written.Error.Code);
        Assert.False(File.Exists(path), "A partly written VHD is worse than none: it invites an attach.");
    }

    [Fact]
    public void LeavesNoFileBehindWhenTheWriteIsCancelled()
    {
        string path = PathFor("cancelled.vhd");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => VhdWriter.WriteFixedToFile(
            Payload(4096),
            path,
            new VhdWriteOptions { BufferSize = 512 },
            progress: null,
            freeSpaceProbe: null,
            cancellation.Token));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OverwritesAFileAlreadyAtThatPath()
    {
        string path = PathFor("existing.vhd");
        File.WriteAllBytes(path, new byte[64 * 1024]);

        Assert.True(VhdWriter.WriteFixedToFile(Payload(1024), path).Ok);

        Assert.Equal(1024 + 512, new FileInfo(path).Length);
    }

    [Fact]
    public void ReportsAnUnwritableDirectoryAsAFailureRatherThanThrowing()
    {
        string path = Path.Combine(_root, "no-such-directory", "image.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(Payload(1024), path);

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void ScratchSpaceChecksItsOwnVolumeBeforeTheDecodeStarts()
    {
        Result<ScratchSpace> created = ScratchSpace.Create(new ScratchOptions { Root = _root });

        Assert.True(created.TryGetValue(out ScratchSpace? scratch));

        using (scratch)
        {
            // 40 GB of image, 12 GB of volume.
            Result refused = scratch.EnsureRoomFor(40L * 1024 * 1024 * 1024, new StubProbe(12L * 1024 * 1024 * 1024));

            Assert.False(refused.Ok);
            Assert.Equal(DmgExitCode.InsufficientSpace, refused.Error.Code);

            Result allowed = scratch.EnsureRoomFor(4096, new StubProbe(long.MaxValue / 2));

            Assert.True(allowed.Ok);
        }
    }

    [Fact]
    public void TheScratchVhdPathIsWhatTheWriterIsGiven()
    {
        Result<ScratchSpace> created = ScratchSpace.Create(new ScratchOptions { Root = _root });

        Assert.True(created.TryGetValue(out ScratchSpace? scratch));

        string path;

        using (scratch)
        {
            path = scratch.VhdPath;

            Assert.True(VhdWriter.WriteFixedToFile(Payload(2048), path).Ok);
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path), "Disposing the scratch space takes the VHD with it.");
    }

    private sealed class StubProbe(long availableBytes) : IFreeSpaceProbe
    {
        public Result<long> AvailableBytes(string path) => Result<long>.Success(availableBytes);
    }

    private sealed class ImmediateProgress(Action<VhdWriteProgress> handler) : IProgress<VhdWriteProgress>
    {
        public void Report(VhdWriteProgress value) => handler(value);
    }

    /// <summary>Claims one length and serves fewer bytes.</summary>
    private sealed class TruncatedStream(int length, int servedBytes) : MemoryStream(new byte[length], writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            long left = servedBytes - Position;

            return left <= 0
                ? 0
                : base.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        }
    }
}
