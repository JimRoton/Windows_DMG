using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dmg.Core.Partitions;

/// <summary>
/// The Apple partition map: a driver descriptor map at block 0 and one 512-byte
/// map entry per partition from block 1 onwards, all big-endian.
/// </summary>
/// <remarks>
/// <para>
/// This is what Apple shipped before GPT, and it is still what an older <c>.dmg</c>
/// carries. It is worth reading for one reason above all: an image laid out this
/// way is almost always an <c>Apple_HFS</c> volume, and telling the user that is
/// the difference between a refusal they can act on and a refusal they cannot.
/// </para>
/// <para>
/// <b>The map describes itself.</b> The first entry is normally
/// <c>Apple_partition_map</c> - the blocks the map occupies - and every entry
/// repeats the map's own length in <c>pmMapBlkCnt</c>. That self-description is
/// what makes the structure checkable without a checksum: the count is read from
/// the first entry and every subsequent entry must carry the <c>PM</c> signature,
/// so a stray sector that happens to start with two familiar bytes does not turn
/// into a partition table.
/// </para>
/// <para>
/// <b>Blocks are not always sectors.</b> The driver descriptor map declares the
/// device's block size, and everything in the partition entries is counted in
/// those blocks. Media authored for CD-ROM uses 2048. Offsets are converted to
/// 512-byte sectors here so that callers above never have to think about it.
/// </para>
/// </remarks>
public static class ApplePartitionMap
{
    /// <summary>The driver descriptor map's signature, <c>ER</c>.</summary>
    public const ushort DriverDescriptorSignature = 0x4552;

    /// <summary>A map entry's signature, <c>PM</c>.</summary>
    public const ushort MapEntrySignature = 0x504D;

    /// <summary>The most map entries this reader will parse, as a sanity cap.</summary>
    public const int MaxEntryCount = 4096;

    /// <summary>The type string Apple uses for unallocated space.</summary>
    public const string FreeSpaceType = "Apple_Free";

    /// <summary>The type string for the blocks the map itself occupies.</summary>
    public const string MapType = "Apple_partition_map";

    /// <summary>True when sector 0 carries a driver descriptor map.</summary>
    /// <param name="sector0">The first sector of the disk.</param>
    public static bool HasDriverDescriptorMap(ReadOnlySpan<byte> sector0) =>
        sector0.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(sector0) == DriverDescriptorSignature;

    /// <summary>True when the sector at block 1 carries a map entry.</summary>
    /// <param name="sector1">The second sector of the disk.</param>
    public static bool HasMapEntry(ReadOnlySpan<byte> sector1) =>
        sector1.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(sector1) == MapEntrySignature;

    /// <summary>
    /// Reads the partition map.
    /// </summary>
    /// <param name="disk">The decoded disk.</param>
    /// <param name="sector0">The first sector, which may or may not be a driver descriptor map.</param>
    /// <param name="diskSectors">The disk's size in sectors, for range checks. Zero to skip them.</param>
    public static Result<PartitionTable> Read(Stream disk, ReadOnlySpan<byte> sector0, ulong diskSectors)
    {
        ArgumentNullException.ThrowIfNull(disk);

        int blockSize = DiskIo.BytesPerSector;

        if (HasDriverDescriptorMap(sector0))
        {
            ushort declared = BinaryPrimitives.ReadUInt16BigEndian(sector0[2..]);

            if (declared != 0)
            {
                if (declared < DiskIo.BytesPerSector
                    || declared % DiskIo.BytesPerSector != 0
                    || declared > 4096)
                {
                    return Result<PartitionTable>.Failure(DmgError.Corrupt(
                        "This image's driver descriptor map declares a block size this build cannot use.",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"sbBlkSize={declared}; it must be a multiple of {DiskIo.BytesPerSector} and no more than 4096.")));
                }

                blockSize = declared;
            }
        }

        int sectorsPerBlock = blockSize / DiskIo.BytesPerSector;

