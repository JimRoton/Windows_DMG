using Dmg.Core.Partitions;

namespace Dmg.Core.Filesystems;

/// <summary>
/// The one entry point: hand it a volume, get back what filesystem is in it.
/// </summary>
/// <remarks>
/// <para>
/// An unrecognised volume is a successful probe carrying
/// <see cref="FilesystemKind.Unknown"/>, not a failure. <c>dmg info</c> has to be
/// able to list a partition it cannot identify - that is information the user
/// wants - and refusing to mount it is a decision for the layer that knows
/// whether the user asked for that partition. A failure here means something else
/// entirely: the volume said it was exFAT and then contradicted itself, or the
/// image would not decode.
/// </para>
/// </remarks>
public static class FilesystemProbe
{
    /// <summary>Identifies the filesystem in a partition.</summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="partition">The partition to probe.</param>
    public static Result<FilesystemInfo> Probe(Stream disk, PartitionEntry partition)
    {
        ArgumentNullException.ThrowIfNull(partition);

        return Probe(disk, partition.ByteOffset, partition.ByteLength);
    }

    /// <summary>Identifies the filesystem in a region of the disk.</summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="offset">The volume's byte offset from the start of the disk.</param>
    /// <param name="length">The volume's length in bytes.</param>
    public static Result<FilesystemInfo> Probe(Stream disk, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (offset < 0 || length <= 0)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This image describes a volume of an impossible size or position.",
                $"offset={offset}, length={length}."));
        }

        VolumeReader volume = new(disk, offset, length);

        int window = (int)Math.Min(length, FilesystemSignature.WindowBytes);
        Result<byte[]> read = volume.Read(0, window, "start");

        if (!read.TryGetValue(out byte[]? head))
        {
            return read.CastFailure<FilesystemInfo>();
        }

        FilesystemKind kind = FilesystemSignature.Recognize(head);

        return kind switch
        {
            FilesystemKind.ExFat => ExFatProbe.Probe(volume, head),
            FilesystemKind.Unknown => Result<FilesystemInfo>.Success(
                new FilesystemInfo { Kind = FilesystemKind.Unknown }),
            _ => Result<FilesystemInfo>.Success(new FilesystemInfo { Kind = kind }),
        };
    }
}
