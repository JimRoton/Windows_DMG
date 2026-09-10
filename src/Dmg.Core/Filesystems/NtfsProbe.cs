using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dmg.Core.Filesystems;

/// <summary>
/// NTFS: recognised from its boot sector, labelled from the <c>$Volume</c> record
/// in the master file table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The label costs a record read.</b> NTFS keeps nothing user-facing in the
/// boot sector - no label, only a serial - so the name comes from the
/// <c>$VOLUME_NAME</c> attribute of MFT record 3. That means finding the MFT,
/// reading one record, undoing its update sequence fixups and walking its
/// attribute list. It is the only way to answer "which volume is this" for an
/// NTFS image, so it is worth the reads.
/// </para>
/// <para>
/// <b>Fixups are not optional.</b> Every record has the last two bytes of each of
/// its sectors replaced by a sequence number, with the originals kept in an array
/// at the start of the record. Skipping the repair leaves two corrupted bytes per
/// sector, which is exactly the kind of damage that reads as a plausible but wrong
/// attribute length.
/// </para>
/// <para>
/// No fixture exercises this path: hdiutil cannot author NTFS, so the corpus has
/// no NTFS image to check against. Everything here is verified against volumes
/// this suite builds to the documented layout.
/// </para>
/// </remarks>
internal static class NtfsProbe
{
    /// <summary>The signature at offset 3 of the boot sector.</summary>
    internal static ReadOnlySpan<byte> OemName => "NTFS    "u8;

    /// <summary>The MFT record number of <c>$Volume</c>.</summary>
    internal const int VolumeRecordNumber = 3;

    /// <summary>The attribute type of <c>$VOLUME_NAME</c>.</summary>
    internal const uint VolumeNameAttribute = 0x60;

    /// <summary>The end-of-attribute-list marker.</summary>
    internal const uint EndOfAttributes = 0xFFFFFFFF;