        Result<byte[]> first = DiskIo.ReadSectors(
            disk,
            sectorsPerBlock,
            sectorsPerBlock,
            "the first Apple partition map entry");

        if (!first.TryGetValue(out byte[]? firstEntry))
        {
            return first.CastFailure<PartitionTable>();
        }

        if (!HasMapEntry(firstEntry))
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image has a driver descriptor map but no Apple partition map behind it.",
                $"The PM signature is missing from block 1 of a {blockSize}-byte-block disk."));
        }

        uint declaredEntries = BinaryPrimitives.ReadUInt32BigEndian(firstEntry.AsSpan(4));

        if (declaredEntries == 0 || declaredEntries > MaxEntryCount)
        {
            return Result<PartitionTable>.Failure(DmgError.Corrupt(
                "This image's Apple partition map declares an impossible number of entries.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pmMapBlkCnt={declaredEntries}; this build reads between 1 and {MaxEntryCount}.")));
        }

        List<PartitionEntry> partitions = [];

        for (uint index = 0; index < declaredEntries; index++)
        {
            byte[] raw;

            if (index == 0)
            {
                raw = firstEntry;
            }
            else
            {
                Result<byte[]> next = DiskIo.ReadSectors(
                    disk,
                    (long)(index + 1) * sectorsPerBlock,
                    sectorsPerBlock,
                    string.Create(CultureInfo.InvariantCulture, $"Apple partition map entry {index + 1}"));

                if (!next.TryGetValue(out byte[]? bytes))
                {
                    return next.CastFailure<PartitionTable>();
                }

                raw = bytes;
            }

            if (!HasMapEntry(raw))
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's Apple partition map ends before it said it would.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} of {declaredEntries} has no PM signature.")));
            }

            uint start = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8));
            uint blocks = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12));
            string name = ReadFixedString(raw.AsSpan(16, 32));
            string type = ReadFixedString(raw.AsSpan(48, 32));

            ulong startSector = (ulong)start * (ulong)sectorsPerBlock;
            ulong sectorCount = (ulong)blocks * (ulong)sectorsPerBlock;

            if (diskSectors > 0 && startSector + sectorCount > diskSectors)
            {
                return Result<PartitionTable>.Failure(DmgError.Corrupt(
                    "This image's Apple partition map describes a partition that runs off the end of the disk.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Entry {index + 1} ({type}) covers sectors {startSector}-{startSector + sectorCount - 1} "
                        + $"of a {diskSectors}-sector disk.")));
            }

            partitions.Add(new PartitionEntry
            {
                Number = partitions.Count + 1,
                Scheme = PartitionScheme.ApplePartitionMap,
                StartSector = startSector,
                SectorCount = sectorCount,
                TypeName = string.IsNullOrEmpty(type) ? "Unnamed Apple partition" : type,
                Name = name,
                IsFreeSpace = string.Equals(type, FreeSpaceType, StringComparison.Ordinal),
            });
        }

        return Result<PartitionTable>.Success(
            new PartitionTable(PartitionScheme.ApplePartitionMap, partitions)
            {
                DiskSectors = diskSectors,
            });
    }

    /// <summary>Reads one of the map's NUL-padded ASCII fields.</summary>
    /// <param name="field">The fixed-width field.</param>
    private static string ReadFixedString(ReadOnlySpan<byte> field)
    {
        int length = field.IndexOf((byte)0);
        ReadOnlySpan<byte> text = length >= 0 ? field[..length] : field;

        // The map's fields are ASCII by definition. Anything that is not is
        // damage, and is dropped rather than turned into replacement characters.
        Span<char> characters = stackalloc char[text.Length];
        int written = 0;

        foreach (byte value in text)
        {
            if (value >= 0x20 && value < 0x7F)
            {
                characters[written++] = (char)value;
            }
        }

        return new string(characters[..written]).Trim();
    }
}
