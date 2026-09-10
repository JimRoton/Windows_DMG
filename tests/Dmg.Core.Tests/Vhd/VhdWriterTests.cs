using Dmg.Core.Imaging;
using Dmg.Core.Tests.Imaging;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers the streaming fixed-VHD writer: the shape of the file it produces, the
/// progress it reports, and what it does with a source or a destination that
/// misbehaves.
/// </summary>
/// <remarks>
/// The byte-for-byte agreement between the written sectors and the source stream
/// is the round-trip suite's job (S7.5). What is checked here is everything the
/// writer itself decides: sizes, padding, buffering, progress and failure codes.
/// </remarks>
public sealed class VhdWriterTests
{
    private static readonly Guid SampleId = new("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static readonly DateTimeOffset SampleTime = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static VhdWriteOptions Deterministic(int? bufferSize = null) => new()
    {
        UniqueId = SampleId,
        CreatedUtc = SampleTime,
        BufferSize = bufferSize ?? VhdWriteOptions.DefaultBufferSize,
    };

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            // Sector-dependent, so a sector written to the wrong offset shows up.
            bytes[index] = (byte)((index / 512) + (index % 251));
        }

        return bytes;
    }

    [Fact]
    public void WritesThePayloadThenA512ByteFooter()
    {
        byte[] payload = Pattern(4 * 512);
        MemoryStream destination = new();

        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            destination,
            Deterministic());

        Assert.True(written.TryGetValue(out VhdWriteResult? result), "A 2 KiB payload should write.");

        byte[] file = destination.ToArray();

        Assert.Equal(payload.Length + 512, file.Length);
        Assert.Equal(payload, file[..payload.Length]);
        Assert.Equal(payload.Length, result.DiskSize);
        Assert.Equal(payload.Length + 512L, result.TotalBytes);
        Assert.False(result.WasPadded);
    }

    [Fact]
    public void TheAppendedFooterParsesBackAsAFixedDiskOfTheRightSize()
    {
        byte[] payload = Pattern(16 * 512);
        MemoryStream destination = new();

        Assert.True(VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            destination,
            Deterministic()).Ok);

        byte[] file = destination.ToArray();
        Result<VhdFooter> parsed = VhdFooter.Parse(file.AsSpan(file.Length - 512));

        Assert.True(parsed.TryGetValue(out VhdFooter? footer), "The appended footer must parse.");
        Assert.Equal(VhdDiskType.Fixed, footer.DiskType);
        Assert.Equal(payload.Length, footer.DiskSize);
        Assert.Equal(SampleId, footer.UniqueId);
        Assert.Equal(SampleTime, footer.CreatedUtc);
        Assert.Equal(VhdGeometry.ForDiskSize(payload.Length), footer.Geometry);
    }

    [Fact]
    public void PadsAPayloadThatDoesNotEndOnASectorBoundary()
    {
        byte[] payload = Pattern(1000);
        MemoryStream destination = new();

        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            destination,
            Deterministic());

        Assert.True(written.TryGetValue(out VhdWriteResult? result));

        byte[] file = destination.ToArray();

        Assert.Equal(1024 + 512, file.Length);
        Assert.Equal(1024, result.DiskSize);
        Assert.Equal(1000, result.SourceBytes);
        Assert.True(result.WasPadded);
        Assert.Equal(payload, file[..1000]);
        Assert.All(file[1000..1024], padding => Assert.Equal(0, padding));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(64 * 1024)]
    public void ProducesTheSameFileWhateverTheBufferSize(int bufferSize)
    {
        byte[] payload = Pattern(37 * 512);

        MemoryStream reference = new();
        Assert.True(VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            reference,
            Deterministic()).Ok);

        MemoryStream buffered = new();
        Assert.True(VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            buffered,
            Deterministic(bufferSize)).Ok);

        Assert.Equal(reference.ToArray(), buffered.ToArray());
    }

    [Fact]
    public void ReadsInBuffersOfTheRequestedSizeRatherThanFourKilobytes()
    {
        // Nine 64 KiB reads for a 512 KiB payload would be a 4 KiB writer; a
        // one-buffer-per-chunk writer asks for exactly what it was told to.
        byte[] payload = Pattern(512 * 1024);
        CountingStream counted = new(new MemoryStream(payload, writable: false));

        Assert.True(VhdWriter.WriteFixed(
            counted,
            new MemoryStream(),
            Deterministic(256 * 1024)).Ok);

        // Two full buffers, plus the read that reports the end of each one is not
        // needed - MemoryStream returns everything asked for in a single call.
        Assert.Equal(2, counted.Reads);
        Assert.Equal(payload.Length, counted.BytesRead);
    }

    [Fact]
    public void ReportsProgressFromZeroToTheWholeFileIncludingTheFooter()
    {
        byte[] payload = Pattern(4 * 1024);
        List<VhdWriteProgress> reports = [];

        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            new MemoryStream(),
            Deterministic(1024),
            new SynchronousProgress<VhdWriteProgress>(reports.Add));

        Assert.True(written.TryGetValue(out VhdWriteResult? result));

        // One zero report, four buffers, one footer.
        Assert.Equal(6, reports.Count);
        Assert.Equal(0, reports[0].BytesWritten);
        Assert.All(reports, report => Assert.Equal(result.TotalBytes, report.TotalBytes));

        for (int index = 1; index < reports.Count; index++)
        {
            Assert.True(
                reports[index].BytesWritten > reports[index - 1].BytesWritten,
                "Progress must be monotonically increasing.");
        }

        Assert.Equal(result.TotalBytes, reports[^1].BytesWritten);
        Assert.True(reports[^1].IsComplete);
        Assert.Equal(100d, reports[^1].Percentage, 6);
    }

    [Fact]
    public void ReportsNothingAndStillSucceedsWithoutACallback()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(Pattern(2048), writable: false),
            new MemoryStream(),
            Deterministic(),
            progress: null);

        Assert.True(written.Ok);
    }

    [Fact]
    public void RefusesAnImageThatDecodesToNothing()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream([], writable: false),
            new MemoryStream(),
            Deterministic());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, written.Error.Code);
    }

    [Fact]
    public void RefusesAnUnseekableSource()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new UnseekableStream(Pattern(1024)),
            new MemoryStream(),
            Deterministic());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void RefusesAnUnwritableDestination()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(Pattern(1024), writable: false),
            new MemoryStream(new byte[16], writable: false),
            Deterministic());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4097)]
    [InlineData(128 * 1024 * 1024)]
    public void RefusesABufferThatIsNotAWholeNumberOfSectorsInRange(int bufferSize)
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(Pattern(1024), writable: false),
            new MemoryStream(),
            new VhdWriteOptions { BufferSize = bufferSize });

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void ReportsTheImageErrorWhenTheSourceEndsEarly()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new TruncatedStream(Pattern(4096), servedBytes: 1024),
            new MemoryStream(),
            Deterministic(512));

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, written.Error.Code);
    }

    [Fact]
    public void CarriesTheBlockStreamsOwnErrorThroughUnchanged()
    {
        DmgError inner = DmgError.Corrupt("A chunk declares a codec this build cannot decode.", "type 0x80000006");

        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new ThrowingStream(4096, new DmgStreamException(inner)),
            new MemoryStream(),
            Deterministic(512));

        Assert.False(written.Ok);
        Assert.Same(inner, written.Error);
    }

    [Fact]
    public void ReportsAFullDiskAsInsufficientSpace()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new MemoryStream(Pattern(4096), writable: false),
            new FullDiskStream(),
            Deterministic(512));

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InsufficientSpace, written.Error.Code);
    }

    [Fact]
    public void ThrowsWhenCancelledRatherThanReportingAHalfWrittenFileAsAFailure()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => VhdWriter.WriteFixed(
            new MemoryStream(Pattern(4096), writable: false),
            new MemoryStream(),
            Deterministic(512),
            progress: null,
            cancellation.Token));
    }

    [Fact]
    public void RefusesAnImageLargerThanAVhdCanDescribe()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteFixed(
            new HugeStream(VhdFooter.MaxDiskSize + 512),
            new MemoryStream(),
            Deterministic());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, written.Error.Code);
    }

    [Fact]
    public void PredictsTheFinishedFileSizeBeforeWritingIt()
    {
        Assert.Equal(512 + 512, VhdWriter.FixedFileSizeFor(512));
        Assert.Equal(1024 + 512, VhdWriter.FixedFileSizeFor(513));
        Assert.Equal(512, VhdWriter.FixedFileSizeFor(0));

        byte[] payload = Pattern(3000);
        MemoryStream destination = new();

        Assert.True(VhdWriter.WriteFixed(
            new MemoryStream(payload, writable: false),
            destination,
            Deterministic()).Ok);

        Assert.Equal(VhdWriter.FixedFileSizeFor(payload.Length), destination.Length);
    }

    /// <summary>Reports on the calling thread, so a test can assert on the reports.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Claims one length and serves fewer bytes: a truncated image.</summary>
    private sealed class TruncatedStream(byte[] bytes, int servedBytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            long left = servedBytes - Position;

            return left <= 0
                ? 0
                : base.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        }
    }

    /// <summary>Throws a chosen exception on the first read.</summary>
    private sealed class ThrowingStream(long length, Exception exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override int Read(Span<byte> buffer) => throw exception;

        public override long Seek(long offset, SeekOrigin origin) => Position;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A destination whose every write fails the way a full disk does.</summary>
    private sealed class FullDiskStream : Stream
    {
        private static readonly int OutOfSpaceHResult = OperatingSystem.IsWindows()
            ? unchecked((int)0x8007_0070)
            : 28;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("There is not enough space on the disk.") { HResult = OutOfSpaceHResult };
    }

    /// <summary>Claims a length no VHD can describe, without allocating it.</summary>
    private sealed class HugeStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override int Read(Span<byte> buffer) => 0;

        public override long Seek(long offset, SeekOrigin origin) => Position;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
