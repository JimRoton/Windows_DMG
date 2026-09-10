using Dmg.Core.Imaging;

namespace Dmg.Core.Partitions;

/// <summary>
/// Bounds-checked reads against a decoded disk.
/// </summary>
/// <remarks>
/// <para>
/// Everything above this point treats the disk as an array of sectors written by
/// somebody else. A partition table can point anywhere - past the end of the
/// image, at an offset whose arithmetic overflows, at a length that would allocate
/// a gigabyte - so every read is checked before it is attempted and a refused read
/// comes back as a <see cref="DmgExitCode.CorruptImage"/> failure rather than an
/// exception.
/// </para>
/// <para>
/// The stream underneath is usually <see cref="DmgBlockStream"/>, which throws
/// <see cref="DmgStreamException"/> when a chunk will not decode. That is caught
/// here and turned back into a result carrying the same exit code, so a partition
/// scan over an image with one undecodable chunk fails like a parse failure and
/// not like a bug.
/// </para>
/// </remarks>
internal static class DiskIo
{
    /// <summary>The sector size everything in this layer assumes.</summary>
    internal const int BytesPerSector = 512;

    /// <summary>The largest single read this layer will attempt, as a sanity cap.</summary>
    internal const int MaxReadBytes = 4 * 1024 * 1024;

    /// <summary>Reads exactly <paramref name="length"/> bytes at <paramref name="offset"/>.</summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="offset">Byte offset from the start of the disk.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    internal static Result<byte[]> Read(Stream disk, long offset, int length, string what)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (offset < 0 || length < 0 || length > MaxReadBytes)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image describes {what} with an impossible size or position.",
                $"offset={offset}, length={length}."));
        }

        if (length == 0)
        {
            return Result<byte[]>.Success([]);
        }

        if (disk.CanSeek && offset > disk.Length - length)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image describes {what} past the end of the disk.",
                $"offset={offset}, length={length}, disk={disk.Length} bytes."));
        }

        try
        {
            disk.Position = offset;
            byte[] buffer = new byte[length];
            disk.ReadExactly(buffer);
            return Result<byte[]>.Success(buffer);
        }
        catch (DmgStreamException failure)
        {
            return Result<byte[]>.Failure(
                failure.Error.Code,
                failure.Error.Message,
                $"While reading {what} at offset {offset}. {failure.Error.Detail}");
        }
        catch (EndOfStreamException failure)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image ends before {what} does.",
                $"offset={offset}, length={length}. {failure.Message}"));
        }
        catch (IOException failure)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image could not be read where {what} should be.",
                $"offset={offset}, length={length}. {failure.Message}"));
        }
    }

    /// <summary>Reads whole sectors, refusing counts whose arithmetic would overflow.</summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="firstSector">The first sector to read.</param>
    /// <param name="sectorCount">How many sectors to read.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    internal static Result<byte[]> ReadSectors(Stream disk, long firstSector, int sectorCount, string what)
    {
        if (firstSector < 0 || sectorCount < 0 || firstSector > long.MaxValue / BytesPerSector)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image places {what} at an impossible sector.",
                $"sector={firstSector}, count={sectorCount}."));
        }

        return Read(disk, firstSector * BytesPerSector, sectorCount * BytesPerSector, what);
    }

    /// <summary>The disk's length in whole sectors, or zero when it cannot be measured.</summary>
    internal static ulong SectorCount(Stream disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.CanSeek && disk.Length > 0 ? (ulong)(disk.Length / BytesPerSector) : 0;
    }
}
