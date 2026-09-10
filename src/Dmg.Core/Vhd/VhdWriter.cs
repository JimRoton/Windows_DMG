using System.Buffers;
using Dmg.Core.Imaging;

namespace Dmg.Core.Vhd;

/// <summary>
/// Streams a decoded disk image into a fixed <c>.vhd</c>: the payload verbatim,
/// then the 512-byte footer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer never holds the image in memory.</b> It copies a buffer at a time
/// from whatever <see cref="Stream"/> it was handed - in the product, a
/// <c>DmgBlockStream</c>; in the tests, a <see cref="MemoryStream"/> - straight to
/// the destination. A 40 GB image costs 40 GB of scratch and one megabyte of RAM.
/// </para>
/// <para>
/// <b>Why one megabyte and not four kilobytes.</b> The default copy buffer is a
/// UDIF chunk, not a memory page. The source decodes a whole chunk per read and
/// caches exactly one, so a 4 KiB buffer would ask for the same chunk hundreds of
/// times over. See <see cref="VhdWriteOptions.DefaultBufferSize"/>.
/// </para>
/// <para>
/// <b>Failures are values.</b> A short source, a full disk, an unwritable
/// destination - all come back as a failed <see cref="Result{T}"/> carrying the
/// exit code the process should return. The one exception is
/// <see cref="OperationCanceledException"/>: cancellation is the caller's own
/// doing and is thrown, not reported, so a half-written scratch file is never
/// mistaken for a finished VHD.
/// </para>
/// <para>
/// This type is portable. It has no Windows dependency and its whole test suite
/// runs on macOS.
/// </para>
/// </remarks>
public static class VhdWriter
{
    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> as a
    /// fixed VHD, reporting progress as it goes.
    /// </summary>
    /// <param name="source">
    /// The decoded disk. Must be readable and seekable; it is read from byte zero
    /// to its <see cref="Stream.Length"/>, whatever position it was left at.
    /// </param>
    /// <param name="destination">
    /// The file to write. Must be writable, and is written from its current
    /// position.
    /// </param>
    /// <param name="options">The copy buffer and footer fields; null for the defaults.</param>
    /// <param name="progress">
    /// Called once before the first byte with a zero report, once per buffer
    /// written, and once at the end with <c>BytesWritten == TotalBytes</c>. Null to
    /// report nothing.
    /// </param>
    /// <param name="cancellationToken">Checked once per buffer.</param>
    /// <returns>
    /// What was written, or the failure: <see cref="DmgExitCode.UnsupportedFormat"/>
    /// for a disk size a VHD cannot describe, <see cref="DmgExitCode.CorruptImage"/>
    /// for a source that ends early, <see cref="DmgExitCode.InsufficientSpace"/> for
    /// a destination that ran out of room.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Result<VhdWriteResult> WriteFixed(
        Stream source,
        Stream destination,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        VhdWriteOptions settings = options ?? VhdWriteOptions.Default;

        Result<VhdWriteOptions> validated = settings.Validate();

        if (!validated.Ok)
        {
            return validated.CastFailure<VhdWriteResult>();
        }

        Result<long> measured = MeasurePayload(source, destination);

        if (!measured.TryGetValue(out long sourceLength))
        {
            return measured.CastFailure<VhdWriteResult>();
        }

        long diskSize = RoundUpToSector(sourceLength);

        Result<VhdFooter> built = VhdFooter.ForFixedDisk(
            diskSize,
            settings.UniqueId ?? Guid.NewGuid(),
            settings.CreatedUtc ?? DateTimeOffset.UtcNow,
            settings.CreatorApplication,
            settings.CreatorHostOs);

        if (!built.TryGetValue(out VhdFooter? footer))
        {
            return built.CastFailure<VhdWriteResult>();
        }

        long totalBytes = diskSize + VhdFooter.Length;
        ProgressReporter reporter = new(progress, totalBytes);

        reporter.Start();

        Result copied = CopyPayload(
            source,
            destination,
            sourceLength,
            diskSize,
            settings.BufferSize,
            ref reporter,
            cancellationToken);

