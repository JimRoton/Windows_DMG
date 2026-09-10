using Dmg.Core.Filesystems;

namespace Dmg.Core.Partitions;

/// <summary>
/// The one entry point: hand it a decoded disk, get back what is on it.
/// </summary>
/// <remarks>
/// <para>
/// The order matters. A GPT disk also has a master boot record - a protective one
/// - so the GPT is looked for first and the MBR is only read as a real table when
/// there is no GPT behind it. A whole-disk image is looked for before either,
/// because an exFAT or FAT boot sector ends in the same <c>0x55AA</c> signature an
/// MBR does and would otherwise be misread as a partition table with four empty
/// slots.
/// </para>
/// <para>
/// <b>No partition table is not the same as a broken one.</b> A whole-disk image
/// is only reported when a filesystem is positively recognised at sector 0. An
/// image with something at sector 0 that is nearly a partition table - a
/// protective MBR with no GPT behind it, a GPT whose checksum fails, a driver
/// descriptor map with no map - is a <see cref="DmgExitCode.CorruptImage"/>
/// failure, and so is an image with nothing recognisable anywhere. Treating
/// damage as "one big volume" would hand Windows sectors the user never asked
/// for, which is the one outcome this layer exists to prevent.
/// </para>
/// </remarks>
public static class PartitionTableReader
{
    /// <summary>Reads the partition table from a decoded disk.</summary>
    /// <param name="disk">The fully decoded disk, positioned anywhere.</param>
    public static Result<PartitionTable> Read(Stream disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        ulong diskSectors = DiskIo.SectorCount(disk);

        if (diskSectors == 0)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image decodes to nothing, so it has no partitions.",
                $"The decoded disk is {(disk.CanSeek ? disk.Length : -1)} bytes."));
        }

        int windowSectors = (int)Math.Min(
            diskSectors,
            FilesystemSignature.WindowBytes / DiskIo.BytesPerSector);

        Result<byte[]> head = DiskIo.ReadSectors(disk, 0, windowSectors, "the start of the disk");

        if (!head.TryGetValue(out byte[]? window))
        {
            return head.CastFailure<PartitionTable>();
        }

        ReadOnlySpan<byte> sector0 = window.AsSpan(0, Math.Min(window.Length, DiskIo.BytesPerSector));
        ReadOnlySpan<byte> sector1 = window.Length >= 2 * DiskIo.BytesPerSector
            ? window.AsSpan(DiskIo.BytesPerSector, DiskIo.BytesPerSector)
            : [];

        FilesystemKind wholeDisk = FilesystemSignature.Recognize(window);

        if (wholeDisk != FilesystemKind.Unknown)
        {
            return Result<PartitionTable>.Success(WholeDisk(wholeDisk, diskSectors));
        }

        if (GuidPartitionTable.HasSignature(sector1) || MasterBootRecord.IsProtective(sector0))
        {
            // A protective MBR promises a GPT. Its absence is damage, not a disk
            // with one 0xEE partition on it.
            return GuidPartitionTable.Read(disk, sector1, diskSectors);
        }

        if (ApplePartitionMap.HasDriverDescriptorMap(sector0) || ApplePartitionMap.HasMapEntry(sector1))
        {
            // Older images are laid out this way. The driver descriptor map is the
            // usual marker, but an image can carry the map without it.
            return ApplePartitionMap.Read(disk, sector0, diskSectors);
        }

        if (MasterBootRecord.HasBootSignature(sector0))
        {
            return MasterBootRecord.Read(sector0, diskSectors);
        }

        return Result<PartitionTable>.Failure(DmgError.Corrupt(
            "This image has no partition table and no filesystem this build recognises at the start of it.",
            "Sector 0 is neither a master boot record nor a driver descriptor map, there is no GUID "
            + "partition table header at LBA 1, and the first sectors are not the start of an exFAT, "
            + "FAT, NTFS, HFS+ or APFS volume."));
    }

    /// <summary>
    /// The table for an image that is one volume from end to end: a single entry
    /// covering the whole stream, so callers above never special-case it.
    /// </summary>
    /// <param name="kind">The filesystem recognised at sector 0.</param>
    /// <param name="diskSectors">The disk's size in sectors.</param>
    private static PartitionTable WholeDisk(FilesystemKind kind, ulong diskSectors) =>
        new(
            PartitionScheme.WholeDisk,
            [
                new PartitionEntry
                {
                    Number = 1,
                    Scheme = PartitionScheme.WholeDisk,
                    StartSector = 0,
                    SectorCount = diskSectors,
                    TypeName = $"{FilesystemSignature.Describe(kind)} (whole disk)",
                    Name = string.Empty,
                    IsFreeSpace = false,
                },
            ])
        {
            DiskSectors = diskSectors,
        };
}
