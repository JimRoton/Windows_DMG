using System.IO.Compression;
using System.Text;
using Dmg.Core.Tests.Containers;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// A whole UDIF image assembled in memory, together with the sector stream it is
/// supposed to decode to.
/// </summary>
/// <param name="Bytes">The complete <c>.dmg</c>: data fork, property list, trailer.</param>
/// <param name="Decoded">
/// What reading the whole of <see cref="DmgBlockStream"/> must produce, byte for
/// byte. This is the point of the class - an assertion against a known answer
/// rather than against our own decoder.
/// </param>
/// <param name="ChunkLengths">The decoded byte length of each chunk, in disk order.</param>
internal sealed record SyntheticUdifImage(
    byte[] Bytes,
    byte[] Decoded,
    IReadOnlyList<int> ChunkLengths)
{
    /// <summary>The decoded disk size in bytes.</summary>
    internal int Length => Decoded.Length;

    /// <summary>A fresh seekable stream over the image.</summary>
    internal MemoryStream OpenSource() => new(Bytes, writable: false);

    /// <summary>Where chunk <paramref name="ordinal"/> starts on the decoded disk.</summary>
    internal int ChunkStart(int ordinal)
    {
        int start = 0;

        for (int index = 0; index < ordinal; index++)
        {
            start += ChunkLengths[index];
        }

        return start;
    }
}

/// <summary>
/// Builds UDIF images to order - a chosen sequence of chunk types and sizes,
/// across a chosen number of block map regions - so the block stream can be tested
/// on layouts no fixture happens to contain.
/// </summary>
/// <remarks>
/// <para>
/// The hdiutil corpus proves we agree with Apple on real images. It cannot prove
/// the boundary arithmetic, because it contains whatever chunk sizes hdiutil felt
/// like writing and no way to ask for a two-sector chunk between two large ones.
/// This builder is how the edge cases in S5.3 get exercised, and it is how the
/// whole suite still means something on a machine with no fixtures at all.
/// </para>
/// <para>
/// Sector content is derived from the <b>absolute</b> sector number, so a read
/// that returns the right number of bytes from the wrong place fails loudly rather
/// than passing. It is deliberately repetitive within a sector so that zlib has
/// something to compress.
/// </para>
/// </remarks>
internal static class SyntheticUdif
{
    /// <summary>The sector size, as the format defines it.</summary>
    internal const int BytesPerSector = 512;

    /// <summary>One chunk to build: its codec and how many sectors it covers.</summary>
    /// <param name="Type">A <c>ChunkEntryType</c> wire value.</param>
    /// <param name="SectorCount">Sectors covered once decoded.</param>
    internal readonly record struct ChunkSpec(uint Type, int SectorCount)
    {
        /// <summary>A zlib-compressed chunk.</summary>
        internal static ChunkSpec Zlib(int sectors) =>
            new(UdifBuilder.Chunk.Zlib, sectors);

        /// <summary>An uncompressed chunk.</summary>
        internal static ChunkSpec Raw(int sectors) =>
            new(UdifBuilder.Chunk.Raw, sectors);

        /// <summary>A zero-fill chunk: nothing stored, zeros produced.</summary>
        internal static ChunkSpec Zero(int sectors) =>
            new(UdifBuilder.Chunk.ZeroFill, sectors);

        /// <summary>An ignore / free chunk: nothing stored, zeros produced.</summary>
        internal static ChunkSpec Ignore(int sectors) =>
            new(UdifBuilder.Chunk.Ignore, sectors);

        /// <summary>A chunk in a codec this build does not implement.</summary>
        internal static ChunkSpec Bzip2(int sectors) =>
            new(UdifBuilder.Chunk.Bzip2, sectors);
    }

    /// <summary>Builds a single-region image from <paramref name="chunks"/>.</summary>
    internal static SyntheticUdifImage Build(params ChunkSpec[] chunks) =>
        Build(chunks, regionCount: 1);

