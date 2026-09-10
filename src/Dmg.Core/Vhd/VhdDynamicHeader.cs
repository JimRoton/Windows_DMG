using System.Buffers.Binary;

namespace Dmg.Core.Vhd;

/// <summary>
/// The 1024-byte <c>cxsparse</c> header that turns a VHD from a flat file into a
/// sparse one: where the block allocation table lives, how many entries it has,
/// and how much disk one entry stands for.
/// </summary>
/// <remarks>
/// <para>
/// A dynamic VHD is a copy of the footer, then this header, then the block
/// allocation table, then whichever blocks actually hold data, then the footer
/// again. A block that is entirely zeros is simply never written and its table
/// entry stays <see cref="UnusedBlockEntry"/>; the reader is required to serve
/// zeros for it. That is the whole trick, and it is why a 48 GB image of a nearly
/// empty volume can cost a few megabytes of scratch instead of 48 GB.
/// </para>
/// <para>
/// <b>The parent fields are all zero, always.</b> They exist for differencing
/// disks - a VHD whose blocks are deltas against another VHD - which this tool
/// does not write and should never be asked to. A stray non-zero parent locator
/// would make Windows go looking for a file that does not exist.
/// </para>
/// <para>
/// <b>Block size is 2 MiB and should stay that way.</b> The specification permits
/// any power of two, but 2 MiB is what every VHD in the wild uses and what the
/// Windows VHD provider is actually exercised against. The field is settable
/// because the tests need small blocks to reach the interesting cases in a
/// kilobyte-sized image, not because a user should change it.
/// </para>
/// </remarks>
public sealed class VhdDynamicHeader
{
    /// <summary>The header's length in bytes: 1024, twice a footer.</summary>
    public const int Length = 1024;

    /// <summary>The eight-byte signature at offset 0.</summary>
    public const string Cookie = "cxsparse";

    /// <summary>The block size this tool writes, and the only one Windows is known to like.</summary>
    public const int DefaultBlockSize = 2 * 1024 * 1024;

    /// <summary>The smallest block size accepted: one sector.</summary>
    public const int MinimumBlockSize = VhdFooter.SectorSize;

    /// <summary>The largest block size accepted: 64 MiB.</summary>
    public const int MaximumBlockSize = 64 * 1024 * 1024;

    /// <summary>
    /// The table entry for a block that was never written: all ones. A reader that
    /// finds it must serve 512 * <see cref="BlockSize"/> / 512 zero bytes.
    /// </summary>
    public const uint UnusedBlockEntry = 0xFFFF_FFFFu;

    /// <summary>The bytes one block allocation table entry occupies.</summary>
    public const int TableEntryLength = sizeof(uint);

    // Version 1.0 of the dynamic header, packed as major:minor in two 16-bit halves.
    private const uint HeaderVersion1 = 0x0001_0000u;

    // "Data Offset" here is reserved and is specified as all ones, unlike the
    // footer's field of the same name, which is where this header's offset goes.
    private const ulong ReservedDataOffset = 0xFFFF_FFFF_FFFF_FFFFuL;

    private const int CookieOffset = 0;
    private const int DataOffsetOffset = 8;
    private const int TableOffsetOffset = 16;
    private const int HeaderVersionOffset = 24;
    private const int MaxTableEntriesOffset = 28;
    private const int BlockSizeOffset = 32;
    private const int ChecksumOffset = 36;
    private const int ParentUniqueIdOffset = 40;

    private VhdDynamicHeader(
        ulong tableOffset,
        uint maxTableEntries,
        int blockSize,
        uint headerVersion,
        ulong dataOffset,
        uint checksum)
    {
        TableOffset = tableOffset;
        MaxTableEntries = maxTableEntries;
        BlockSize = blockSize;
        HeaderVersion = headerVersion;
        DataOffset = dataOffset;
        Checksum = checksum;
    }

    /// <summary>The absolute byte offset of the block allocation table.</summary>
    public ulong TableOffset { get; }

    /// <summary>How many entries the table has - one per block of the disk.</summary>
    public uint MaxTableEntries { get; }

    /// <summary>How many bytes of disk one block holds.</summary>
    public int BlockSize { get; }

    /// <summary>The header's own format version, <c>0x00010000</c>.</summary>
    public uint HeaderVersion { get; }

    /// <summary>The reserved <c>Data Offset</c> field, which is all ones.</summary>
    public ulong DataOffset { get; }

