using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dmg.Core.Partitions;

/// <summary>
/// The GUID partition table: a 92-byte header at LBA 1 and an array of 128-byte
/// entries wherever the header points.
/// </summary>
/// <remarks>
/// <para>
/// <b>The header CRC is checked, not assumed.</b> A GPT carries a CRC-32 of its
/// own header and a second one of the whole entry array, and they are the only
/// thing standing between a partly overwritten table and a mount that hands
/// Windows the wrong sectors. Both are verified here; either failing is a
/// <see cref="DmgExitCode.CorruptImage"/> refusal.
/// </para>
/// <para>
/// <b>The backup table is not required.</b> An image converted with
/// <c>hdiutil convert -format UDTO</c> stops at the last non-zero sector, so the
/// backup header the primary points at is routinely missing from a perfectly good
/// image. The primary header's self-consistency is what is checked; the alternate
/// LBA is reported, never chased.
/// </para>
/// </remarks>
public static class GuidPartitionTable
{
    /// <summary>The signature at the start of the header: <c>EFI PART</c>.</summary>
    public static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    /// <summary>The LBA the primary header lives at.</summary>
    public const long HeaderSector = 1;

    /// <summary>The smallest header the specification allows.</summary>
    public const int MinimumHeaderSize = 92;

    /// <summary>The entry size every writer in practice uses.</summary>
    public const int StandardEntrySize = 128;

    /// <summary>The most entries this reader will parse, as a sanity cap.</summary>
    public const int MaxEntryCount = 4096;

    /// <summary>True when <paramref name="sector"/> starts with the GPT signature.</summary>
    /// <param name="sector">The sector at LBA 1.</param>
    public static bool HasSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= Signature.Length && sector[..Signature.Length].SequenceEqual(Signature);

