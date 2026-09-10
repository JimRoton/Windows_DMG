using System.Buffers.Binary;
using System.Globalization;

namespace Dmg.Core.Partitions;

/// <summary>
/// The DOS master boot record at sector 0: four 16-byte entries at offset 446 and
/// the <c>0x55AA</c> signature at 510.
/// </summary>
/// <remarks>
/// <para>
/// Two quite different things live in this structure. A real MBR describes up to
/// four partitions, and <c>hdiutil</c> writes one for every image whose payload
/// Windows is meant to read - the exFAT and FAT32 fixtures are all MBR disks with
/// a single type <c>0x07</c> or <c>0x0B</c> entry. A protective MBR describes one
/// type <c>0xEE</c> partition covering the disk and exists only to stop old tools
/// from believing the disk is unpartitioned; the real table is the GPT behind it.
/// </para>
/// <para>
/// <b>CHS is ignored.</b> The fixtures write <c>FE FF FF</c> - the "out of range,
/// use LBA" sentinel - into every CHS triple, which is what every modern writer
/// does. Only the LBA fields are read.
/// </para>
/// </remarks>
public static class MasterBootRecord
{
    /// <summary>Byte offset of the first partition entry within sector 0.</summary>
    public const int FirstEntryOffset = 446;

    /// <summary>The size of one entry.</summary>
    public const int EntrySize = 16;

    /// <summary>How many entries an MBR has.</summary>
    public const int EntryCount = 4;

    /// <summary>The partition type that means "the real table is a GPT".</summary>
    public const byte ProtectiveType = 0xEE;

    /// <summary>True when <paramref name="sector"/> ends in the <c>0x55AA</c> boot signature.</summary>
    /// <param name="sector">Sector 0 of the disk.</param>
    public static bool HasBootSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= DiskIo.BytesPerSector && sector[510] == 0x55 && sector[511] == 0xAA;

    /// <summary>
    /// True when sector 0 is a protective MBR: a single <c>0xEE</c> entry and
    /// nothing else. The GPT reader, not this one, describes such a disk.
    /// </summary>
    /// <param name="sector">Sector 0 of the disk.</param>
    public static bool IsProtective(ReadOnlySpan<byte> sector)
    {
        if (!HasBootSignature(sector))
        {
            return false;
        }

        bool sawProtective = false;

        for (int index = 0; index < EntryCount; index++)
        {
            byte type = sector[FirstEntryOffset + (index * EntrySize) + 4];

            if (type == ProtectiveType)
            {
                sawProtective = true;
            }
            else if (type != 0)
            {
                return false;
            }
        }

        return sawProtective;
    }

    /// <summary>
    /// Reads sector 0 as a real partition table.
    /// </summary>
    /// <param name="sector">Sector 0 of the disk.</param>
    /// <param name="diskSectors">The disk's size in sectors, for range checks. Zero to skip them.</param>
    /// <remarks>
    /// An entry whose type byte is zero is an unused slot and is skipped without
    /// consuming a partition number. An entry that is present but impossible - a
    /// zero length, or a range running off the end of the disk - is a corrupt
    /// image, because the alternative is handing a filesystem probe an offset the
    /// image cannot satisfy.
    /// </remarks>
    public static Result<PartitionTable> Read(ReadOnlySpan<byte> sector, ulong diskSectors)
    {
        if (!HasBootSignature(sector))
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "Sector 0 of this image is not a master boot record.",
                "The 0x55AA signature at offset 510 is missing."));
        }

        List<PartitionEntry> partitions = [];

        for (int index = 0; index < EntryCount; index++)
        {
            ReadOnlySpan<byte> raw = sector.Slice(FirstEntryOffset + (index * EntrySize), EntrySize);
            byte status = raw[0];
            byte type = raw[4];
            uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
            uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);

            if (type == 0)
            {
                continue;
            }

            if (status is not (0x00 or 0x80))
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's master boot record is damaged.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} has status byte 0x{status:X2}; only 0x00 and 0x80 are valid.")));
            }

            if (sectorCount == 0)
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's master boot record declares an empty partition.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} has type 0x{type:X2} but a length of zero sectors.")));
            }

            ulong end = (ulong)startSector + sectorCount;

            if (diskSectors > 0 && end > diskSectors)
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's master boot record describes a partition that runs off the end of the disk.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} covers sectors {startSector}-{end - 1} of a {diskSectors}-sector disk.")));
            }

            partitions.Add(new PartitionEntry
            {
                Number = partitions.Count + 1,
                Scheme = PartitionScheme.MasterBootRecord,
                StartSector = startSector,
                SectorCount = sectorCount,
                MbrType = type,
                TypeName = DescribeType(type),
                IsFreeSpace = false,
            });
        }

        return partitions.Count == 0
            ? Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image has a master boot record with no partitions in it.",
                "All four entries have partition type 0x00."))
            : Result<PartitionTable>.Success(new PartitionTable(PartitionScheme.MasterBootRecord, partitions)
            {
                DiskSectors = diskSectors,
            });
    }

    /// <summary>The human-readable name for an MBR partition type byte.</summary>
    /// <param name="type">The type byte from the entry.</param>
    /// <remarks>
    /// Type <c>0x07</c> is the interesting one: it means "IFS", which in practice
    /// is NTFS or exFAT, and hdiutil writes it for every exFAT image it makes -
    /// the type byte alone never distinguishes the two, so the name says so and
    /// the filesystem probe settles it.
    /// </remarks>
    public static string DescribeType(byte type) => type switch
    {
        0x00 => "Unused (0x00)",
        0x01 => "FAT12 (0x01)",
        0x04 => "FAT16 under 32 MB (0x04)",
        0x05 => "Extended (0x05)",
        0x06 => "FAT16 (0x06)",
        0x07 => "exFAT or NTFS (0x07)",
        0x0B => "FAT32 (0x0B)",
        0x0C => "FAT32 LBA (0x0C)",
        0x0E => "FAT16 LBA (0x0E)",
        0x0F => "Extended LBA (0x0F)",
        0x11 => "Hidden FAT12 (0x11)",
        0x1B => "Hidden FAT32 (0x1B)",
        0x1C => "Hidden FAT32 LBA (0x1C)",
        0x27 => "Windows recovery (0x27)",
        0x82 => "Linux swap (0x82)",
        0x83 => "Linux (0x83)",
        0xA8 => "Apple UFS (0xA8)",
        0xAB => "Apple boot (0xAB)",
        0xAF => "Apple HFS or HFS+ (0xAF)",
        0xEE => "GPT protective (0xEE)",
        0xEF => "EFI system (0xEF)",
        _ => string.Create(CultureInfo.InvariantCulture, $"Unknown (0x{type:X2})"),
    };
}