        if (!copied.Ok)
        {
            return copied.CastFailure<VhdWriteResult>();
        }

        Result stamped = WriteFooter(destination, footer, ref reporter);

        if (!stamped.Ok)
        {
            return stamped.CastFailure<VhdWriteResult>();
        }

        if (settings.FlushWhenDone)
        {
            Result flushed = Flush(destination);

            if (!flushed.Ok)
            {
                return flushed.CastFailure<VhdWriteResult>();
            }
        }

        return Result<VhdWriteResult>.Success(
            new VhdWriteResult(footer, diskSize, totalBytes, sourceLength));
    }

    /// <summary>
    /// The size the finished fixed VHD will be, for a payload of
    /// <paramref name="payloadBytes"/>: the payload rounded up to a whole sector,
    /// plus the footer.
    /// </summary>
    /// <remarks>
    /// The free-space precheck needs this number before anything is written, and
    /// so does a progress bar. It is a pure function of the payload size.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadBytes"/> is negative.</exception>
    public static long FixedFileSizeFor(long payloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);

        return checked(RoundUpToSector(payloadBytes) + VhdFooter.Length);
    }

    /// <summary>Rounds a byte count up to the next whole 512-byte sector.</summary>
    internal static long RoundUpToSector(long bytes)
    {
        long remainder = bytes % VhdFooter.SectorSize;

        return remainder == 0
            ? bytes
            : checked(bytes + (VhdFooter.SectorSize - remainder));
    }

    /// <summary>
    /// Checks the two streams and works out how many bytes there are to copy.
    /// </summary>
    private static Result<long> MeasurePayload(Stream source, Stream destination)
    {
        if (!source.CanRead || !source.CanSeek)
        {
            return Result<long>.Failure(DmgError.Internal(
                "A VHD is written from a readable, seekable image stream.",
                $"CanRead={source.CanRead}, CanSeek={source.CanSeek}"));
        }

        if (!destination.CanWrite)
        {
            return Result<long>.Failure(DmgError.Internal(
                "A VHD must be written to a writable stream.",
                "the destination stream reports CanWrite=false"));
        }

        long length;

        try
        {
            length = source.Length;
            source.Position = 0;
        }
        catch (DmgStreamException stream)
        {
            return Result<long>.Failure(stream.Error);
        }
        catch (IOException exception)
        {
            return Result<long>.Failure(DmgError.Internal(
                "The image stream could not be measured.",
                exception.Message));
        }

        if (length <= 0)
        {
            return Result<long>.Failure(DmgError.Unsupported(
                "There is nothing to write: the image decodes to no data at all.",
                $"source length {length} bytes"));
        }

        return Result<long>.Success(length);
    }

    /// <summary>
    /// Copies the payload a buffer at a time, padding the last partial sector with
    /// zeros so the file ends on a sector boundary.
    /// </summary>
    private static Result CopyPayload(
        Stream source,
        Stream destination,
        long sourceLength,
        long diskSize,
        int bufferSize,
        ref ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            long remaining = sourceLength;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int wanted = (int)Math.Min(bufferSize, remaining);
                Result<int> filled = Fill(source, buffer.AsSpan(0, wanted));

                if (!filled.TryGetValue(out int read))
                {
                    return filled.Discard();
                }

                if (read == 0)
                {
                    return Result.Failure(DmgError.Corrupt(
                        "The image ended before the size it declared.",
                        $"{remaining} bytes short of {sourceLength}"));
                }

                Result written = Write(destination, buffer.AsSpan(0, read));

                if (!written.Ok)
                {
                    return written;
                }

                remaining -= read;
                reporter.Advance(read);
            }

            long padding = diskSize - sourceLength;

            if (padding > 0)
            {
                // A source that does not end on a sector boundary cannot be a whole
                // disk, but it is not worth refusing: zero-filling the tail sector
                // is what every imaging tool does and it keeps the geometry honest.
                Span<byte> tail = buffer.AsSpan(0, (int)padding);
                tail.Clear();

                Result padded = Write(destination, tail);

                if (!padded.Ok)
                {
                    return padded;
                }

                reporter.Advance(padding);
            }

            return Result.Success();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Appends the footer and reports the final progress.</summary>
    private static Result WriteFooter(Stream destination, VhdFooter footer, ref ProgressReporter reporter)
    {
        Span<byte> bytes = stackalloc byte[VhdFooter.Length];
        footer.WriteTo(bytes);

        Result written = Write(destination, bytes);

        if (!written.Ok)
        {
            return written;
        }

        reporter.Advance(VhdFooter.Length);

        return Result.Success();
    }

    /// <summary>
    /// Reads until the span is full or the source is exhausted, returning how many
    /// bytes arrived.
    /// </summary>
    private static Result<int> Fill(Stream source, Span<byte> destination)
    {
        int total = 0;

        try
        {
            while (total < destination.Length)
            {
                int read = source.Read(destination[total..]);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (DmgStreamException stream)
        {
            // The block stream reports a malformed image this way: the error inside
            // already carries the right exit code and a message about the image.
            return Result<int>.Failure(stream.Error);
        }
        catch (IOException exception)
        {
            return Result<int>.Failure(DmgError.Internal(
                "The image could not be read.",
                exception.Message));
        }

        return Result<int>.Success(total);
    }

    private static Result Write(Stream destination, ReadOnlySpan<byte> bytes)
    {
        try
        {
            destination.Write(bytes);
            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DescribeWriteFailure(exception, bytes.Length));
        }
    }

    private static Result Flush(Stream destination)
    {
        try
        {
            destination.Flush();
            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DescribeWriteFailure(exception, bytes: 0));
        }
    }

    /// <summary>
    /// Turns a write failure into the right exit code. A full disk is
    /// <see cref="DmgExitCode.InsufficientSpace"/> even though the precheck should
    /// have caught it - something else on the machine can eat the space while we
    /// are writing, and "out of space" is far more use to the user than "internal
    /// error".
    /// </summary>
    private static DmgError DescribeWriteFailure(Exception exception, int bytes)
    {
        string where = bytes > 0 ? $"writing {bytes} bytes: " : string.Empty;

        return IsOutOfSpace(exception)
            ? new DmgError(
                DmgExitCode.InsufficientSpace,
                "The disk filled up while the VHD was being written.",
                where + exception.Message)
            : DmgError.Internal(
                "The VHD could not be written.",
                where + exception.Message);
    }

    /// <summary>
    /// Whether an I/O failure was the disk running out of room.
    /// </summary>
    /// <remarks>
    /// There is no portable exception for this, so the underlying error number is
    /// what has to be examined: <c>ENOSPC</c> on Unix, <c>ERROR_DISK_FULL</c> or
    /// <c>ERROR_HANDLE_DISK_FULL</c> on Windows. Getting this wrong costs a less
    /// helpful message, never correctness, which is why it is a heuristic and not
    /// a contract.
    /// </remarks>
    private static bool IsOutOfSpace(Exception exception)
    {
        const int ENOSPC = 28;
        const int ErrorHandleDiskFull = 39;
        const int ErrorDiskFull = 112;

        int code = exception.HResult & 0xFFFF;

        return OperatingSystem.IsWindows()
            ? code is ErrorDiskFull or ErrorHandleDiskFull
            : code == ENOSPC;
    }

    /// <summary>
    /// Keeps the running total and hands it to the caller's callback.
    /// </summary>
    /// <remarks>
    /// A struct so that a write with no progress callback allocates nothing at all
    /// for the reporting it is not doing.
    /// </remarks>
    private struct ProgressReporter(IProgress<VhdWriteProgress>? progress, long totalBytes)
    {
        private readonly IProgress<VhdWriteProgress>? _progress = progress;
        private readonly long _totalBytes = totalBytes;
        private long _written;

        /// <summary>Reports zero, so a display can be drawn before anything is written.</summary>
        public readonly void Start() =>
            _progress?.Report(new VhdWriteProgress(0, _totalBytes));

        /// <summary>Adds to the running total and reports it.</summary>
        public void Advance(long bytes)
        {
            _written += bytes;
            _progress?.Report(new VhdWriteProgress(_written, _totalBytes));
        }
    }
}