    /// <summary>
    /// Reads the header at LBA 1 and the entry array it points at.
    /// </summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="headerSector">The 512 bytes at LBA 1.</param>
    /// <param name="diskSectors">The disk's size in sectors, for range checks. Zero to skip them.</param>
    public static Result<PartitionTable> Read(Stream disk, ReadOnlySpan<byte> headerSector, ulong diskSectors)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (!HasSignature(headerSector))
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image has a protective master boot record but no GUID partition table behind it.",
                "The EFI PART signature is missing from LBA 1."));
        }

        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[8..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[12..]);
        uint storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[16..]);

        if (headerSize < MinimumHeaderSize || headerSize > DiskIo.BytesPerSector)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table header declares an impossible size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"HeaderSize={headerSize}; it must be between {MinimumHeaderSize} and {DiskIo.BytesPerSector}.")));
        }

        uint computedHeaderCrc = HeaderCrc(headerSector[..(int)headerSize]);

        if (computedHeaderCrc != storedHeaderCrc)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table header fails its own checksum, so the partition "
                + "layout cannot be trusted.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Header CRC-32 is 0x{storedHeaderCrc:X8} in the image but 0x{computedHeaderCrc:X8} "
                    + $"over the {headerSize} bytes it covers.")));
        }

        if ((revision >> 16) != 1)
        {
            return Result<PartitionTable>.Failure(DmgError.Unsupported(
                "This image uses a GUID partition table revision this build does not know.",
                string.Create(CultureInfo.InvariantCulture, $"Revision 0x{revision:X8}.")));
        }

        ulong myLba = BinaryPrimitives.ReadUInt64LittleEndian(headerSector[24..]);
        ulong alternateLba = BinaryPrimitives.ReadUInt64LittleEndian(headerSector[32..]);
        ulong firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(headerSector[40..]);
        ulong lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(headerSector[48..]);
        string diskGuid = FormatGuid(headerSector.Slice(56, 16));
        ulong entryArrayLba = BinaryPrimitives.ReadUInt64LittleEndian(headerSector[72..]);
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[80..]);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[84..]);
        uint storedArrayCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerSector[88..]);

        if (myLba != HeaderSector)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table header does not agree about where it lives.",
                string.Create(CultureInfo.InvariantCulture, $"MyLBA={myLba}, but it was read from LBA 1.")));
        }

        if (firstUsable > lastUsable)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table declares an empty usable range.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FirstUsableLBA={firstUsable}, LastUsableLBA={lastUsable}.")));
        }

        if (entrySize < StandardEntrySize || entrySize % 8 != 0 || entrySize > 4096)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table declares an impossible entry size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SizeOfPartitionEntry={entrySize}; it must be a multiple of 8 and at least {StandardEntrySize}.")));
        }

        if (entryCount > MaxEntryCount)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition table declares more entries than any real disk has.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"NumberOfPartitionEntries={entryCount}; this build reads at most {MaxEntryCount}.")));
        }

        Result<byte[]> array = DiskIo.ReadSectors(
            disk,
            (long)entryArrayLba,
            (int)((((long)entryCount * entrySize) + DiskIo.BytesPerSector - 1) / DiskIo.BytesPerSector),
            "the GUID partition table's entry array");

        if (!array.TryGetValue(out byte[]? arrayBytes))
        {
            return array.CastFailure<PartitionTable>();
        }

        int arrayLength = (int)((long)entryCount * entrySize);

        if (arrayLength > arrayBytes.Length)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image ends inside its GUID partition table's entry array.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entryCount} entries of {entrySize} bytes need {arrayLength} bytes at LBA {entryArrayLba}.")));
        }

        uint computedArrayCrc = Crc32.Compute(arrayBytes.AsSpan(0, arrayLength));

        if (computedArrayCrc != storedArrayCrc)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's GUID partition entries fail the checksum in its header, so the partition "
                + "layout cannot be trusted.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Entry array CRC-32 is 0x{storedArrayCrc:X8} in the header but 0x{computedArrayCrc:X8} "
                    + $"over the {arrayLength} bytes it covers.")));
        }

        List<PartitionEntry> partitions = [];

        for (uint index = 0; index < entryCount; index++)
        {
            ReadOnlySpan<byte> raw = arrayBytes.AsSpan((int)(index * entrySize), (int)entrySize);

            if (!raw[..16].ContainsAnyExcept((byte)0))
            {
                continue;
            }

            ulong start = BinaryPrimitives.ReadUInt64LittleEndian(raw[32..]);
            ulong end = BinaryPrimitives.ReadUInt64LittleEndian(raw[40..]);

            if (end < start)
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's GUID partition table describes a partition that ends before it begins.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1}: StartingLBA={start}, EndingLBA={end}.")));
            }

            if (diskSectors > 0 && start >= diskSectors)
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's GUID partition table describes a partition that starts past the end of the disk.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} starts at sector {start} of a {diskSectors}-sector disk.")));
            }

            string typeGuid = FormatGuid(raw[..16]);

            partitions.Add(new PartitionEntry
            {
                Number = partitions.Count + 1,
                Scheme = PartitionScheme.GuidPartitionTable,
                StartSector = start,
                SectorCount = end - start + 1,
                TypeGuid = typeGuid,
                UniqueGuid = FormatGuid(raw.Slice(16, 16)),
                TypeName = DescribeType(typeGuid),
                Name = ReadName(raw, (int)entrySize),
                IsFreeSpace = false,
            });
        }

        return Result<PartitionTable>.Success(
            new PartitionTable(PartitionScheme.GuidPartitionTable, partitions)
            {
                DiskGuid = diskGuid,
                DiskSectors = diskSectors > 0 ? diskSectors : alternateLba + 1,
            });
    }

    /// <summary>
    /// The CRC-32 the header should carry: computed over the header with its own
    /// CRC field treated as zero.
    /// </summary>
    /// <param name="header">The header, trimmed to its declared size.</param>
    internal static uint HeaderCrc(ReadOnlySpan<byte> header)
    {
        byte[] copy = header.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16), 0);
        return Crc32.Compute(copy);
    }

    /// <summary>
    /// Renders a GPT GUID the way every tool prints it.
    /// </summary>
    /// <param name="raw">The sixteen bytes from the table.</param>
    /// <remarks>
    /// The first three fields are little-endian and the last two are laid out in
    /// order, which is the mixed endianness the fixtures confirm: an
    /// <c>Apple_HFS</c> partition stores <c>00 53 46 48 …</c> and prints as
    /// <c>48465300-0000-11AA-AA11-00306543ECAC</c>.
    /// </remarks>
    public static string FormatGuid(ReadOnlySpan<byte> raw) =>
        raw.Length < 16
            ? string.Empty
            : new Guid(
                BinaryPrimitives.ReadUInt32LittleEndian(raw),
                BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]),
                raw[8], raw[9], raw[10], raw[11], raw[12], raw[13], raw[14], raw[15])
                .ToString("D", CultureInfo.InvariantCulture)
                .ToUpperInvariant();

    /// <summary>The human-readable name for a GPT partition type GUID.</summary>
    /// <param name="typeGuid">The type GUID, canonical upper case.</param>
    public static string DescribeType(string typeGuid) => typeGuid switch
    {
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI System",
        "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft Reserved",
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft Basic Data",
        "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC" => "Windows Recovery Environment",
        "48465300-0000-11AA-AA11-00306543ECAC" => "Apple_HFS",
        "7C3457EF-0000-11AA-AA11-00306543ECAC" => "Apple_APFS",
        "55465300-0000-11AA-AA11-00306543ECAC" => "Apple_UFS",
        "52414944-0000-11AA-AA11-00306543ECAC" => "Apple_RAID",
        "52414944-5F4F-11AA-AA11-00306543ECAC" => "Apple_RAID_Offline",
        "426F6F74-0000-11AA-AA11-00306543ECAC" => "Apple_Boot",
        "4C616265-6C00-11AA-AA11-00306543ECAC" => "Apple_Label",
        "5265636F-7665-11AA-AA11-00306543ECAC" => "Apple_TV_Recovery",
        "53746F72-6167-11AA-AA11-00306543ECAC" => "Apple_Core_Storage",
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux filesystem",
        "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux swap",
        "21686148-6449-6E6F-744E-656564454649" => "BIOS boot",
        "00000000-0000-0000-0000-000000000000" => "Unused",
        _ => $"Unknown type {typeGuid}",
    };

    private static string ReadName(ReadOnlySpan<byte> entry, int entrySize)
    {
        // 36 UTF-16LE code units at offset 56, NUL-padded. A shorter entry than
        // the standard one cannot carry the whole name, so take what fits.
        int available = Math.Max(0, Math.Min(72, entrySize - 56));

        if (available < 2)
        {
            return string.Empty;
        }

        string name = Encoding.Unicode.GetString(entry.Slice(56, available - (available % 2)));
        int terminator = name.IndexOf('\0', StringComparison.Ordinal);

        return (terminator >= 0 ? name[..terminator] : name).Trim();
    }
}
