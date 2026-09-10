using System.Buffers.Binary;

namespace Dmg.Core.Filesystems;

/// <summary>
/// The filesystems this build can recognise inside an image.
/// </summary>
/// <remarks>
/// Recognising one is not the same as being able to mount it. The tool exists
/// because Windows already has drivers for some of these and none at all for the
/// others; naming the ones it cannot mount is what lets the refusal say something
/// useful instead of "unsupported filesystem".
/// </remarks>
public enum FilesystemKind
{
    /// <summary>Nothing recognisable at the start of the volume.</summary>
    Unknown = 0,

    /// <summary>exFAT - the format this tool exists to mount.</summary>
    ExFat = 1,

    /// <summary>FAT32.</summary>
    Fat32 = 2,

    /// <summary>FAT16.</summary>
    Fat16 = 3,

    /// <summary>FAT12.</summary>
    Fat12 = 4,

    /// <summary>NTFS.</summary>
    Ntfs = 5,

    /// <summary>HFS+ (or HFSX), which Windows has no driver for.</summary>
    HfsPlus = 6,

    /// <summary>The original HFS, which Windows has no driver for.</summary>
    Hfs = 7,

    /// <summary>APFS, which Windows has no driver for.</summary>
    Apfs = 8,
}

/// <summary>
/// Magic-number recognition: what filesystem, if any, starts at a given offset.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately only the signature check. It answers "is there a volume
/// here at all", which is what the partition layer needs to tell a whole-disk
/// image apart from a damaged partition table, and it does so from a fixed-size
/// window with no further reads. Labels, serials and geometry are the probes'
/// business.
/// </para>
/// <para>
/// <b>Every check is anchored.</b> A signature is only accepted where the format
/// puts it - <c>EXFAT</c> at offset 3 of the boot sector, <c>H+</c> at 1024,
/// <c>NXSB</c> at 32 - and the FAT family additionally has to look like a boot
/// sector. Sniffing for a string anywhere in the first block is how a JPEG turns
/// into a filesystem.
/// </para>
/// </remarks>
public static class FilesystemSignature
{
    /// <summary>How many bytes of the volume the recogniser needs.</summary>
    /// <remarks>
    /// 4096: enough for the boot sector, the HFS+ volume header at 1024, and the
    /// APFS container superblock's magic at 32.
    /// </remarks>
    public const int WindowBytes = 4096;

    /// <summary>The name to print for a filesystem.</summary>
    /// <param name="kind">The filesystem.</param>
    public static string Describe(FilesystemKind kind) => kind switch
    {
        FilesystemKind.ExFat => "exFAT",
        FilesystemKind.Fat32 => "FAT32",
        FilesystemKind.Fat16 => "FAT16",
        FilesystemKind.Fat12 => "FAT12",
        FilesystemKind.Ntfs => "NTFS",
        FilesystemKind.HfsPlus => "HFS+",
        FilesystemKind.Hfs => "HFS",
        FilesystemKind.Apfs => "APFS",
        _ => "unrecognised",
    };

    /// <summary>
    /// True when Windows has a driver for this filesystem, so mounting it is worth
    /// attempting.
    /// </summary>
    /// <param name="kind">The filesystem.</param>
    public static bool WindowsCanMount(FilesystemKind kind) => kind switch
    {
        FilesystemKind.ExFat or FilesystemKind.Fat32 or FilesystemKind.Fat16
            or FilesystemKind.Fat12 or FilesystemKind.Ntfs => true,
        _ => false,
    };

    /// <summary>
    /// Identifies the filesystem at the start of <paramref name="volume"/>.
    /// </summary>
    /// <param name="volume">
    /// The first <see cref="WindowBytes"/> bytes of the volume. A shorter span is
    /// allowed; checks that do not fit in it simply do not fire.
    /// </param>
    public static FilesystemKind Recognize(ReadOnlySpan<byte> volume)
    {
        if (volume.Length >= 512)
        {
            if (volume.Slice(3, 8).SequenceEqual("EXFAT   "u8) && HasBootSignature(volume))
            {
                return FilesystemKind.ExFat;
            }

            if (volume.Slice(3, 8).SequenceEqual("NTFS    "u8) && HasBootSignature(volume))
            {
                return FilesystemKind.Ntfs;
            }

            FilesystemKind fat = RecognizeFat(volume);

            if (fat != FilesystemKind.Unknown)
            {
                return fat;
            }
        }

        if (volume.Length >= 36 && volume.Slice(32, 4).SequenceEqual("NXSB"u8))
        {
            return FilesystemKind.Apfs;
        }

        if (volume.Length >= 1026)
        {
            ushort signature = BinaryPrimitives.ReadUInt16BigEndian(volume[1024..]);

            // 'H+' is HFS+, 'HX' is HFSX (case-sensitive HFS+), 'BD' is the
            // original HFS - which in practice is an HFS+ volume in a wrapper.
            if (signature is 0x482B or 0x4858)
            {
                return FilesystemKind.HfsPlus;
            }

            if (signature == 0x4244)
            {
                return FilesystemKind.Hfs;
            }
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>True when the sector ends in the <c>0x55AA</c> boot signature.</summary>
    /// <param name="sector">The boot sector.</param>
    private static bool HasBootSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= 512 && sector[510] == 0x55 && sector[511] == 0xAA;

    /// <summary>
    /// FAT, which has no magic number of its own and has to be recognised by its
    /// BIOS parameter block agreeing with itself.
    /// </summary>
    /// <param name="volume">The boot sector.</param>
    private static FilesystemKind RecognizeFat(ReadOnlySpan<byte> volume)
    {
        if (!HasBootSignature(volume))
        {
            return FilesystemKind.Unknown;
        }

        // A FAT volume starts with a jump instruction and declares a sector size
        // that is a power of two between 512 and 4096, at least one FAT, and a
        // non-zero media descriptor. Without those the type string at offset 82 or
        // 54 is just bytes that happen to spell FAT32.
        if (volume[0] is not (0xEB or 0xE9))
        {
            return FilesystemKind.Unknown;
        }

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(volume[11..]);

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            return FilesystemKind.Unknown;
        }

        if (volume[13] == 0 || volume[16] == 0)
        {
            return FilesystemKind.Unknown;
        }

        ushort fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(volume[22..]);
        ushort rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(volume[17..]);

        if (volume.Slice(82, 8).SequenceEqual("FAT32   "u8) || (fatSize16 == 0 && rootEntries == 0))
        {
            return FilesystemKind.Fat32;
        }

        if (volume.Slice(54, 8).SequenceEqual("FAT16   "u8))
        {
            return FilesystemKind.Fat16;
        }

        if (volume.Slice(54, 8).SequenceEqual("FAT12   "u8))
        {
            return FilesystemKind.Fat12;
        }

        if (volume.Slice(54, 5).SequenceEqual("FAT  "u8) && fatSize16 != 0)
        {
            // An older writer that filled in only "FAT". Cluster count is what
            // separates FAT12 from FAT16, and the probes work that out; the
            // signature layer says FAT16, which is the common case by far.
            return FilesystemKind.Fat16;
        }

        return FilesystemKind.Unknown;
    }
}
