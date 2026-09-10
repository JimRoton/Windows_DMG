using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dmg.Core.Filesystems;

/// <summary>
/// exFAT: the format this tool exists to mount, and the one path through the code
/// that has to be right.
/// </summary>
/// <remarks>
/// <para>
/// <b>The label is not in the boot sector.</b> exFAT keeps the volume label in a
/// directory entry at the start of the root directory - type <c>0x83</c> when it
/// is in use, <c>0x03</c> when the volume has been given no label - with the text
/// as UTF-16 and a length in characters, not bytes. Reading the boot sector alone
/// gets the serial and nothing a user recognises their disk by, so the root
/// directory chain is walked here.
/// </para>
/// <para>
/// <b>Geometry is checked before it is used.</b> Every offset in the boot sector
/// is a number an image controls, so the FAT, the cluster heap and the root
/// directory all have to lie inside the volume and inside each other before a
/// single one of them is read.
/// </para>
/// </remarks>
internal static class ExFatProbe
{
    /// <summary>The signature at offset 3 of the boot sector.</summary>
    internal static ReadOnlySpan<byte> FileSystemName => "EXFAT   "u8;

    /// <summary>The directory entry type for a volume label that is in use.</summary>
    internal const byte VolumeLabelInUse = 0x83;

    /// <summary>The same entry with its in-use bit cleared: a volume with no label.</summary>
    internal const byte VolumeLabelUnused = 0x03;

    /// <summary>The end-of-directory marker.</summary>
    internal const byte EndOfDirectory = 0x00;

    /// <summary>The most clusters of root directory this probe will walk.</summary>
    internal const int MaxRootDirectoryClusters = 64;

