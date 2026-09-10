using System.Buffers;
using System.Buffers.Binary;
using Dmg.Core.Imaging;

namespace Dmg.Core.Vhd;

/// <summary>
/// Streams a decoded disk image into a <c>.vhd</c>: fixed by default - the payload
/// verbatim, then the 512-byte footer - or dynamic behind a flag, which leaves out
/// every block that is entirely zeros.
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
/// <b>Fixed is the default and dynamic is opt-in.</b> A fixed VHD has nothing in
/// it to get wrong: no allocation table, no per-block bitmap, and a half-written
/// file that is obviously half-written. A dynamic VHD is worth the extra structure
/// only when the saving is real - a 48 MiB volume with almost nothing on it costs
/// half a megabyte of scratch instead of 48 - so it is behind
/// <see cref="VhdWriteOptions.DiskType"/> until it has been exercised against a
/// real Windows VHD provider. See <see cref="WriteDynamic"/>.
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
    /// Writes <paramref name="source"/> to the file at <paramref name="path"/> as a
    /// fixed VHD, refusing before the first byte if the volume has no room for it.
    /// </summary>
    /// <param name="source">The decoded disk. Readable and seekable.</param>
    /// <param name="path">
    /// The file to create, overwriting anything already there. In the product this
    /// is <c>ScratchSpace.VhdPath</c> - a path built from a generated mount id, not
    /// from anything the image said.
    /// </param>
    /// <param name="options">The copy buffer and footer fields; null for the defaults.</param>
    /// <param name="progress">The progress callback, or null.</param>
    /// <param name="freeSpaceProbe">The volume probe for the precheck; null for the real one.</param>
    /// <param name="cancellationToken">Checked once per buffer.</param>
    /// <returns>
    /// What was written, or the failure. A volume without room comes back as
    /// <see cref="DmgExitCode.InsufficientSpace"/> naming both figures, and nothing
    /// is created at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Nothing is left behind on failure.</b> A partly written VHD is worse than
    /// no VHD: it is a file of the right name that Windows will happily try to
    /// attach. If any step fails, the file is deleted before the failure is
    /// returned.
    /// </para>
    /// <para>
    /// The file is preallocated at its final size, so a volume that fills between
    /// the precheck and the write fails immediately rather than forty minutes in,
    /// and the result is one extent rather than ten thousand.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Result<VhdWriteResult> WriteFixedToFile(
        Stream source,
        string path,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        IFreeSpaceProbe? freeSpaceProbe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Result<long> measured = MeasureSource(source);

        if (!measured.TryGetValue(out long sourceLength))
        {
            return measured.CastFailure<VhdWriteResult>();
        }

        long fileSize = FixedFileSizeFor(sourceLength);

        return ToFile(
            path,
            fileSize,
            preallocationSize: fileSize,
            freeSpaceProbe,
            destination => WriteFixed(source, destination, options, progress, cancellationToken));
    }

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> as a
    /// dynamic (sparse) VHD: a block allocation table, then only those blocks that
    /// are not entirely zeros.
    /// </summary>
    /// <param name="source">The decoded disk. Readable and seekable.</param>
    /// <param name="destination">
    /// The file to write. Must be writable and <b>seekable</b>, and must be
    /// positioned at byte zero: every table entry is an absolute sector number, so
    /// a dynamic VHD cannot begin part-way into a file.
    /// </param>
    /// <param name="options">
    /// The block size and the footer fields; null for the defaults. The disk type
    /// on the options is not consulted here - calling this method <i>is</i> the
    /// request for a dynamic disk.
    /// </param>
    /// <param name="progress">The progress callback, or null.</param>
    /// <param name="sparseMap">
    /// An optional map of the disk's known-zero ranges. Purely an optimisation:
    /// the same file comes out either way, but with a map the writer never reads
    /// - or decompresses - the parts of the image it is going to discard.
    /// </param>
    /// <param name="cancellationToken">Checked once per block.</param>
    /// <returns>What was written, or the failure.</returns>
    /// <remarks>
    /// <para>
    /// <b>A block is left out when it reads back as zeros, whatever the map says.</b>
    /// The map is consulted first because it is free; a block it does not vouch for
    /// is still read and still tested. That is what makes the map an optimisation
    /// rather than a correctness dependency, and it means a raw chunk that happens
    /// to be full of zeros costs nothing either.
    /// </para>
    /// <para>
    /// <b>The table is written twice.</b> Once as all-ones placeholders before the
    /// blocks, so the file has its final shape from the start, and once for real
    /// afterwards - which is why the destination has to be seekable. Writing it
    /// only at the end would leave a file whose header points at a hole for the
    /// whole of the conversion.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Result<VhdWriteResult> WriteDynamic(
        Stream source,
        Stream destination,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        IVhdSparseMap? sparseMap = null,
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

        if (!destination.CanSeek)
        {
            return Result<VhdWriteResult>.Failure(DmgError.Internal(
                "A dynamic VHD must be written to a seekable destination.",
                "the block allocation table is filled in after the blocks it points at"));
        }

        Result<long> measured = MeasurePayload(source, destination);

        if (!measured.TryGetValue(out long sourceLength))
        {
            return measured.CastFailure<VhdWriteResult>();
        }

        if (destination.Position != 0)
        {
            return Result<VhdWriteResult>.Failure(DmgError.Internal(
                "A dynamic VHD must start at the beginning of its file.",
                $"the destination is at byte {destination.Position}, and every table "
                + "entry is an absolute sector number"));
        }

        long diskSize = RoundUpToSector(sourceLength);

        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(diskSize, settings.BlockSize);

        if (!planned.TryGetValue(out VhdDynamicLayout layout))
        {
            return planned.CastFailure<VhdWriteResult>();
        }

        Result<VhdFooter> built = VhdFooter.ForDynamicDisk(
            diskSize,
            (ulong)VhdDynamicLayout.HeaderOffset,
            settings.UniqueId ?? Guid.NewGuid(),
            settings.CreatedUtc ?? DateTimeOffset.UtcNow,
            settings.CreatorApplication,
            settings.CreatorHostOs);

        if (!built.TryGetValue(out VhdFooter? footer))
        {
            return built.CastFailure<VhdWriteResult>();
        }

        Result<VhdDynamicHeader> headed = VhdDynamicHeader.Create(
            (ulong)VhdDynamicLayout.TableOffset,
            layout.BlockCount,
            layout.BlockSize);

        if (!headed.TryGetValue(out VhdDynamicHeader? header))
        {
            return headed.CastFailure<VhdWriteResult>();
        }

        // The denominator is the finished file at its largest useful reading: the
        // metadata plus every byte of disk considered. Blocks that are skipped are
        // still counted as done, so the bar advances at a steady rate rather than
        // leaping through the empty parts of the image.
        ProgressReporter reporter = new(progress, checked(layout.MetadataBytes + diskSize));

        reporter.Start();

        return WriteDynamicBody(
            source,
            destination,
            sourceLength,
            diskSize,
            layout,
            footer,
            header,
            settings,
            ref reporter,
            sparseMap,
            cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="source"/> to the file at <paramref name="path"/> as a
    /// dynamic VHD, refusing before the first byte if the volume has no room.
    /// </summary>
    /// <param name="source">The decoded disk. Readable and seekable.</param>
    /// <param name="path">The file to create, overwriting anything already there.</param>
    /// <param name="options">The block size and the footer fields; null for the defaults.</param>
    /// <param name="progress">The progress callback, or null.</param>
    /// <param name="freeSpaceProbe">The volume probe for the precheck; null for the real one.</param>
    /// <param name="sparseMap">The optional map of known-zero ranges.</param>
    /// <param name="cancellationToken">Checked once per block.</param>
    /// <returns>What was written, or the failure. Nothing is left behind on failure.</returns>
    /// <remarks>
    /// <b>The precheck is only as good as the map.</b> Without one there is no way
    /// to know how many blocks will be allocated before reading the image, so the
    /// worst case - every block allocated, which is larger than the fixed VHD - is
    /// what has to be insisted on. With a map the figure is the real one, give or
    /// take the blocks that turn out to be zeros without the map having said so.
    /// That is the practical reason to pass a map even on a fast volume: without
    /// it, dynamic writing asks for more free space than fixed writing does.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static Result<VhdWriteResult> WriteDynamicToFile(
        Stream source,
        string path,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        IFreeSpaceProbe? freeSpaceProbe = null,
        IVhdSparseMap? sparseMap = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        VhdWriteOptions settings = options ?? VhdWriteOptions.Default;

        Result<long> measured = MeasureSource(source);

        if (!measured.TryGetValue(out long sourceLength))
        {
            return measured.CastFailure<VhdWriteResult>();
        }

        Result<long> sized = DynamicFileSizeFor(sourceLength, settings.BlockSize, sparseMap);

        if (!sized.TryGetValue(out long fileSize))
        {
            return sized.CastFailure<VhdWriteResult>();
        }

        return ToFile(
            path,
            fileSize,

            // Deliberately not preallocated. A dynamic VHD's whole purpose is to
            // occupy less than its declared capacity, and preallocating would hand
            // the space straight back to the filesystem's allocator.
            preallocationSize: 0,
            freeSpaceProbe,
            destination => WriteDynamic(source, destination, settings, progress, sparseMap, cancellationToken));
    }

    /// <summary>
    /// Writes a fixed or a dynamic VHD according to
    /// <see cref="VhdWriteOptions.DiskType"/>.
    /// </summary>
    /// <remarks>
    /// The dispatcher the product calls, so that "which kind of VHD" is a setting
    /// and not a different code path at every call site. The default is
    /// <see cref="VhdDiskType.Fixed"/>; dynamic is opt-in.
    /// </remarks>
    public static Result<VhdWriteResult> Write(
        Stream source,
        Stream destination,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        IVhdSparseMap? sparseMap = null,
        CancellationToken cancellationToken = default) =>
        (options ?? VhdWriteOptions.Default).DiskType == VhdDiskType.Dynamic
            ? WriteDynamic(source, destination, options, progress, sparseMap, cancellationToken)
            : WriteFixed(source, destination, options, progress, cancellationToken);

    /// <summary>
    /// Writes a fixed or a dynamic VHD to a file according to
    /// <see cref="VhdWriteOptions.DiskType"/>, with the free-space precheck.
    /// </summary>
    public static Result<VhdWriteResult> WriteToFile(
        Stream source,
        string path,
        VhdWriteOptions? options = null,
        IProgress<VhdWriteProgress>? progress = null,
        IFreeSpaceProbe? freeSpaceProbe = null,
        IVhdSparseMap? sparseMap = null,
        CancellationToken cancellationToken = default) =>
        (options ?? VhdWriteOptions.Default).DiskType == VhdDiskType.Dynamic
            ? WriteDynamicToFile(source, path, options, progress, freeSpaceProbe, sparseMap, cancellationToken)
            : WriteFixedToFile(source, path, options, progress, freeSpaceProbe, cancellationToken);

    /// <summary>
    /// How large the dynamic VHD for a <paramref name="payloadBytes"/>-byte image
    /// will be, at worst.
    /// </summary>
    /// <param name="payloadBytes">The decoded image's size.</param>
    /// <param name="blockSize">The block size to be used.</param>
    /// <param name="sparseMap">
    /// The known-zero map, or null. With one the answer is the real size, bar the
    /// blocks that turn out to be zeros without the map having said so; without
    /// one it is the worst case, every block allocated.
    /// </param>
    /// <returns>The size in bytes, or the failure that makes the request unwritable.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadBytes"/> is negative.</exception>
    public static Result<long> DynamicFileSizeFor(
        long payloadBytes,
        int blockSize = VhdDynamicHeader.DefaultBlockSize,
        IVhdSparseMap? sparseMap = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);

        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(RoundUpToSector(payloadBytes), blockSize);

        if (!planned.TryGetValue(out VhdDynamicLayout layout))
        {
            return planned.CastFailure<long>();
        }

        if (sparseMap is null)
        {
            return Result<long>.Success(layout.MaximumFileSize);
        }

        long allocatable = 0;

        for (uint index = 0; index < layout.BlockCount; index++)
        {
            if (!sparseMap.IsKnownZero((long)index * layout.BlockSize, layout.DiskBytesInBlock(index)))
            {
                allocatable++;
            }
        }

        return Result<long>.Success(layout.FileSizeFor(allocatable));
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
        if (!destination.CanWrite)
        {
            return Result<long>.Failure(DmgError.Internal(
                "A VHD must be written to a writable stream.",
                "the destination stream reports CanWrite=false"));
        }

        return MeasureSource(source);
    }

    /// <summary>
    /// Checks the source and reads its length - the one number the precheck, the
    /// footer and the progress total are all derived from.
    /// </summary>
    private static Result<long> MeasureSource(Stream source)
    {
        if (!source.CanRead || !source.CanSeek)
        {
            return Result<long>.Failure(DmgError.Internal(
                "A VHD is written from a readable, seekable image stream.",
                $"CanRead={source.CanRead}, CanSeek={source.CanSeek}"));
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

                Result written = WriteBytes(destination, buffer.AsSpan(0, read));

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

                Result padded = WriteBytes(destination, tail);

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

        Result written = WriteBytes(destination, bytes);

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

    private static Result WriteBytes(Stream destination, ReadOnlySpan<byte> bytes)
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
    /// Creates the file, hands it to <paramref name="write"/>, and makes sure
    /// nothing is left behind if that does not work out.
    /// </summary>
    /// <param name="path">The file to create, overwriting anything already there.</param>
    /// <param name="requiredBytes">What the free-space precheck must find room for.</param>
    /// <param name="preallocationSize">
    /// The size to reserve up front, or zero not to. A fixed VHD reserves its whole
    /// size so a volume that fills mid-write fails at once and the file lands in one
    /// extent; a dynamic VHD reserves nothing, because occupying less than its
    /// declared capacity is the entire point of it.
    /// </param>
    /// <param name="freeSpaceProbe">The volume probe for the precheck; null for the real one.</param>
    /// <param name="write">Writes the VHD into the stream it is given.</param>
    /// <remarks>
    /// <b>A partly written VHD is worse than no VHD:</b> it is a file of the right
    /// name that Windows will happily try to attach. Every exit from here that is
    /// not a success deletes it, cancellation included.
    /// </remarks>
    private static Result<VhdWriteResult> ToFile(
        string path,
        long requiredBytes,
        long preallocationSize,
        IFreeSpaceProbe? freeSpaceProbe,
        Func<Stream, Result<VhdWriteResult>> write)
    {
        Result room = FreeSpaceCheck.Require(path, requiredBytes, freeSpaceProbe);

        if (!room.Ok)
        {
            return room.CastFailure<VhdWriteResult>();
        }

        Result<VhdWriteResult> written;

        try
        {
            using FileStream destination = new(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,

                // The writer hands over whole buffers, so a buffer inside the
                // FileStream would only copy each one a second time.
                BufferSize = 0,
                PreallocationSize = preallocationSize,
            });

            written = write(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Delete(path);

            return Result<VhdWriteResult>.Failure(DescribeWriteFailure(exception, bytes: 0));
        }
        catch (OperationCanceledException)
        {
            Delete(path);

            throw;
        }

        if (!written.Ok)
        {
            Delete(path);
        }

        return written;
    }

    /// <summary>
    /// The body of a dynamic write: the two headers, a placeholder table, the
    /// blocks that are not all zeros, the footer, and then the table for real.
    /// </summary>
    private static Result<VhdWriteResult> WriteDynamicBody(
        Stream source,
        Stream destination,
        long sourceLength,
        long diskSize,
        VhdDynamicLayout layout,
        VhdFooter footer,
        VhdDynamicHeader header,
        VhdWriteOptions settings,
        ref ProgressReporter reporter,
        IVhdSparseMap? sparseMap,
        CancellationToken cancellationToken)
    {
        uint[] table = new uint[layout.BlockCount];

        table.AsSpan().Fill(VhdDynamicHeader.UnusedBlockEntry);

        byte[] footerBytes = footer.ToArray();

        // The footer is written at both ends. The copy at the head is what lets a
        // reader recover a VHD whose tail was lost, which is the case the
        // specification put it there for.
        Result step = WriteBytes(destination, footerBytes);

        if (!step.Ok)
        {
            return step.CastFailure<VhdWriteResult>();
        }

        step = WriteBytes(destination, header.ToArray());

        if (!step.Ok)
        {
            return step.CastFailure<VhdWriteResult>();
        }

        step = WriteTable(destination, table, layout.TableBytes);

        if (!step.Ok)
        {
            return step.CastFailure<VhdWriteResult>();
        }

        reporter.Advance(layout.FirstBlockOffset);

        int blockSize = layout.BlockSize;
        int bitmapBytes = layout.SectorBitmapBytes;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        byte[] bitmap = ArrayPool<byte>.Shared.Rent(bitmapBytes);

        try
        {
            // Every sector of an allocated block is present, so every bit is set.
            // A bitmap with holes in it would be legal and would mean "these sectors
            // have never been written"; we always write the whole block, so it never
            // has holes, and saying so plainly is what keeps a reader from having to
            // guess.
            bitmap.AsSpan(0, bitmapBytes).Fill(0xFF);

            long nextBlockOffset = layout.FirstBlockOffset;
            long allocated = 0;

            for (uint index = 0; index < layout.BlockCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long blockStart = (long)index * blockSize;
                int inBlock = (int)layout.DiskBytesInBlock(index);

                if (sparseMap is not null && sparseMap.IsKnownZero(blockStart, inBlock))
                {
                    // Never read, never decompressed, never written. This is where a
                    // mostly-empty image stops costing anything.
                    reporter.Advance(inBlock);
                    continue;
                }

                Span<byte> block = buffer.AsSpan(0, inBlock);
                int fromSource = (int)Math.Clamp(sourceLength - blockStart, 0, inBlock);

                if (fromSource > 0)
                {
                    Result sought = Seek(source, blockStart);

                    if (!sought.Ok)
                    {
                        return sought.CastFailure<VhdWriteResult>();
                    }

                    Result<int> filled = Fill(source, block[..fromSource]);

                    if (!filled.TryGetValue(out int read))
                    {
                        return filled.CastFailure<VhdWriteResult>();
                    }

                    if (read < fromSource)
                    {
                        return Result<VhdWriteResult>.Failure(DmgError.Corrupt(
                            "The image ended before the size it declared.",
                            $"{fromSource - read} bytes short at offset {blockStart + read}"));
                    }
                }

                // The tail of a source that did not end on a block boundary, and of
                // one that did not end on a sector boundary either.
                block[fromSource..].Clear();

                if (!block.ContainsAnyExcept((byte)0))
                {
                    // Zeros the map did not know about - a raw chunk of nothing, or
                    // a compressed chunk that decodes to nothing. Just as skippable.
                    reporter.Advance(inBlock);
                    continue;
                }

                table[index] = (uint)(nextBlockOffset / VhdFooter.SectorSize);

                step = WriteBytes(destination, bitmap.AsSpan(0, bitmapBytes));

                if (!step.Ok)
                {
                    return step.CastFailure<VhdWriteResult>();
                }

                step = WriteBytes(destination, block);

                if (!step.Ok)
                {
                    return step.CastFailure<VhdWriteResult>();
                }

                // A final short block still occupies a whole block on disk: the
                // table has no way to express a partial one.
                int tail = blockSize - inBlock;

                if (tail > 0)
                {
                    Span<byte> padding = buffer.AsSpan(0, tail);
                    padding.Clear();

                    step = WriteBytes(destination, padding);

                    if (!step.Ok)
                    {
                        return step.CastFailure<VhdWriteResult>();
                    }
                }

                nextBlockOffset = checked(nextBlockOffset + layout.BlockStride);
                allocated++;
                reporter.Advance(inBlock);
            }

            step = WriteFooter(destination, footer, ref reporter);

            if (!step.Ok)
            {
                return step.CastFailure<VhdWriteResult>();
            }

            long endOfFile = nextBlockOffset + VhdFooter.Length;

            // Back to the table, now that every entry is known.
            step = Seek(destination, VhdDynamicLayout.TableOffset);

            if (!step.Ok)
            {
                return step.CastFailure<VhdWriteResult>();
            }

            step = WriteTable(destination, table, layout.TableBytes);

            if (!step.Ok)
            {
                return step.CastFailure<VhdWriteResult>();
            }

            step = Seek(destination, endOfFile);

            if (!step.Ok)
            {
                return step.CastFailure<VhdWriteResult>();
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
                new VhdWriteResult(footer, diskSize, layout.FileSizeFor(allocated), sourceLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bitmap);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes the block allocation table, padded out to its sector-aligned length.
    /// </summary>
    /// <remarks>
    /// The padding past the last real entry is all ones as well. It is not a block
    /// anybody may allocate, and leaving it as zeros would make it look like a
    /// block living at sector zero - which is where the footer copy is.
    /// </remarks>
    private static Result WriteTable(Stream destination, uint[] table, long tableBytes)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent((int)tableBytes);

        try
        {
            Span<byte> span = bytes.AsSpan(0, (int)tableBytes);

            span.Fill(0xFF);

            for (int index = 0; index < table.Length; index++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    span[(index * VhdDynamicHeader.TableEntryLength)..],
                    table[index]);
            }

            return WriteBytes(destination, span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Moves a stream to an absolute offset, reporting a refusal as a value.</summary>
    private static Result Seek(Stream stream, long offset)
    {
        if (stream.Position == offset)
        {
            return Result.Success();
        }

        try
        {
            stream.Position = offset;

            return Result.Success();
        }
        catch (DmgStreamException image)
        {
            return Result.Failure(image.Error);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentOutOfRangeException)
        {
            return Result.Failure(DmgError.Internal(
                "The VHD could not be positioned where it needed to be written.",
                $"seeking to {offset}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Removes a VHD that was not finished, so no half-written file is left with a
    /// name that invites an attach. A failure to delete is not worth reporting over
    /// the failure that caused it.
    /// </summary>
    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do here: the caller is already returning a failure,
            // and the scratch directory's own cleanup gets another go at it.
        }
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
