namespace Dmg.Core.Partitions;

/// <summary>
/// The one entry point: hand it a decoded disk, get back what is on it.
/// </summary>
/// <remarks>
/// <para>
/// The order matters. A GPT disk also has a master boot record - a protective one
/// - so the GPT is looked for first and the MBR is only read as a real table when
/// there is no GPT behind it. Everything the reader does not recognise is a
/// refusal, never a guess.
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

        Result<byte[]> head = DiskIo.ReadSectors(disk, 0, 2, "the first two sectors of the disk");

        if (!head.TryGetValue(out byte[]? sectors))
        {
            return head.CastFailure<PartitionTable>();
        }

        ReadOnlySpan<byte> sector0 = sectors.AsSpan(0, DiskIo.BytesPerSector);
        ReadOnlySpan<byte> sector1 = sectors.AsSpan(DiskIo.BytesPerSector, DiskIo.BytesPerSector);

        if (GuidPartitionTable.HasSignature(sector1))
        {
            return GuidPartitionTable.Read(disk, sector1, diskSectors);
        }

        if (MasterBootRecord.IsProtective(sector0))
        {
            // A protective MBR promises a GPT. Its absence is damage, not a disk
            // with one 0xEE partition on it.
            return GuidPartitionTable.Read(disk, sector1, diskSectors);
        }

        if (MasterBootRecord.HasBootSignature(sector0))
        {
            return MasterBootRecord.Read(sector0, diskSectors);
        }

        return Result<PartitionTable>.Failure(DmgError.Corrupt(
            "This image has no partition table that this build recognises.",
            "Sector 0 is neither a master boot record nor a driver descriptor map, and there is no "
            + "GUID partition table header at LBA 1."));
    }
}