    /// <summary>Reads an NTFS volume's boot sector, serial and label.</summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="boot">The volume's first sector.</param>
    internal static Result<FilesystemInfo> Probe(VolumeReader volume, ReadOnlySpan<byte> boot)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (boot.Length < 512 || !boot.Slice(3, 8).SequenceEqual(OemName))
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume claims to be NTFS but its boot sector does not say so.",
                "The NTFS signature is missing from offset 3."));
        }

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        byte sectorsPerCluster = boot[13];
        ulong totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot[40..]);
        ulong mftCluster = BinaryPrimitives.ReadUInt64LittleEndian(boot[48..]);
        sbyte recordSizeCode = (sbyte)boot[64];
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(boot[72..]);

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || sectorsPerCluster == 0
            || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's NTFS boot sector declares an impossible geometry.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSector={bytesPerSector}, SectorsPerCluster={sectorsPerCluster}.")));
        }

        long bytesPerCluster = (long)bytesPerSector * sectorsPerCluster;

        // A positive code counts clusters per record; a negative one is the base-2
        // logarithm of the record size in bytes, which is what any volume with
        // clusters larger than a record uses.
        long recordSize = recordSizeCode > 0
            ? recordSizeCode * bytesPerCluster
            : 1L << -recordSizeCode;

        if (recordSize is < 512 or > (1 << 20) || recordSize % bytesPerSector != 0)
        {
            return Result<FilesystemInfo>.Failure(DmgError.Corrupt(
                "This volume's NTFS boot sector declares an impossible file record size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ClustersPerFileRecordSegment={recordSizeCode} gives {recordSize} bytes.")));
        }

        Result<string?> label = ReadVolumeName(volume, mftCluster, bytesPerCluster, recordSize, bytesPerSector);

        if (!label.Ok)
        {
            return label.CastFailure<FilesystemInfo>();
        }

        return Result<FilesystemInfo>.Success(new FilesystemInfo
        {
            Kind = FilesystemKind.Ntfs,
            VolumeLabel = label.GetValueOrDefault(),

            // Windows prints the low half of the 64-bit serial, in the same
            // XXXX-XXXX shape as FAT's.
            VolumeSerial = FilesystemInfo.FormatSerial((uint)serial),
            BytesPerSector = bytesPerSector,
            BytesPerCluster = bytesPerCluster,
            VolumeBytes = totalSectors > (ulong)(long.MaxValue / bytesPerSector)
                ? long.MaxValue
                : (long)totalSectors * bytesPerSector,
        });
    }

    /// <summary>Reads the <c>$VOLUME_NAME</c> attribute out of MFT record 3.</summary>
    private static Result<string?> ReadVolumeName(
        VolumeReader volume,
        ulong mftCluster,
        long bytesPerCluster,
        long recordSize,
        int bytesPerSector)
    {
        if (mftCluster == 0 || mftCluster > (ulong)(long.MaxValue / bytesPerCluster))
        {
            return Result<string?>.Failure(DmgError.Corrupt(
                "This volume's NTFS boot sector puts the master file table at an impossible cluster.",
                string.Create(CultureInfo.InvariantCulture, $"MftLcn={mftCluster}.")));
        }

        long offset = ((long)mftCluster * bytesPerCluster) + (VolumeRecordNumber * recordSize);

        if (offset < 0 || offset >= volume.Length)
        {
            // A truncated image - a converted one stops at the last non-zero
            // sector - simply has no record to read. That is not damage.
            return Result<string?>.Success(null);
        }

        Result<byte[]> read = volume.Read(offset, (int)recordSize, "$Volume file record");

        if (!read.TryGetValue(out byte[]? record))
        {
            return read.CastFailure<string?>();
        }

        if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
        {
            return Result<string?>.Failure(DmgError.Corrupt(
                "This volume's NTFS master file table does not start with a file record.",
                "The FILE signature is missing from record 3."));
        }

        Result applied = ApplyFixups(record, bytesPerSector);

        if (!applied.Ok)
        {
            return applied.CastFailure<string?>();
        }

        int attributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));

        while (attributeOffset >= 0 && attributeOffset + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attributeOffset));

            if (type == EndOfAttributes)
            {
                return Result<string?>.Success(null);
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attributeOffset + 4));

            if (length < 16 || attributeOffset + length > record.Length)
            {
                return Result<string?>.Failure(DmgError.Corrupt(
                    "This volume's NTFS $Volume record has an attribute of an impossible length.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Attribute type 0x{type:X} at offset {attributeOffset} declares {length} bytes.")));
            }

            if (type == VolumeNameAttribute && record[attributeOffset + 8] == 0)
            {
                int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attributeOffset + 16));
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attributeOffset + 20));

                if (valueLength < 0
                    || valueOffset < 0
                    || attributeOffset + valueOffset + valueLength > record.Length)
                {
                    return Result<string?>.Failure(DmgError.Corrupt(
                        "This volume's NTFS volume name runs outside its own record.",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"offset={valueOffset}, length={valueLength}, record={record.Length} bytes.")));
                }

                string name = Encoding.Unicode
                    .GetString(record.AsSpan(attributeOffset + valueOffset, valueLength - (valueLength % 2)))
                    .Trim();

                return Result<string?>.Success(name.Length == 0 ? null : name);
            }

            attributeOffset += length;
        }

        return Result<string?>.Success(null);
    }

    /// <summary>
    /// Undoes the update sequence array: the last two bytes of every sector of the
    /// record are put back to the values held at the start of it.
    /// </summary>
    private static Result ApplyFixups(byte[] record, int bytesPerSector)
    {
        int arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        int arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));

        if (arrayCount == 0)
        {
            return Result.Success();
        }

        if (arrayOffset < 42 || arrayOffset + (arrayCount * 2) > record.Length)
        {
            return Result.Failure(DmgError.Corrupt(
                "This volume's NTFS $Volume record has an impossible update sequence array.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"offset={arrayOffset}, count={arrayCount}, record={record.Length} bytes.")));
        }

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(arrayOffset));

        for (int sector = 0; sector < arrayCount - 1; sector++)
        {
            int tail = ((sector + 1) * bytesPerSector) - 2;

            if (tail + 2 > record.Length)
            {
                return Result.Failure(DmgError.Corrupt(
                    "This volume's NTFS $Volume record claims more sectors than it holds.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Sector {sector + 1} ends at {tail + 2} of a {record.Length}-byte record.")));
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(tail)) != sequence)
            {
                return Result.Failure(DmgError.Corrupt(
                    "This volume's NTFS $Volume record fails its update sequence check.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Sector {sector + 1} does not end in the record's sequence number 0x{sequence:X4}.")));
            }

            record.AsSpan(arrayOffset + 2 + (sector * 2), 2).CopyTo(record.AsSpan(tail, 2));
        }

        return Result.Success();
    }
}
