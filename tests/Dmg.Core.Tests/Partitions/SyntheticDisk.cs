using System.Buffers.Binary;
using System.Text;

namespace Dmg.Core.Tests.Partitions;

/// <summary>One entry of a synthetic master boot record.</summary>
/// <param name="Type">The partition type byte.</param>
/// <param name="Start">The first LBA.</param>
/// <param name="Count">The length in sectors.</param>
/// <param name="Status">The status byte - 0x80 for bootable.</param>
internal readonly record struct MbrPartition(byte Type, uint Start, uint Count, byte Status = 0);

/// <summary>One entry of a synthetic GUID partition table.</summary>
/// <param name="TypeGuid">The partition type GUID.</param>
/// <param name="Start">The first LBA.</param>
/// <param name="End">The last LBA, inclusive.</param>
/// <param name="Name">The partition name, up to 36 UTF-16 code units.</param>
internal readonly record struct GptPartition(string TypeGuid, ulong Start, ulong End, string Name);

/// <summary>
/// Disks built byte by byte, so a test can say exactly what is wrong with one.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures prove the readers work on what <c>hdiutil</c> writes. These prove
/// what happens when a field is wrong, which no fixture can: a real corpus has no
/// image with a broken GPT checksum in it, and manufacturing one by hand is the
/// only way to check that the checksum is looked at.
/// </para>
/// <para>
/// The CRC-32 here is written out again rather than reused from
/// <c>Dmg.Core.Partitions</c>. Checking a checksum against the same routine that
/// produced it proves nothing; this one is an independent implementation, and the
/// fixture tests then check both against Apple's.
/// </para>
/// </remarks>
internal static class SyntheticDisk
{
    /// <summary>The sector size everything here uses.</summary>
    internal const int SectorSize = 512;

    /// <summary>A disk of <paramref name="sectors"/> zeroed sectors.</summary>
    /// <param name="sectors">How many sectors the disk holds.</param>
    internal static byte[] Blank(int sectors) => new byte[sectors * SectorSize];

    /// <summary>A disk whose sector 0 is a master boot record describing <paramref name="entries"/>.</summary>
    /// <param name="sectors">The disk's size in sectors.</param>
    /// <param name="entries">The partitions to declare, in slot order.</param>
    internal static byte[] WithMbr(int sectors, params MbrPartition[] entries)
    {
        byte[] disk = Blank(sectors);
        WriteMbr(disk, entries);
        return disk;
    }

    /// <summary>Writes a master boot record into sector 0 of <paramref name="disk"/>.</summary>
    /// <param name="disk">The disk to write into.</param>
    /// <param name="entries">The partitions to declare, in slot order.</param>
    internal static void WriteMbr(byte[] disk, params MbrPartition[] entries)
    {
        for (int index = 0; index < entries.Length && index < 4; index++)
        {
            Span<byte> slot = disk.AsSpan(446 + (index * 16), 16);
            slot[0] = entries[index].Status;
            slot[1] = 0xFE;
            slot[2] = 0xFF;
            slot[3] = 0xFF;
            slot[4] = entries[index].Type;
            slot[5] = 0xFE;
            slot[6] = 0xFF;
            slot[7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], entries[index].Start);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], entries[index].Count);
        }

        disk[510] = 0x55;
        disk[511] = 0xAA;
    }

    /// <summary>
    /// A disk with a protective master boot record, a primary GPT header at LBA 1
    /// and a 128-entry array at LBA 2, both correctly checksummed.
    /// </summary>
    /// <param name="sectors">The disk's size in sectors.</param>
    /// <param name="entries">The partitions to declare.</param>
    internal static byte[] WithGpt(int sectors, params GptPartition[] entries)
    {
        byte[] disk = Blank(sectors);

        WriteMbr(disk, new MbrPartition(0xEE, 1, (uint)Math.Min(sectors - 1, uint.MaxValue)));

        const int entryCount = 128;
        const int entrySize = 128;

        Span<byte> array = disk.AsSpan(2 * SectorSize, entryCount * entrySize);

        for (int index = 0; index < entries.Length; index++)
        {
            GptPartition partition = entries[index];
            Span<byte> slot = array.Slice(index * entrySize, entrySize);

            Guid.Parse(partition.TypeGuid).TryWriteBytes(slot);
            Guid.NewGuid().TryWriteBytes(slot[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], partition.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], partition.End);
            Encoding.Unicode.GetBytes(partition.Name).CopyTo(slot[56..]);
        }

        Span<byte> header = disk.AsSpan(SectorSize, SectorSize);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)sectors - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 34);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)sectors - 34);
        Guid.NewGuid().TryWriteBytes(header.Slice(56, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc32(array));

        ResealGptHeader(disk);

        return disk;
    }

    /// <summary>Recomputes the GPT header checksum after a test has damaged a field.</summary>
    /// <param name="disk">The disk whose header at LBA 1 should be resealed.</param>
    internal static void ResealGptHeader(byte[] disk)
    {
        Span<byte> header = disk.AsSpan(SectorSize, SectorSize);
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32(header[..headerSize]));
    }

    /// <summary>A seekable stream over a synthetic disk.</summary>
    /// <param name="disk">The disk's bytes.</param>
    internal static MemoryStream Open(byte[] disk) => new(disk, writable: false);

    /// <summary>CRC-32 (reflected IEEE), written out here so the checks are not circular.</summary>
    /// <param name="data">The bytes to checksum.</param>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
