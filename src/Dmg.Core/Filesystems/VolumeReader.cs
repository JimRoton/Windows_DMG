using Dmg.Core.Imaging;

namespace Dmg.Core.Filesystems;

/// <summary>
/// A window onto one volume: reads addressed from the start of the partition, and
/// clipped to it.
/// </summary>
/// <remarks>
/// A filesystem's own structures are all relative to the volume, and every one of
/// them is a number a hostile image controls. Routing them through here means a
/// FAT that points a hundred megabytes past the end of its partition is a refusal
/// with an offset in it, rather than a read that quietly wanders into the next
/// volume and comes back with plausible-looking bytes.
/// </remarks>
internal sealed class VolumeReader
{
    private readonly Stream _disk;

    /// <summary>Opens a window onto the volume at <paramref name="offset"/>.</summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="offset">The volume's byte offset from the start of the disk.</param>
    /// <param name="length">The volume's length in bytes.</param>
    internal VolumeReader(Stream disk, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _disk = disk;
        Offset = offset;
        Length = length;
    }

    /// <summary>The volume's byte offset from the start of the disk.</summary>
    internal long Offset { get; }

    /// <summary>The volume's length in bytes.</summary>
    internal long Length { get; }

    /// <summary>Reads <paramref name="length"/> bytes from <paramref name="offset"/> within the volume.</summary>
    /// <param name="offset">The offset from the start of the volume.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    internal Result<byte[]> Read(long offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"This volume's {what} lies outside the volume.",
                $"offset={offset}, length={length}, volume={Length} bytes."));
        }

        if (offset > long.MaxValue - Offset)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"This volume's {what} is at an impossible offset.",
                $"offset={offset} from a volume at {Offset}."));
        }

        return ReadDisk(Offset + offset, length, what);
    }

    /// <summary>Reads from the disk, converting a decode failure into a result.</summary>
    /// <param name="offset">The absolute byte offset.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    private Result<byte[]> ReadDisk(long offset, int length, string what)
    {
        try
        {
            _disk.Position = offset;
            byte[] buffer = new byte[length];
            _disk.ReadExactly(buffer);
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
                $"The image ends before this volume's {what} does.",
                $"offset={offset}, length={length}. {failure.Message}"));
        }
        catch (IOException failure)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"The image could not be read where this volume's {what} should be.",
                $"offset={offset}, length={length}. {failure.Message}"));
        }
    }
}
