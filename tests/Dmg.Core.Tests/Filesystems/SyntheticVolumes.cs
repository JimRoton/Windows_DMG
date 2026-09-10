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
