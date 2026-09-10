using System.Buffers.Binary;
using System.Text;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// Volumes built field by field: boot sectors, FATs and directory entries that
/// say exactly what a test needs them to say.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are the ground truth for what the formats look like, and the
/// fixture tests check the probes against them. What a fixture cannot do is be
/// wrong on purpose - there is no hdiutil flag for "an exFAT volume whose label
/// entry is marked not-in-use", or "a root directory chain that loops". These
/// builders exist for those, and every field they write was read out of a real
/// image first.
/// </para>
/// <para>
/// The geometry is deliberately the one hdiutil produces for a small exFAT
/// volume: 512-byte sectors, 8-sector clusters, one FAT of 128 sectors starting
/// at sector 128, the cluster heap at 256, and the root directory at cluster 5.
/// </para>
/// </remarks>
internal static class SyntheticVolumes
{
    /// <summary>The sector size every builder here uses.</summary>
    internal const int SectorSize = 512;

    /// <summary>Sectors per cluster, matching the fixtures' 4 KB clusters.</summary>
    internal const int SectorsPerCluster = 8;

    /// <summary>The sector the FAT starts at.</summary>
    internal const int FatOffsetSectors = 128;

    /// <summary>The FAT's length in sectors.</summary>
    internal const int FatLengthSectors = 128;

    /// <summary>The sector the cluster heap starts at.</summary>
    internal const int ClusterHeapOffsetSectors = 256;

    /// <summary>The cluster the root directory starts at.</summary>
    internal const uint RootDirectoryCluster = 5;

    /// <summary>The volume serial the exFAT builder writes, as it appears on disk.</summary>
    internal const uint ExfatSerial = 0x6AA2D346;

    /// <summary>
    /// An exFAT volume of <paramref name="sectors"/> sectors carrying
    /// <paramref name="label"/> as its volume label directory entry.
    /// </summary>
    /// <param name="sectors">The volume's size in sectors.</param>
    /// <param name="label">The volume label, or null for a volume with none.</param>
    internal static byte[] ExfatDisk(int sectors, string? label)
    {
        byte[] volume = new byte[sectors * SectorSize];
        uint clusterCount = (uint)((sectors - ClusterHeapOffsetSectors) / SectorsPerCluster);

        Span<byte> boot = volume.AsSpan(0, SectorSize);
        boot[0] = 0xEB;
        boot[1] = 0x76;
        boot[2] = 0x90;
        "EXFAT   "u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[64..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[72..], (ulong)sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[80..], FatOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[84..], FatLengthSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[88..], ClusterHeapOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[92..], clusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[96..], RootDirectoryCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[100..], ExfatSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[104..], 0x0100);
        boot[108] = 9;
        boot[109] = 3;
        boot[110] = 1;
        boot[111] = 0x80;
        boot[112] = 0xFF;
        boot[510] = 0x55;
        boot[511] = 0xAA;

        Span<byte> fat = volume.AsSpan(FatOffsetSectors * SectorSize, FatLengthSectors * SectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fat[(int)(RootDirectoryCluster * 4)..],
            0xFFFFFFFF);

        if (label is not null)
        {
            Span<byte> entry = volume.AsSpan(ClusterOffset(RootDirectoryCluster), 32);
            entry[0] = 0x83;
            entry[1] = (byte)label.Length;
            Encoding.Unicode.GetBytes(label).CopyTo(entry[2..]);
        }

        return volume;
    }

    /// <summary>The byte offset of an exFAT cluster in a volume this builder made.</summary>
    /// <param name="cluster">The cluster number, two-based as exFAT counts them.</param>
    internal static int ClusterOffset(uint cluster) =>
        (ClusterHeapOffsetSectors + ((int)(cluster - 2) * SectorsPerCluster)) * SectorSize;

    /// <summary>The volume serial the FAT builders write, as it appears on disk.</summary>
    internal const uint FatSerial = 0x0844AF12;

    /// <summary>Reserved sectors in the FAT32 volumes this builder makes.</summary>
    internal const int Fat32ReservedSectors = 32;

    /// <summary>The size of one FAT in the FAT32 volumes this builder makes.</summary>
    internal const int Fat32FatSectors = 520;

    /// <summary>
    /// The size of the FAT32 volumes this builder makes.
    /// </summary>
    /// <remarks>
    /// FAT has no type field: a volume is FAT32 because it has at least 65,525
    /// data clusters, so a synthetic FAT32 volume has to actually be that big.
    /// With one sector per cluster that is a little over 34 MB, which is the
    /// smallest honest FAT32 volume there is.
    /// </remarks>
    internal const int Fat32Sectors = 67000;

