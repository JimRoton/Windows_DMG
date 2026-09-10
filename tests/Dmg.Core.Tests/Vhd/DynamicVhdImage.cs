using System.Buffers.Binary;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Reads a dynamic VHD back: parses both footers, the <c>cxsparse</c> header and
/// the block allocation table, checks they agree with each other, and serves the
/// logical disk the file describes.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the test project on purpose. The product writes VHDs and hands
/// them to Windows to read; a reader in <c>src</c> would be code with no caller,
/// and the round-trip suite is the only thing that needs one.
/// </para>
/// <para>
/// <b>It is deliberately suspicious.</b> Every structural claim the file makes -
/// that the two footers agree, that the table points inside the file, that no two
/// blocks overlap, that an allocated block's bitmap says all its sectors are
/// present - is checked rather than assumed, because a round-trip test whose
/// reader shares the writer's misunderstanding proves nothing.
/// </para>
/// </remarks>
internal sealed class DynamicVhdImage : IDisposable
{
    private readonly FileStream _file;

    private DynamicVhdImage(
        FileStream file,
        VhdFooter footer,
        VhdDynamicHeader header,
        uint[] table,
        int allocatedBlocks)
    {
        _file = file;
        Footer = footer;
        Header = header;
        Table = table;
        AllocatedBlocks = allocatedBlocks;
    }

    /// <summary>The footer at the end of the file.</summary>
    internal VhdFooter Footer { get; }

    /// <summary>The dynamic disk header.</summary>
    internal VhdDynamicHeader Header { get; }

    /// <summary>The block allocation table, one entry per block of the disk.</summary>
    internal uint[] Table { get; }

    /// <summary>How many blocks were actually written.</summary>
    internal int AllocatedBlocks { get; }

    /// <summary>How many blocks were left out because they were all zeros.</summary>
    internal int UnallocatedBlocks => Table.Length - AllocatedBlocks;

    /// <summary>The disk's declared capacity.</summary>
    internal long DiskSize => Footer.DiskSize;

    /// <summary>The size of the file on disk, which is the point of the exercise.</summary>
    internal long FileSize => _file.Length;

    public void Dispose() => _file.Dispose();