    /// <summary>
    /// Builds an image whose chunks are spread over <paramref name="regionCount"/>
    /// block map regions, as evenly as they divide.
    /// </summary>
    /// <remarks>
    /// More than one region matters because a chunk's <c>SectorNumber</c> is
    /// relative to its own region. An image with one region hides every bug in that
    /// arithmetic, and the first region usually starts at sector zero, so the
    /// mistake stays hidden right up until it does not.
    /// </remarks>
    internal static SyntheticUdifImage Build(
        IReadOnlyList<ChunkSpec> chunks,
        int regionCount)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentOutOfRangeException.ThrowIfLessThan(regionCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(regionCount, chunks.Count);

        var dataFork = new MemoryStream();
        var decoded = new MemoryStream();
        List<int> chunkLengths = new(chunks.Count);
        List<byte[]> regionBlocks = [];

        int perRegion = (chunks.Count + regionCount - 1) / regionCount;
        int chunkIndex = 0;
        ulong absoluteSector = 0;

        for (int region = 0; region < regionCount; region++)
        {
            int take = Math.Min(perRegion, chunks.Count - chunkIndex);

            if (take <= 0)
            {
                break;
            }

            UdifBuilder.Mish mish = new()
            {
                FirstSectorNumber = absoluteSector,
            };

            ulong regionSectors = 0;

            for (int offset = 0; offset < take; offset++)
            {
                ChunkSpec spec = chunks[chunkIndex + offset];
                byte[] plain = SectorContent(absoluteSector, spec.SectorCount);

                decoded.Write(spec.Type is UdifBuilder.Chunk.ZeroFill or UdifBuilder.Chunk.Ignore
                    ? new byte[plain.Length]
                    : plain);

                ulong compressedOffset = (ulong)dataFork.Length;
                byte[] stored = Store(spec.Type, plain);
                dataFork.Write(stored);

                mish.Chunks.Add(new UdifBuilder.Chunk(
                    spec.Type,
                    SectorNumber: regionSectors,
                    SectorCount: (ulong)spec.SectorCount,
                    CompressedOffset: compressedOffset,
                    CompressedLength: (ulong)stored.Length));

                chunkLengths.Add(plain.Length);
                regionSectors += (ulong)spec.SectorCount;
                absoluteSector += (ulong)spec.SectorCount;
            }

            mish.SectorCount = regionSectors;
            mish.Chunks.Add(UdifBuilder.Chunk.End(regionSectors));
            regionBlocks.Add(mish.ToArray());

            chunkIndex += take;
        }

        byte[] xml = Encoding.UTF8.GetBytes(Plist(regionBlocks));
        byte[] fork = dataFork.ToArray();

        UdifBuilder.Koly koly = new()
        {
            DataForkOffset = 0,
            DataForkLength = (ulong)fork.Length,
            XmlOffset = (ulong)fork.Length,
            XmlLength = (ulong)xml.Length,
            SectorCount = absoluteSector,
        };

        var image = new MemoryStream();
        image.Write(fork);
        image.Write(xml);
        image.Write(koly.ToArray());

        return new SyntheticUdifImage(image.ToArray(), decoded.ToArray(), chunkLengths);
    }

    /// <summary>
    /// The bytes of <paramref name="sectorCount"/> sectors starting at
    /// <paramref name="firstSector"/>: unique to their position, and compressible.
    /// </summary>
    internal static byte[] SectorContent(ulong firstSector, int sectorCount)
    {
        byte[] buffer = new byte[sectorCount * BytesPerSector];

        for (int sector = 0; sector < sectorCount; sector++)
        {
            ulong absolute = firstSector + (ulong)sector;
            Span<byte> target = buffer.AsSpan(sector * BytesPerSector, BytesPerSector);

            // Sixteen bytes naming the sector, repeated to fill it. Unique per
            // sector so a misplaced read is visible, repetitive so zlib has work
            // to do and the compressed chunk is genuinely smaller than the plain
            // one.
            Span<byte> unit = target[..16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(unit, absolute);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
                unit[8..],
                0xA5A5_0000_0000_5A5AUL ^ absolute);

            for (int offset = 16; offset < BytesPerSector; offset += 16)
            {
                unit.CopyTo(target[offset..]);
            }
        }

        return buffer;
    }

    /// <summary>Encodes a chunk's payload the way its entry type says it is stored.</summary>
    private static byte[] Store(uint entryType, byte[] plain) => entryType switch
    {
        UdifBuilder.Chunk.ZeroFill or UdifBuilder.Chunk.Ignore => [],
        UdifBuilder.Chunk.Raw => plain,
        UdifBuilder.Chunk.Zlib => Deflate(plain),

        // A codec we cannot produce either. The bytes are never decoded - the point
        // of such a chunk is that the read is refused before it gets that far - so
        // a recognisable placeholder is enough.
        _ => Encoding.ASCII.GetBytes("not really compressed"),
    };

    /// <summary>zlib (RFC 1950), which is what a UDZO chunk holds.</summary>
    private static byte[] Deflate(byte[] plain)
    {
        var compressed = new MemoryStream();

        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        return compressed.ToArray();
    }

    /// <summary>The XML property list hdiutil would have written for these regions.</summary>
    private static string Plist(IReadOnlyList<byte[]> regions)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>");
        builder.Append("<key>resource-fork</key><dict><key>blkx</key><array>");

        for (int index = 0; index < regions.Count; index++)
        {
            builder.Append("<dict>");
            builder.Append("<key>Attributes</key><string>0x0050</string>");
            builder.Append($"<key>CFName</key><string>region {index}</string>");
            builder.Append($"<key>Name</key><string>region {index}</string>");
            builder.Append($"<key>ID</key><string>{index}</string>");
            builder.Append("<key>Data</key><data>");
            builder.Append(Convert.ToBase64String(regions[index]));
            builder.Append("</data>");
            builder.Append("</dict>");
        }

        builder.Append("</array><key>plst</key><array/></dict></dict></plist>");
        return builder.ToString();
    }
}
