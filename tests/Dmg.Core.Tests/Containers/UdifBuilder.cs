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

    /// <summary>One 40-byte chunk descriptor.</summary>
    internal sealed record Chunk(
        uint Type,
        ulong SectorNumber,
        ulong SectorCount,
        ulong CompressedOffset = 0,
        ulong CompressedLength = 0,
        uint Comment = 0)
    {
        public const uint ZeroFill = 0x00000000;
        public const uint Raw = 0x00000001;
        public const uint Ignore = 0x00000002;
        public const uint Zlib = 0x80000005;
        public const uint Bzip2 = 0x80000006;
        public const uint CommentEntry = 0x7FFFFFFE;
        public const uint Terminator = 0xFFFFFFFF;

        /// <summary>The terminator entry hdiutil writes: type only, at the region's end.</summary>
        public static Chunk End(ulong sectorNumber) => new(Terminator, sectorNumber, 0);
    }

    /// <summary>A mish block whose header fields and chunk table can be perturbed.</summary>
    internal sealed class Mish
    {
        public uint Signature { get; set; } = 0x6D697368;

        public uint Version { get; set; } = 1;

        public ulong FirstSectorNumber { get; set; }

        public ulong SectorCount { get; set; }

        public ulong DataOffset { get; set; }

        public uint BuffersNeeded { get; set; } = 2056;

        public uint BlockDescriptors { get; set; }

        public uint ChecksumType { get; set; } = 2;

        public uint ChecksumSize { get; set; } = 32;

        /// <summary>Written to 0xC8 instead of the real chunk count, when set.</summary>
        public uint? DeclaredChunkCount { get; set; }

        /// <summary>Bytes of junk appended after the chunk table.</summary>
        public int TrailingBytes { get; set; }

        public List<Chunk> Chunks { get; } = [];

        public Mish With(params Chunk[] chunks)
        {
            Chunks.AddRange(chunks);
            return this;
        }

        public byte[] ToArray()
        {
            byte[] block = new byte[0xCC + (Chunks.Count * 40) + TrailingBytes];

            PutUInt32(block, 0x00, Signature);
            PutUInt32(block, 0x04, Version);
            PutUInt64(block, 0x08, FirstSectorNumber);
            PutUInt64(block, 0x10, SectorCount);
            PutUInt64(block, 0x18, DataOffset);
            PutUInt32(block, 0x20, BuffersNeeded);
            PutUInt32(block, 0x24, BlockDescriptors);
            PutUInt32(block, 0x40, ChecksumType);
            PutUInt32(block, 0x44, ChecksumSize);
            PutUInt32(block, 0xC8, DeclaredChunkCount ?? (uint)Chunks.Count);

            for (int index = 0; index < Chunks.Count; index++)
            {
                int offset = 0xCC + (index * 40);
                Chunk chunk = Chunks[index];

                PutUInt32(block, offset + 0x00, chunk.Type);
                PutUInt32(block, offset + 0x04, chunk.Comment);
                PutUInt64(block, offset + 0x08, chunk.SectorNumber);
                PutUInt64(block, offset + 0x10, chunk.SectorCount);
                PutUInt64(block, offset + 0x18, chunk.CompressedOffset);
                PutUInt64(block, offset + 0x20, chunk.CompressedLength);
            }

            return block;
        }
    }

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
