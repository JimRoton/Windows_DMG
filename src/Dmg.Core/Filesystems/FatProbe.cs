using System.Buffers.Binary;
using System.Globalization;

namespace Dmg.Core.Filesystems;

/// <summary>
/// FAT12, FAT16 and FAT32, which Windows mounts as readily as exFAT and which
/// hdiutil writes whenever it is asked for a DOS filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>FAT has no version field.</b> Nothing in the boot sector says "this is
/// FAT32"; the string at offset 82 is a hint that writers get wrong. The type is
/// worked out the way Microsoft's own specification defines it - count the data
/// clusters, then 4085 and 65525 are the boundaries - and that count is the only
/// thing this probe trusts.
/// </para>
/// <para>
/// <b>The label is in the root directory.</b> The boot sector carries a copy, but
/// it is the copy: formatting tools write it once and relabelling updates only the
/// directory entry. The directory is read first and the boot sector is a fallback
/// for a volume whose root has no label entry at all.
/// </para>
/// </remarks>
internal static class FatProbe
{
    /// <summary>The attribute bit that marks a directory entry as the volume label.</summary>
    internal const byte VolumeLabelAttribute = 0x08;

    /// <summary>The attribute combination that marks a long-file-name fragment.</summary>
    internal const byte LongNameAttributes = 0x0F;

    /// <summary>The most root-directory clusters this probe will walk.</summary>
    internal const int MaxRootDirectoryClusters = 64;

    /// <summary>The cluster count at which a volume stops being FAT12.</summary>
    internal const uint Fat16Threshold = 4085;

    /// <summary>The cluster count at which a volume becomes FAT32.</summary>
    internal const uint Fat32Threshold = 65525;