    /// <summary>A FAT32 volume carrying <paramref name="label"/> in its root directory.</summary>
    /// <param name="label">The volume label, or null for a volume with no label entry.</param>
    /// <param name="bootSectorLabel">The label copy to write into the boot sector, or null for none.</param>
    internal static byte[] Fat32Disk(string? label, string? bootSectorLabel = null)
    {
        byte[] volume = new byte[(long)Fat32Sectors * SectorSize];

        Span<byte> boot = volume.AsSpan(0, SectorSize);
        boot[0] = 0xEB;
        boot[1] = 0x58;
        boot[2] = 0x90;
        "BSD  4.4"u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], SectorSize);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], Fat32ReservedSectors);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], 0);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], Fat32Sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], Fat32FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], 2);
        boot[66] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(boot[67..], FatSerial);
        WriteShortName(boot.Slice(71, 11), bootSectorLabel);
        "FAT32   "u8.CopyTo(boot[82..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        Span<byte> fat = volume.AsSpan(Fat32ReservedSectors * SectorSize, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[8..], 0x0FFFFFFF);

        if (label is not null)
        {
            Span<byte> entry = volume.AsSpan(Fat32RootDirectoryOffset, 32);
            WriteShortName(entry[..11], label);
            entry[11] = 0x28;
        }

        return volume;
    }

    /// <summary>The byte offset of the root directory in a FAT32 volume this builder made.</summary>
    internal static int Fat32RootDirectoryOffset =>
        (Fat32ReservedSectors + (2 * Fat32FatSectors)) * SectorSize;

    /// <summary>A FAT16 volume carrying <paramref name="label"/> in its fixed root directory.</summary>
    /// <param name="label">The volume label, or null for a volume with no label entry.</param>
    internal static byte[] Fat16Disk(string? label)
    {
        const int reserved = 1;
        const int fatSectors = 32;
        const int rootEntries = 512;
        const int sectors = 20000;

        byte[] volume = new byte[sectors * SectorSize];

        Span<byte> boot = volume.AsSpan(0, SectorSize);
        boot[0] = 0xEB;
        boot[1] = 0x3C;
        boot[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], SectorSize);
        boot[13] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], reserved);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], rootEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], sectors);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], fatSectors);
        boot[38] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(boot[39..], FatSerial);
        WriteShortName(boot.Slice(43, 11), null);
        "FAT16   "u8.CopyTo(boot[54..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        if (label is not null)
        {
            Span<byte> entry = volume.AsSpan((reserved + (2 * fatSectors)) * SectorSize, 32);
            WriteShortName(entry[..11], label);
            entry[11] = 0x08;
        }

        return volume;
    }

    /// <summary>
    /// An NTFS volume whose <c>$Volume</c> record carries <paramref name="label"/>.
    /// </summary>
    /// <remarks>
    /// hdiutil cannot author NTFS, so there is no fixture for this and the record
    /// is built here to the documented layout: a FILE record at MFT record 3, with
    /// an update sequence array and one resident $VOLUME_NAME attribute.
    /// </remarks>
    /// <param name="label">The volume label, or null for a record with no name attribute.</param>
    internal static byte[] NtfsDisk(string? label)
    {
        const int sectors = 4096;
        const int sectorsPerCluster = 8;
        const int clusterBytes = sectorsPerCluster * SectorSize;
        const int mftCluster = 4;
        const int recordSize = 1024;

        byte[] volume = new byte[sectors * SectorSize];

        Span<byte> boot = volume.AsSpan(0, SectorSize);
        boot[0] = 0xEB;
        boot[1] = 0x52;
        boot[2] = 0x90;
        "NTFS    "u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], SectorSize);
        boot[13] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt64LittleEndian(boot[40..], sectors - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[48..], mftCluster);
        boot[64] = unchecked((byte)-10);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[72..], 0x1234567890ABCDEFul);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        int recordOffset = (mftCluster * clusterBytes) + (3 * recordSize);
        Span<byte> record = volume.AsSpan(recordOffset, recordSize);

        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record[20..], 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record[22..], 1);

        int attribute = 56;

        if (label is not null)
        {
            byte[] name = Encoding.Unicode.GetBytes(label);
            int length = 24 + name.Length + ((24 + name.Length) % 8 == 0 ? 0 : 8 - ((24 + name.Length) % 8));

            BinaryPrimitives.WriteUInt32LittleEndian(record[attribute..], 0x60);
            BinaryPrimitives.WriteUInt32LittleEndian(record[(attribute + 4)..], (uint)length);
            record[attribute + 8] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(record[(attribute + 16)..], (uint)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[(attribute + 20)..], 24);
            name.CopyTo(record[(attribute + 24)..]);

            attribute += length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record[attribute..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(record[24..], (uint)(attribute + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(record[28..], recordSize);

        // The update sequence: every sector of the record ends in the sequence
        // number, with the bytes it displaced kept in the array.
        const ushort sequence = 0x0007;
        BinaryPrimitives.WriteUInt16LittleEndian(record[48..], sequence);

        for (int sector = 0; sector < (recordSize / SectorSize); sector++)
        {
            int tail = ((sector + 1) * SectorSize) - 2;
            record.Slice(tail, 2).CopyTo(record.Slice(50 + (sector * 2), 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record[tail..], sequence);
        }

        return volume;
    }

    /// <summary>Writes one of FAT's 11-byte space-padded names.</summary>
    private static void WriteShortName(Span<byte> field, string? name)
    {
        field.Fill(0x20);

        if (name is null)
        {
            return;
        }

        for (int index = 0; index < name.Length && index < field.Length; index++)
        {
            field[index] = (byte)name[index];
        }
    }

    /// <summary>An HFS+ volume: the volume header at offset 1024, which is all the probe reads.</summary>
    /// <param name="sectors">The volume's size in sectors.</param>
    internal static byte[] HfsPlusDisk(int sectors)
    {
        byte[] volume = new byte[sectors * SectorSize];
        Span<byte> header = volume.AsSpan(1024);

        BinaryPrimitives.WriteUInt16BigEndian(header, 0x482B);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(header[40..], 4096);
        BinaryPrimitives.WriteUInt32BigEndian(header[44..], (uint)(sectors / 8));
        BinaryPrimitives.WriteUInt32BigEndian(header[48..], (uint)(sectors / 16));

        return volume;
    }

    /// <summary>An APFS container: the NXSB superblock at block 0.</summary>
    /// <param name="sectors">The volume's size in sectors.</param>
    internal static byte[] ApfsDisk(int sectors)
    {
        byte[] volume = new byte[sectors * SectorSize];
        Span<byte> superblock = volume.AsSpan(0);

        "NXSB"u8.CopyTo(superblock[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[36..], 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(superblock[40..], (ulong)(sectors / 8));

        return volume;
    }
}