    /// <summary>Reads an exFAT volume's boot sector, geometry and label.</summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="boot">The volume's first sector, already known to carry the exFAT signature.</param>
    internal static Result<FilesystemInfo> Probe(VolumeReader volume, ReadOnlySpan<byte> boot)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (boot.Length < 512 || !boot.Slice(3, 8).SequenceEqual(FileSystemName))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume claims to be exFAT but its boot sector does not say so.",
                "The EXFAT signature is missing from offset 3."));
        }

        // exFAT reuses the bytes a FAT BIOS parameter block would occupy and
        // requires them to be zero. A writer that filled them in produced
        // something no exFAT driver will mount.
        if (boot[11..64].ContainsAnyExcept((byte)0))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector has a BIOS parameter block in it.",
                "Bytes 11-63 must be zero in exFAT; this volume has data there."));
        }

        byte sectorShift = boot[108];
        byte clusterShift = boot[109];

        if (sectorShift is < 9 or > 12)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible sector size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSectorShift={sectorShift}; it must be between 9 (512) and 12 (4096).")));
        }

        if (sectorShift + clusterShift > 25)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible cluster size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSectorShift={sectorShift} plus SectorsPerClusterShift={clusterShift} exceeds the 32 MB maximum.")));
        }

        int bytesPerSector = 1 << sectorShift;
        int sectorsPerCluster = 1 << clusterShift;
        long bytesPerCluster = (long)bytesPerSector * sectorsPerCluster;

        ulong volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(boot[72..]);
        uint fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]);
        uint fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]);
        uint heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]);
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot[92..]);
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]);
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(boot[100..]);
        ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(boot[104..]);
        byte fatCount = boot[110];

        if (fatCount is not (1 or 2))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible number of FATs.",
                string.Create(CultureInfo.InvariantCulture, $"NumberOfFats={fatCount}; exFAT allows 1 or 2.")));
        }

        if (fatOffset < 24 || fatLength == 0 || heapOffset < (ulong)fatOffset + fatLength)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT layout overlaps itself.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FatOffset={fatOffset}, FatLength={fatLength}, ClusterHeapOffset={heapOffset}.")));
        }

        if (volumeLength < (ulong)heapOffset + ((ulong)clusterCount * (ulong)sectorsPerCluster))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT cluster heap does not fit in the volume it declares.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"VolumeLength={volumeLength} sectors, ClusterHeapOffset={heapOffset}, "
                    + $"ClusterCount={clusterCount}, {sectorsPerCluster} sectors per cluster.")));
        }

        if (rootCluster < 2 || rootCluster >= (ulong)clusterCount + 2)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's exFAT root directory is outside its cluster heap.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FirstClusterOfRootDirectory={rootCluster}, ClusterCount={clusterCount}.")));
        }

        Result<string?> label = ReadVolumeLabel(
            volume,
            bytesPerSector,
            sectorsPerCluster,
            fatOffset,
            fatLength,
            heapOffset,
            clusterCount,
            rootCluster);

        if (!label.Ok)
        {
            return label.CastFailure<FilesystemInfo>();
        }

        return Result<FilesystemInfo>.Success(new FilesystemInfo
        {
            Kind = FilesystemKind.ExFat,
            VolumeLabel = label.GetValueOrDefault(),
            VolumeSerial = FilesystemInfo.FormatSerial(serial),
            BytesPerSector = bytesPerSector,
            BytesPerCluster = bytesPerCluster,
            VolumeBytes = volumeLength > (ulong)(long.MaxValue / bytesPerSector)
                ? long.MaxValue
                : (long)volumeLength * bytesPerSector,
        });
    }

    /// <summary>
    /// Walks the root directory for the volume label entry.
    /// </summary>
    /// <remarks>
    /// The label is normally the very first entry, but it is not required to be,
    /// and the root directory is a cluster chain like any other directory. The
    /// walk follows the FAT, refuses to leave the cluster heap, and gives up after
    /// <see cref="MaxRootDirectoryClusters"/> clusters so a chain that loops back
    /// on itself cannot spin.
    /// </remarks>
    private static Result<string?> ReadVolumeLabel(
        VolumeReader volume,
        int bytesPerSector,
        int sectorsPerCluster,
        uint fatOffset,
        uint fatLength,
        uint heapOffset,
        uint clusterCount,
        uint rootCluster)
    {
        int bytesPerCluster = bytesPerSector * sectorsPerCluster;
        uint cluster = rootCluster;

        for (int step = 0; step < MaxRootDirectoryClusters; step++)
        {
            long clusterOffset =
                ((long)heapOffset + ((long)(cluster - 2) * sectorsPerCluster)) * bytesPerSector;

            Result<byte[]> read = volume.Read(clusterOffset, bytesPerCluster, "root directory");

            if (!read.TryGetValue(out byte[]? directory))
            {
                return read.CastFailure<string?>();
            }

            for (int entry = 0; entry + 32 <= directory.Length; entry += 32)
            {
                ReadOnlySpan<byte> raw = directory.AsSpan(entry, 32);

                if (raw[0] == EndOfDirectory)
                {
                    return Result<string?>.Success(null);
                }

                if (raw[0] == VolumeLabelUnused)
                {
                    // The entry is there and says the volume has no label.
                    return Result<string?>.Success(null);
                }

                if (raw[0] != VolumeLabelInUse)
                {
                    continue;
                }

                int characters = raw[1];

                if (characters is < 1 or > 11)
                {
                    return Result<string?>.Failure(DmgError.Corrupt(
                        "This volume's exFAT label entry declares an impossible length.",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"CharacterCount={characters}; exFAT allows up to 11.")));
                }

                return Result<string?>.Success(
                    Encoding.Unicode.GetString(raw.Slice(2, characters * 2)).Trim());
            }

            Result<uint> next = NextCluster(volume, bytesPerSector, fatOffset, fatLength, cluster);

            if (!next.TryGetValue(out uint following))
            {
                return next.CastFailure<string?>();
            }

            if (following is 0xFFFFFFFF or 0)
            {
                return Result<string?>.Success(null);
            }

            if (following < 2 || following >= clusterCount + 2)
            {
                return Result<string?>.Failure(DmgError.Corrupt(
                    "This volume's exFAT root directory chain leaves the cluster heap.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cluster {cluster} is followed by {following}, and the heap holds {clusterCount} clusters.")));
            }

            cluster = following;
        }

        // A root directory longer than this is not a labelling problem worth
        // failing over; the volume simply has no label as far as we are concerned.
        return Result<string?>.Success(null);
    }

    /// <summary>Reads one FAT entry.</summary>
    private static Result<uint> NextCluster(
        VolumeReader volume,
        int bytesPerSector,
        uint fatOffset,
        uint fatLength,
        uint cluster)
    {
        long entryOffset = ((long)fatOffset * bytesPerSector) + ((long)cluster * 4);

        if (entryOffset + 4 > ((long)fatOffset + fatLength) * bytesPerSector)
        {
            return Result<uint>.Failure(DmgError.Corrupt(
                "This volume's exFAT root directory points outside its own FAT.",
                string.Create(CultureInfo.InvariantCulture, $"Cluster {cluster} has no entry in a {fatLength}-sector FAT.")));
        }

        Result<byte[]> read = volume.Read(entryOffset, 4, "file allocation table");

        return read.TryGetValue(out byte[]? bytes)
            ? Result<uint>.Success(BinaryPrimitives.ReadUInt32LittleEndian(bytes))
            : read.CastFailure<uint>();
    }
}