    /// <summary>Reads a FAT volume's BIOS parameter block, type, serial and label.</summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="boot">The volume's first sector.</param>
    internal static Result<FilesystemInfo> Probe(VolumeReader volume, ReadOnlySpan<byte> boot)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (boot.Length < 512)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume is too short to hold a FAT boot sector.",
                $"{boot.Length} bytes available."));
        }

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        byte sectorsPerCluster = boot[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        byte fatCount = boot[16];
        ushort rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
        ushort totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
        ushort fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot[32..]);
        uint fatSize32 = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]);

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || sectorsPerCluster == 0
            || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0
            || reservedSectors == 0
            || fatCount is 0 or > 2)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's FAT boot sector declares an impossible geometry.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSector={bytesPerSector}, SectorsPerCluster={sectorsPerCluster}, ReservedSectors={reservedSectors}, NumFATs={fatCount}.")));
        }

        uint fatSize = fatSize16 != 0 ? fatSize16 : fatSize32;
        uint totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;

        if (fatSize == 0 || totalSectors == 0)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's FAT boot sector declares no FAT or no sectors.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FATSz={fatSize}, TotSec={totalSectors}.")));
        }

        uint rootDirectorySectors =
            (uint)(((rootEntries * 32) + bytesPerSector - 1) / bytesPerSector);
        long metadataSectors = reservedSectors + ((long)fatCount * fatSize) + rootDirectorySectors;

        if (metadataSectors >= totalSectors)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's FAT metadata does not leave room for any data.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{metadataSectors} metadata sectors in a {totalSectors}-sector volume.")));
        }

        uint clusters = (uint)((totalSectors - metadataSectors) / sectorsPerCluster);

        FilesystemKind kind = clusters switch
        {
            < Fat16Threshold => FilesystemKind.Fat12,
            < Fat32Threshold => FilesystemKind.Fat16,
            _ => FilesystemKind.Fat32,
        };

        bool isFat32 = kind == FilesystemKind.Fat32;

        if (isFat32 && (fatSize32 == 0 || rootCluster < 2))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume counts as FAT32 but has no FAT32 root directory.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FATSz32={fatSize32}, RootClus={rootCluster}, clusters={clusters}.")));
        }

        uint serial = isFat32
            ? BinaryPrimitives.ReadUInt32LittleEndian(boot[67..])
            : BinaryPrimitives.ReadUInt32LittleEndian(boot[39..]);

        Result<string?> label = isFat32
            ? ReadFat32Label(volume, bytesPerSector, sectorsPerCluster, reservedSectors, fatCount, fatSize32, rootCluster, clusters)
            : ReadFixedRootLabel(volume, bytesPerSector, reservedSectors, fatCount, fatSize, rootDirectorySectors);

        if (!label.Ok)
        {
            return label.CastFailure<FilesystemInfo>();
        }

        string? name = label.GetValueOrDefault() ?? BootSectorLabel(boot, isFat32);

        return Result<FilesystemInfo>.Success(new FilesystemInfo
        {
            Kind = kind,
            VolumeLabel = name,
            VolumeSerial = FilesystemInfo.FormatSerial(serial),
            BytesPerSector = bytesPerSector,
            BytesPerCluster = (long)bytesPerSector * sectorsPerCluster,
            VolumeBytes = (long)totalSectors * bytesPerSector,
        });
    }

    /// <summary>The label copy in the boot sector, ignored when it is the placeholder.</summary>
    /// <param name="boot">The boot sector.</param>
    /// <param name="isFat32">True when the extended fields are at the FAT32 offsets.</param>
    private static string? BootSectorLabel(ReadOnlySpan<byte> boot, bool isFat32)
    {
        int signatureOffset = isFat32 ? 66 : 38;
        int labelOffset = isFat32 ? 71 : 43;

        if (boot[signatureOffset] != 0x29)
        {
            return null;
        }

        string label = DecodeShortName(boot.Slice(labelOffset, 11));

        return label.Length == 0 || string.Equals(label, "NO NAME", StringComparison.Ordinal)
            ? null
            : label;
    }

    /// <summary>Walks the FAT32 root directory chain looking for the label entry.</summary>
    private static Result<string?> ReadFat32Label(
        VolumeReader volume,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        ushort reservedSectors,
        byte fatCount,
        uint fatSize,
        uint rootCluster,
        uint clusters)
    {
        long firstDataSector = reservedSectors + ((long)fatCount * fatSize);
        int bytesPerCluster = bytesPerSector * sectorsPerCluster;
        uint cluster = rootCluster;

        for (int step = 0; step < MaxRootDirectoryClusters; step++)
        {
            if (cluster < 2 || cluster >= clusters + 2)
            {
                return Result<string?>.Failure(DmgError.Corrupt(
                    "This volume's FAT32 root directory leaves the data area.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cluster {cluster} in a volume with {clusters} clusters.")));
            }

            long offset = (firstDataSector + ((long)(cluster - 2) * sectorsPerCluster)) * bytesPerSector;
            Result<byte[]> read = volume.Read(offset, bytesPerCluster, "root directory");

            if (!read.TryGetValue(out byte[]? directory))
            {
                return read.CastFailure<string?>();
            }

            (string? label, bool ended) = ScanDirectory(directory);

            if (label is not null || ended)
            {
                return Result<string?>.Success(label);
            }

            long fatEntry = ((long)reservedSectors * bytesPerSector) + ((long)cluster * 4);
            Result<byte[]> entry = volume.Read(fatEntry, 4, "file allocation table");

            if (!entry.TryGetValue(out byte[]? bytes))
            {
                return entry.CastFailure<string?>();
            }

            uint next = BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF;

            if (next is < 2 or >= 0x0FFFFFF8)
            {
                return Result<string?>.Success(null);
            }

            cluster = next;
        }

        return Result<string?>.Success(null);
    }

    /// <summary>Reads the fixed-size root directory FAT12 and FAT16 volumes have.</summary>
    private static Result<string?> ReadFixedRootLabel(
        VolumeReader volume,
        ushort bytesPerSector,
        ushort reservedSectors,
        byte fatCount,
        uint fatSize,
        uint rootDirectorySectors)
    {
        if (rootDirectorySectors == 0)
        {
            return Result<string?>.Success(null);
        }

        long offset = (reservedSectors + ((long)fatCount * fatSize)) * bytesPerSector;
        long length = (long)rootDirectorySectors * bytesPerSector;

        Result<byte[]> read = volume.Read(
            offset,
            (int)Math.Min(length, 512 * 1024),
            "root directory");

        if (!read.TryGetValue(out byte[]? directory))
        {
            return read.CastFailure<string?>();
        }

        return Result<string?>.Success(ScanDirectory(directory).Label);
    }

    /// <summary>
    /// Looks for the volume label in one block of directory entries.
    /// </summary>
    /// <param name="directory">The directory data.</param>
    /// <returns>The label if one was found, and whether the directory ended here.</returns>
    private static (string? Label, bool Ended) ScanDirectory(ReadOnlySpan<byte> directory)
    {
        for (int offset = 0; offset + 32 <= directory.Length; offset += 32)
        {
            ReadOnlySpan<byte> entry = directory.Slice(offset, 32);

            if (entry[0] == 0x00)
            {
                return (null, true);
            }

            if (entry[0] == 0xE5)
            {
                continue;
            }

            byte attributes = entry[11];

            // A long-name fragment sets every one of the low four bits, volume
            // label included, so it has to be excluded before the label bit means
            // anything.
            if ((attributes & LongNameAttributes) == LongNameAttributes
                || (attributes & VolumeLabelAttribute) == 0)
            {
                continue;
            }

            string label = DecodeShortName(entry[..11]);

            if (label.Length > 0)
            {
                return (label, false);
            }
        }

        return (null, false);
    }

    /// <summary>
    /// Decodes one of FAT's 11-byte space-padded names.
    /// </summary>
    /// <remarks>
    /// These are in an OEM code page, not Unicode. This build is compiled with
    /// InvariantGlobalization, so no code page tables are available and a byte
    /// above 0x7F cannot be resolved to the character the formatter meant; those
    /// are dropped rather than guessed at or turned into replacement characters.
    /// </remarks>
    private static string DecodeShortName(ReadOnlySpan<byte> name)
    {
        Span<char> characters = stackalloc char[name.Length];
        int written = 0;

        foreach (byte value in name)
        {
            if (value is >= 0x20 and < 0x7F)
            {
                characters[written++] = (char)value;
            }
        }

        return new string(characters[..written]).TrimEnd();
    }
}
