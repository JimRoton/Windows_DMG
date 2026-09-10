using System.Buffers.Binary;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// Builds UDIF structures byte by byte so the parsers can be pointed at images
/// that no tool would ever produce: lying lengths, offsets past the end of the
/// file, structures truncated at each field boundary.
/// </summary>
/// <remarks>
/// Fixtures made by <c>hdiutil</c> prove the happy path. They cannot prove the
/// hostile ones, because <c>hdiutil</c> will not write a corrupt image on request.
/// </remarks>
internal static class UdifBuilder
{
    /// <summary>Writes a big-endian <see cref="uint"/> at <paramref name="offset"/>.</summary>
    public static void PutUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);

    /// <summary>Writes a big-endian <see cref="ulong"/> at <paramref name="offset"/>.</summary>
    public static void PutUInt64(byte[] buffer, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset), value);

    /// <summary>A koly trailer whose fields can each be perturbed independently.</summary>
    internal sealed class Koly
    {
        public uint Signature { get; set; } = 0x6B6F6C79;

        public uint Version { get; set; } = 4;

        public uint HeaderSize { get; set; } = 512;

        public uint Flags { get; set; } = 1;

        public ulong RunningDataForkOffset { get; set; }

        public ulong DataForkOffset { get; set; }

        public ulong DataForkLength { get; set; }

        public uint SegmentNumber { get; set; } = 1;

        public uint SegmentCount { get; set; } = 1;

        public ulong XmlOffset { get; set; }

        public ulong XmlLength { get; set; }

        public uint ImageVariant { get; set; } = 1;

        public ulong SectorCount { get; set; } = 1;

        public byte[] ToArray()
        {
            byte[] trailer = new byte[512];

            PutUInt32(trailer, 0x000, Signature);
            PutUInt32(trailer, 0x004, Version);
            PutUInt32(trailer, 0x008, HeaderSize);
            PutUInt32(trailer, 0x00C, Flags);
            PutUInt64(trailer, 0x010, RunningDataForkOffset);
            PutUInt64(trailer, 0x018, DataForkOffset);
            PutUInt64(trailer, 0x020, DataForkLength);
            PutUInt32(trailer, 0x038, SegmentNumber);
            PutUInt32(trailer, 0x03C, SegmentCount);
            PutUInt64(trailer, 0x0D8, XmlOffset);
            PutUInt64(trailer, 0x0E0, XmlLength);
            PutUInt32(trailer, 0x1E8, ImageVariant);
            PutUInt64(trailer, 0x1EC, SectorCount);

            return trailer;
        }
    }
}