    /// <summary>
    /// Opens and validates a dynamic VHD.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a well-formed dynamic VHD.</exception>
    internal static DynamicVhdImage Open(string path)
    {
        FileStream file = File.OpenRead(path);

        try
        {
            byte[] tailBytes = ReadAt(file, file.Length - VhdFooter.Length, VhdFooter.Length);
            VhdFooter footer = Parse(VhdFooter.Parse(tailBytes), "the footer");

            if (footer.DiskType != VhdDiskType.Dynamic)
            {
                throw new InvalidDataException($"The footer says {footer.DiskType}, not Dynamic.");
            }

            byte[] copyBytes = ReadAt(file, 0, VhdFooter.Length);

            if (!copyBytes.AsSpan().SequenceEqual(tailBytes))
            {
                throw new InvalidDataException(
                    "The footer copy at the head of the file differs from the footer at the end.");
            }

            if (footer.DataOffset != (ulong)VhdFooter.Length)
            {
                throw new InvalidDataException(
                    $"The footer points at byte {footer.DataOffset} for its dynamic header.");
            }

            VhdDynamicHeader header = Parse(
                VhdDynamicHeader.Parse(ReadAt(file, (long)footer.DataOffset, VhdDynamicHeader.Length)),
                "the dynamic disk header");

            long expectedBlocks = (footer.DiskSize + header.BlockSize - 1) / header.BlockSize;

            if (header.MaxTableEntries != expectedBlocks)
            {
                throw new InvalidDataException(
                    $"The header declares {header.MaxTableEntries} blocks; {expectedBlocks} cover the disk.");
            }

            uint[] table = ReadTable(file, (long)header.TableOffset, header.MaxTableEntries);
            int allocated = Validate(file, header, table);

            return new DynamicVhdImage(file, footer, header, table, allocated);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A forward-only stream over the logical disk: allocated blocks come from the
    /// file, unallocated ones are served as zeros.
    /// </summary>
    internal Stream OpenDisk() => new DiskStream(this);

    /// <summary>Reads the whole logical disk into an array. For small fixtures only.</summary>
    internal byte[] ReadAllBytes()
    {
        byte[] disk = new byte[DiskSize];

        using Stream stream = OpenDisk();
        stream.ReadExactly(disk);

        return disk;
    }

    /// <summary>The absolute file offset of an allocated block's data.</summary>
    private long DataOffsetOf(uint index) =>
        VhdDynamicLayout.BlockOffsetFor(Table[index]) + Header.SectorBitmapBytes;

    private static uint[] ReadTable(FileStream file, long offset, uint entries)
    {
        byte[] bytes = ReadAt(file, offset, checked((int)entries * VhdDynamicHeader.TableEntryLength));
        uint[] table = new uint[entries];

        for (int index = 0; index < table.Length; index++)
        {
            table[index] = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(index * VhdDynamicHeader.TableEntryLength));
        }

        return table;
    }

    /// <summary>
    /// Checks that the allocated blocks sit inside the file, do not overlap each
    /// other, and claim every one of their sectors. Returns how many there are.
    /// </summary>
    private static int Validate(FileStream file, VhdDynamicHeader header, uint[] table)
    {
        long stride = header.BlockStride;
        long firstBlock = VhdDynamicLayout.TableOffset
            + ((table.Length * (long)VhdDynamicHeader.TableEntryLength) + VhdFooter.SectorSize - 1)
            / VhdFooter.SectorSize * VhdFooter.SectorSize;

        List<long> starts = [];

        for (uint index = 0; index < table.Length; index++)
        {
            if (table[index] == VhdDynamicHeader.UnusedBlockEntry)
            {
                continue;
            }

            long start = VhdDynamicLayout.BlockOffsetFor(table[index]);

            if (start < firstBlock || start + stride > file.Length - VhdFooter.Length)
            {
                throw new InvalidDataException(
                    $"Block {index} claims bytes [{start}, {start + stride}) of a {file.Length}-byte file.");
            }

            if ((start - firstBlock) % stride != 0)
            {
                throw new InvalidDataException($"Block {index} is not on a block boundary.");
            }

            byte[] bitmap = ReadAt(file, start, header.SectorBitmapBytes);
            int sectors = header.BlockSize / VhdFooter.SectorSize;

            for (int sector = 0; sector < sectors; sector++)
            {
                // The specification numbers the bits within a byte from the most
                // significant end, so sector 0 is bit 7 of byte 0.
                bool present = (bitmap[sector / 8] & (1 << (7 - (sector % 8)))) != 0;

                if (!present)
                {
                    throw new InvalidDataException(
                        $"Block {index}'s bitmap says sector {sector} was never written.");
                }
            }

            starts.Add(start);
        }

        if (starts.Distinct().Count() != starts.Count)
        {
            throw new InvalidDataException("Two block table entries point at the same block.");
        }

        return starts.Count;
    }

    private static byte[] ReadAt(FileStream file, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > file.Length)
        {
            throw new InvalidDataException(
                $"The file is {file.Length} bytes; {length} were wanted at {offset}.");
        }

        byte[] bytes = new byte[length];

        file.Position = offset;
        file.ReadExactly(bytes);

        return bytes;
    }

    private static T Parse<T>(Result<T> result, string what) =>
        result.TryGetValue(out T? value)
            ? value
            : throw new InvalidDataException($"Could not parse {what}: {result.Error}");

    /// <summary>
    /// Serves the logical disk: file bytes for an allocated block, zeros for one
    /// that was never written.
    /// </summary>
    private sealed class DiskStream(DynamicVhdImage image) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => image.DiskSize;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = image.DiskSize - _position;

            if (left <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            int blockSize = image.Header.BlockSize;
            uint block = (uint)(_position / blockSize);
            int within = (int)(_position % blockSize);

            // One read never crosses a block boundary: past it the answer might come
            // from somewhere completely different in the file, or from nowhere.
            int wanted = (int)Math.Min(Math.Min(buffer.Length, left), blockSize - within);
            Span<byte> slice = buffer[..wanted];

            if (image.Table[block] == VhdDynamicHeader.UnusedBlockEntry)
            {
                slice.Clear();
            }
            else
            {
                image._file.Position = image.DataOffsetOf(block) + within;
                image._file.ReadExactly(slice);
            }

            _position += wanted;

            return wanted;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
