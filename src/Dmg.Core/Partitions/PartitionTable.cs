using System.Globalization;

namespace Dmg.Core.Partitions;

/// <summary>
/// How a decoded disk describes what is on it.
/// </summary>
public enum PartitionScheme
{
    /// <summary>
    /// No partition table at all: the whole stream is one volume. Common for
    /// images made from a single filesystem rather than from a disk.
    /// </summary>
    WholeDisk = 0,

    /// <summary>A DOS master boot record at sector 0.</summary>
    MasterBootRecord = 1,

    /// <summary>A GUID partition table, normally behind a protective MBR.</summary>
    GuidPartitionTable = 2,

    /// <summary>A driver descriptor map plus an Apple partition map.</summary>
    ApplePartitionMap = 3,
}

/// <summary>
/// One region of the disk, as its partition table describes it.
/// </summary>
/// <remarks>
/// <para>
/// The fields here are deliberately the union of what the three schemes carry,
/// because <c>dmg info</c> prints one table whatever the disk uses. A field the
/// scheme does not have is null rather than invented: an MBR partition has no
/// name and no type GUID, a GPT partition has no MBR type byte.
/// </para>
/// <para>
/// <see cref="StartSector"/> and <see cref="SectorCount"/> are in 512-byte
/// sectors and are relative to the start of the decoded disk, so
/// <see cref="ByteOffset"/> can be handed straight to a filesystem probe.
/// </para>
/// </remarks>
public sealed record PartitionEntry
{
    /// <summary>
    /// The number the user sees and passes to <c>--partition</c>. One-based, and
    /// counted over the entries the table actually declares, so it survives an
    /// empty slot in the middle of an MBR.
    /// </summary>
    public required int Number { get; init; }

    /// <summary>The scheme this entry came out of.</summary>
    public required PartitionScheme Scheme { get; init; }

    /// <summary>The first 512-byte sector of the partition.</summary>
    public required ulong StartSector { get; init; }

    /// <summary>The partition's length in 512-byte sectors.</summary>
    public required ulong SectorCount { get; init; }

    /// <summary>
    /// A human-readable type: <c>Apple_HFS</c>, <c>EFI System</c>, <c>exFAT or
    /// NTFS (0x07)</c>. Never blank - an unrecognised type is described by its
    /// raw value.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>The partition's name, where the scheme records one. Empty otherwise.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The GPT partition type GUID, canonical upper case. Null for other schemes.</summary>
    public string? TypeGuid { get; init; }

    /// <summary>The GPT unique partition GUID, canonical upper case. Null for other schemes.</summary>
    public string? UniqueGuid { get; init; }

    /// <summary>The MBR partition type byte. Null for other schemes.</summary>
    public byte? MbrType { get; init; }

    /// <summary>
    /// True when the entry describes free space rather than a volume -
    /// <c>Apple_Free</c>, or the padding an Apple partition map declares around
    /// itself. Never a mount candidate.
    /// </summary>
    public bool IsFreeSpace { get; init; }

    /// <summary>The last sector of the partition, or <see cref="StartSector"/> when it is empty.</summary>
    public ulong EndSector => SectorCount == 0 ? StartSector : StartSector + SectorCount - 1;

    /// <summary>The partition's byte offset from the start of the disk.</summary>
    public long ByteOffset => checked((long)StartSector * DiskIo.BytesPerSector);

    /// <summary>The partition's length in bytes.</summary>
    public long ByteLength => checked((long)SectorCount * DiskIo.BytesPerSector);

    /// <summary>One line, the way <c>dmg info</c> prints it.</summary>
    public override string ToString()
    {
        string name = string.IsNullOrEmpty(Name) ? string.Empty : $" \"{Name}\"";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Number}: {TypeName}{name} sectors {StartSector}-{EndSector} ({SectorCount})");
    }
}

/// <summary>
/// What a decoded disk turned out to contain: a scheme, and the regions it declares.
/// </summary>
/// <param name="Scheme">The partition scheme found.</param>
/// <param name="Partitions">
/// Every declared region in table order, free space included. A whole-disk image
/// yields exactly one entry covering the whole stream.
/// </param>
/// <remarks>
/// A table is only ever produced for a disk the reader understood. A disk that has
/// something at sector 0 that is nearly but not quite a partition table is a
/// <see cref="DmgExitCode.CorruptImage"/> failure instead - never a whole-disk
/// table, because silently treating a damaged GPT as one big volume is how a user
/// ends up handing Windows garbage sectors.
/// </remarks>
public sealed record PartitionTable(PartitionScheme Scheme, IReadOnlyList<PartitionEntry> Partitions)
{
    /// <summary>The GPT disk GUID, canonical upper case. Null for other schemes.</summary>
    public string? DiskGuid { get; init; }

    /// <summary>The disk's size in 512-byte sectors, as measured from the decoded stream.</summary>
    public ulong DiskSectors { get; init; }

    /// <summary>True when there was no partition table and the whole stream is one volume.</summary>
    public bool IsWholeDisk => Scheme == PartitionScheme.WholeDisk;

    /// <summary>The entries that could hold a mountable filesystem - everything but free space.</summary>
    public IEnumerable<PartitionEntry> Volumes => Partitions.Where(entry => !entry.IsFreeSpace);
}