    /// <summary>The stored one's-complement checksum.</summary>
    public uint Checksum { get; }

    /// <summary>
    /// How many bytes of sector bitmap sit in front of each block's data.
    /// </summary>
    /// <remarks>
    /// One bit per sector of the block, rounded up to a whole sector of its own.
    /// For the 2 MiB default that is 4096 bits - exactly 512 bytes, exactly one
    /// sector - which is why the arithmetic looks like it could be a constant. It
    /// cannot: a 512-byte block needs one bit and still costs a whole sector.
    /// </remarks>
    public int SectorBitmapBytes => BitmapBytesFor(BlockSize);

    /// <summary>The bytes one allocated block occupies on disk: bitmap then data.</summary>
    public long BlockStride => SectorBitmapBytes + (long)BlockSize;

    /// <summary>
    /// The sector bitmap length for a block of <paramref name="blockSize"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not a positive multiple of 512.</exception>
    public static int BitmapBytesFor(int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        if (blockSize % VhdFooter.SectorSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockSize),
                blockSize,
                "A VHD block is a whole number of 512-byte sectors.");
        }

        int sectors = blockSize / VhdFooter.SectorSize;
        int bytes = (sectors + 7) / 8;

        return (int)VhdWriter.RoundUpToSector(bytes);
    }

    /// <summary>
    /// Builds the header for a table of <paramref name="maxTableEntries"/> blocks of
    /// <paramref name="blockSize"/> bytes, starting at <paramref name="tableOffset"/>.
    /// </summary>
    /// <returns>The header, or a failure describing why the request is not writable.</returns>
    public static Result<VhdDynamicHeader> Create(ulong tableOffset, uint maxTableEntries, int blockSize)
    {
        Result checkedBlockSize = ValidateBlockSize(blockSize);

        if (!checkedBlockSize.Ok)
        {
            return checkedBlockSize.CastFailure<VhdDynamicHeader>();
        }

        if (maxTableEntries == 0)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Internal(
                "A dynamic VHD needs at least one block.",
                "max table entries 0"));
        }

        if (tableOffset == 0 || tableOffset % VhdFooter.SectorSize != 0)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Internal(
                "The block allocation table must start on a sector boundary after the header.",
                $"table offset {tableOffset}"));
        }

        VhdDynamicHeader header = new(
            tableOffset,
            maxTableEntries,
            blockSize,
            HeaderVersion1,
            ReservedDataOffset,
            checksum: 0);

        byte[] bytes = new byte[Length];
        header.WriteFields(bytes);

        return Result<VhdDynamicHeader>.Success(
            new VhdDynamicHeader(
                tableOffset,
                maxTableEntries,
                blockSize,
                HeaderVersion1,
                ReservedDataOffset,
                ComputeChecksum(bytes)));
    }

    /// <summary>
    /// Whether <paramref name="blockSize"/> is one this writer will use: a power of
    /// two, a whole number of sectors, and in range.
    /// </summary>
    public static Result ValidateBlockSize(int blockSize)
    {
        if (blockSize < MinimumBlockSize || blockSize > MaximumBlockSize)
        {
            return Result.Failure(DmgError.Internal(
                "The VHD block size is outside the range this writer accepts.",
                $"block size {blockSize}, allowed {MinimumBlockSize}..{MaximumBlockSize}"));
        }

        if ((blockSize & (blockSize - 1)) != 0)
        {
            return Result.Failure(DmgError.Internal(
                "A VHD block size must be a power of two.",
                $"block size {blockSize}"));
        }

        return Result.Success();
    }

    /// <summary>
    /// Serialises the header into <paramref name="destination"/>, which must be
    /// exactly <see cref="Length"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A dynamic disk header is exactly {Length} bytes; the destination is {destination.Length}.",
                nameof(destination));
        }

        WriteFields(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[ChecksumOffset..], Checksum);
    }

    /// <summary>Serialises the header into a fresh 1024-byte array.</summary>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[Length];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>
    /// The one's-complement checksum of a serialised header: every byte summed with
    /// the four checksum bytes treated as zero, then bitwise inverted.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="header"/> is not <see cref="Length"/> bytes.</exception>
    public static uint ComputeChecksum(ReadOnlySpan<byte> header)
    {
        if (header.Length != Length)
        {
            throw new ArgumentException(
                $"A dynamic disk header is exactly {Length} bytes; got {header.Length}.",
                nameof(header));
        }

        uint sum = 0;

        for (int index = 0; index < Length; index++)
        {
            if (index >= ChecksumOffset && index < ChecksumOffset + sizeof(uint))
            {
                continue;
            }

            sum += header[index];
        }

        return ~sum;
    }

    /// <summary>
    /// Reads a dynamic disk header back off disk, rejecting anything malformed.
    /// </summary>
    /// <remarks>
    /// Used to verify what this tool wrote. It is deliberately strict about the
    /// parent fields: a header carrying a parent locator is a differencing disk,
    /// whose blocks mean something completely different, and mistaking one for a
    /// plain dynamic disk would serve a caller somebody else's data.
    /// </remarks>
    public static Result<VhdDynamicHeader> Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length != Length)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                "That is not a dynamic disk header: the wrong number of bytes.",
                $"expected {Length}, got {source.Length}"));
        }

        for (int index = 0; index < Cookie.Length; index++)
        {
            if (source[CookieOffset + index] != (byte)Cookie[index])
            {
                return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                    "That is not a dynamic disk header: the 'cxsparse' cookie is missing.",
                    $"at offset {CookieOffset}"));
            }
        }

        uint storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(source[ChecksumOffset..]);
        uint expectedChecksum = ComputeChecksum(source);

        if (storedChecksum != expectedChecksum)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                "The dynamic disk header's checksum does not match its contents.",
                $"stored 0x{storedChecksum:X8}, computed 0x{expectedChecksum:X8}"));
        }

        uint headerVersion = BinaryPrimitives.ReadUInt32BigEndian(source[HeaderVersionOffset..]);

        if (headerVersion >> 16 != 1)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Unsupported(
                "This dynamic VHD uses a header version this build does not understand.",
                $"version 0x{headerVersion:X8}"));
        }

        // A differencing disk is a dynamic disk with a parent. Every parent field
        // is zero in a plain one, and the identifier is the cheapest to check.
        if (source.Slice(ParentUniqueIdOffset, 16).ContainsAnyExcept((byte)0))
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Unsupported(
                "This is a differencing VHD, whose blocks are deltas against a parent image.",
                "the parent unique id is not zero"));
        }

        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(source[BlockSizeOffset..]);

        if (blockSize > int.MaxValue)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                "The dynamic disk header names a block size larger than a file can hold.",
                $"block size {blockSize}"));
        }

        Result blockSizeCheck = ValidateBlockSize((int)blockSize);

        if (!blockSizeCheck.Ok)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                "The dynamic disk header names a block size no VHD writer produces.",
                $"block size {blockSize}"));
        }

        ulong tableOffset = BinaryPrimitives.ReadUInt64BigEndian(source[TableOffsetOffset..]);

        if (tableOffset == 0 || tableOffset % VhdFooter.SectorSize != 0)
        {
            return Result<VhdDynamicHeader>.Failure(DmgError.Corrupt(
                "The dynamic disk header's table offset is not a sector boundary.",
                $"table offset {tableOffset}"));
        }

        return Result<VhdDynamicHeader>.Success(new VhdDynamicHeader(
            tableOffset,
            BinaryPrimitives.ReadUInt32BigEndian(source[MaxTableEntriesOffset..]),
            (int)blockSize,
            headerVersion,
            BinaryPrimitives.ReadUInt64BigEndian(source[DataOffsetOffset..]),
            storedChecksum));
    }

    /// <summary>A one-line summary for verbose output and diagnostics.</summary>
    public override string ToString() =>
        $"cxsparse {MaxTableEntries} x {BlockSize} bytes, table at {TableOffset}, checksum 0x{Checksum:X8}";

    /// <summary>
    /// Writes every field except the checksum, leaving those four bytes zero, which
    /// is exactly the state the checksum is computed over.
    /// </summary>
    private void WriteFields(Span<byte> destination)
    {
        // Everything not written below - the parent identifier, the parent
        // timestamp, the parent name and all eight parent locators - stays zero,
        // which is what a non-differencing disk requires.
        destination.Clear();

        for (int index = 0; index < Cookie.Length; index++)
        {
            destination[CookieOffset + index] = (byte)Cookie[index];
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[DataOffsetOffset..], DataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(destination[TableOffsetOffset..], TableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(destination[HeaderVersionOffset..], HeaderVersion);
        BinaryPrimitives.WriteUInt32BigEndian(destination[MaxTableEntriesOffset..], MaxTableEntries);
        BinaryPrimitives.WriteUInt32BigEndian(destination[BlockSizeOffset..], (uint)BlockSize);
    }
}
