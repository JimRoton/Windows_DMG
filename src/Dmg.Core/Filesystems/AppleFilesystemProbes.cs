using System.Buffers.Binary;
using System.Globalization;

namespace Dmg.Core.Filesystems;

/// <summary>
/// HFS+ and APFS: identified precisely, and then refused by name.
/// </summary>
/// <remarks>
/// <para>
/// These are the two filesystems the product exists to say no to, and saying no
/// well is the whole job. A user who is told "unsupported filesystem" learns
/// nothing and tries again; a user who is told the image contains an HFS+ volume
/// knows why Windows cannot mount it and what to do instead. So both are
/// identified exactly - HFS+ and HFSX apart from the original HFS, an APFS
/// container apart from anything else - rather than lumped together as "Apple".
/// </para>
/// <para>
/// <b>Neither volume's name is read.</b> HFS+ keeps it in the catalog B-tree and
/// APFS in a volume superblock reachable only through the object map, and both
/// would mean implementing a large part of a filesystem to print a string on a
/// message that is refusing to mount it. The geometry that is in the superblock is
/// reported; the name is left null rather than guessed at.
/// </para>
/// </remarks>
internal static class AppleFilesystemProbes
{
    /// <summary>The offset of the HFS+ volume header within the volume.</summary>
    internal const int HfsPlusHeaderOffset = 1024;

    /// <summary>The <c>H+</c> signature of an HFS+ volume.</summary>
    internal const ushort HfsPlusSignature = 0x482B;

    /// <summary>The <c>HX</c> signature of an HFSX volume - HFS+ with case-sensitive names.</summary>
    internal const ushort HfsxSignature = 0x4858;

    /// <summary>The <c>BD</c> signature of an original HFS volume.</summary>
    internal const ushort HfsSignature = 0x4244;

    /// <summary>The offset of the APFS container superblock's magic within block 0.</summary>
    internal const int ApfsMagicOffset = 32;

    /// <summary>Reads an HFS+ volume header.</summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="head">The volume's first bytes, covering the header at 1024.</param>
    internal static Result<FilesystemInfo> ProbeHfsPlus(VolumeReader volume, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (head.Length < HfsPlusHeaderOffset + 52)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume is too short to hold an HFS+ volume header.",
                $"{head.Length} bytes available; the header is at offset {HfsPlusHeaderOffset}."));
        }

        ReadOnlySpan<byte> header = head[HfsPlusHeaderOffset..];
        ushort signature = BinaryPrimitives.ReadUInt16BigEndian(header);
        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(header[40..]);
        uint totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(header[44..]);

        FilesystemKind kind = signature switch
        {
            HfsPlusSignature or HfsxSignature => FilesystemKind.HfsPlus,
            HfsSignature => FilesystemKind.Hfs,
            _ => FilesystemKind.Unknown,
        };

        if (kind == FilesystemKind.Unknown)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume claims to be HFS+ but its volume header does not say so.",
                string.Create(CultureInfo.InvariantCulture, $"Signature 0x{signature:X4} at offset {HfsPlusHeaderOffset}.")));
        }

        if (kind == FilesystemKind.HfsPlus
            && (blockSize < 512 || blockSize > (1 << 24) || (blockSize & (blockSize - 1)) != 0))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's HFS+ header declares an impossible allocation block size.",
                string.Create(CultureInfo.InvariantCulture, $"blockSize={blockSize}.")));
        }

        return Result<FilesystemInfo>.Success(new FilesystemInfo
        {
            Kind = kind,
            BytesPerCluster = blockSize,
            VolumeBytes = (long)blockSize * totalBlocks,
        });
    }

    /// <summary>Reads an APFS container superblock.</summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="head">The volume's first bytes, covering block 0.</param>
    internal static Result<FilesystemInfo> ProbeApfs(VolumeReader volume, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (head.Length < 48 || !head.Slice(ApfsMagicOffset, 4).SequenceEqual("NXSB"u8))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume claims to be APFS but its container superblock does not say so.",
                $"The NXSB magic is missing from offset {ApfsMagicOffset}."));
        }

        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(head[36..]);
        ulong blockCount = BinaryPrimitives.ReadUInt64LittleEndian(head[40..]);

        if (blockSize < 512 || blockSize > (1 << 24) || (blockSize & (blockSize - 1)) != 0)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's APFS container declares an impossible block size.",
                string.Create(CultureInfo.InvariantCulture, $"nx_block_size={blockSize}.")));
        }

        return Result<FilesystemInfo>.Success(new FilesystemInfo
        {
            Kind = FilesystemKind.Apfs,
            BytesPerCluster = blockSize,
            VolumeBytes = blockCount > (ulong)(long.MaxValue / blockSize)
                ? long.MaxValue
                : (long)blockCount * blockSize,
        });
    }
}
