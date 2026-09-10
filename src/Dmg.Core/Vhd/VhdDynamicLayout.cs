namespace Dmg.Core.Vhd;

/// <summary>
/// Where everything sits in a dynamic VHD, worked out from the disk size and the
/// block size alone.
/// </summary>
/// <remarks>
/// <para>
/// The file is: a copy of the footer, the <c>cxsparse</c> header, the block
/// allocation table, the allocated blocks in the order they were written, and the
/// real footer. Every offset here is absolute from the start of the file, because
/// that is how the block allocation table's entries are defined - a table entry is
/// a sector number, not a delta.
/// </para>
/// <para>
/// <b>The footer is written twice on purpose.</b> The copy at offset zero is what
/// the specification asks for, and it is what lets a reader recover a VHD whose
/// tail was lost - the case that motivated it. Both copies are byte-identical.
/// </para>
/// <para>
/// This is a value, computed and thrown away. It is public because the round-trip
/// tests have to find the table without re-deriving the arithmetic they are
/// checking, and because a later reader will want the same numbers.
/// </para>
/// </remarks>
public readonly record struct VhdDynamicLayout
{
    private VhdDynamicLayout(long diskSize, int blockSize, uint blockCount, long tableBytes)
    {
        DiskSize = diskSize;
        BlockSize = blockSize;
        BlockCount = blockCount;
        TableBytes = tableBytes;
    }

    /// <summary>
    /// The most table entries this writer will produce: sixteen million, a 64 MiB
    /// table. The table is built in memory before it is written, so this is what
    /// bounds that.
    /// </summary>
    public const long MaxTableEntries = 16L * 1024 * 1024;

    /// <summary>The disk's capacity in bytes: a whole number of sectors.</summary>
    public long DiskSize { get; }

    /// <summary>How many bytes of disk one block holds.</summary>
    public int BlockSize { get; }

    /// <summary>How many blocks the disk is divided into, and so how many table entries there are.</summary>
    public uint BlockCount { get; }

    /// <summary>The table's length on disk, rounded up to a whole sector.</summary>
    public long TableBytes { get; }

    /// <summary>The offset of the footer copy at the head of the file: zero.</summary>
    public static long FooterCopyOffset => 0;

    /// <summary>The offset of the <c>cxsparse</c> header, immediately after the footer copy.</summary>
    public static long HeaderOffset => VhdFooter.Length;

    /// <summary>The offset of the block allocation table, immediately after the header.</summary>
    public static long TableOffset => VhdFooter.Length + VhdDynamicHeader.Length;

    /// <summary>The offset the first allocated block would be written at.</summary>
    public long FirstBlockOffset => TableOffset + TableBytes;

    /// <summary>The bytes of sector bitmap in front of each block's data.</summary>
    public int SectorBitmapBytes => VhdDynamicHeader.BitmapBytesFor(BlockSize);

    /// <summary>The bytes one allocated block occupies: bitmap then a full block of data.</summary>
    public long BlockStride => SectorBitmapBytes + (long)BlockSize;

    /// <summary>Everything before the first block: two headers, the table, and nothing else.</summary>
    public long MetadataBytes => FirstBlockOffset + VhdFooter.Length;

    /// <summary>
    /// The file's size when <paramref name="allocatedBlocks"/> of its blocks were
    /// written and the rest left unallocated.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="allocatedBlocks"/> is negative or larger than <see cref="BlockCount"/>.
    /// </exception>
    public long FileSizeFor(long allocatedBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBlocks);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(allocatedBlocks, BlockCount);

        return checked(FirstBlockOffset + (allocatedBlocks * BlockStride) + VhdFooter.Length);
    }

    /// <summary>
    /// The largest the file can get: every block allocated. Always bigger than the
    /// equivalent fixed VHD, by the header and the table.
    /// </summary>
    public long MaximumFileSize => FileSizeFor(BlockCount);

    /// <summary>The absolute offset of the block whose table entry is <paramref name="tableEntry"/>.</summary>
    public static long BlockOffsetFor(uint tableEntry) => (long)tableEntry * VhdFooter.SectorSize;

    /// <summary>How many bytes of disk the block at <paramref name="index"/> covers.</summary>
    /// <remarks>
    /// The last block is short whenever the disk is not a whole number of blocks.
    /// It still occupies a full <see cref="BlockStride"/> on disk - the tail is
    /// zero-filled - because the table's arithmetic has no way to express a short
    /// block.
    /// </remarks>
    public long DiskBytesInBlock(uint index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);

        long start = (long)index * BlockSize;

        return Math.Min(BlockSize, DiskSize - start);
    }

    /// <summary>
    /// Works out the layout for a disk of <paramref name="diskSize"/> bytes in
    /// blocks of <paramref name="blockSize"/> bytes.
    /// </summary>
    /// <returns>
    /// The layout, or a failure when the disk size or the block size is one no
    /// dynamic VHD can describe.
    /// </returns>
    public static Result<VhdDynamicLayout> For(long diskSize, int blockSize)
    {
        Result checkedBlockSize = VhdDynamicHeader.ValidateBlockSize(blockSize);

        if (!checkedBlockSize.Ok)
        {
            return checkedBlockSize.CastFailure<VhdDynamicLayout>();
        }

        if (diskSize <= 0 || diskSize % VhdFooter.SectorSize != 0)
        {
            return Result<VhdDynamicLayout>.Failure(DmgError.Internal(
                "A dynamic VHD's disk size must be a positive whole number of 512-byte sectors.",
                $"disk size {diskSize}"));
        }

        long blocks = (diskSize + blockSize - 1) / blockSize;

        if (blocks > MaxTableEntries)
        {
            // The table is held in memory while the blocks are written, so its size
            // is a real limit and not a theoretical one. At the 2 MiB default this
            // ceiling is thirty-two terabytes of disk - far past what a VHD can
            // describe at all - so it can only be reached by asking for tiny blocks
            // on a large image, and the answer to that is to ask for bigger blocks.
            return Result<VhdDynamicLayout>.Failure(DmgError.Unsupported(
                "The image needs more blocks than this writer will hold a table for.",
                $"{blocks} blocks of {blockSize} bytes, at most {MaxTableEntries}"));
        }

        long tableBytes = VhdWriter.RoundUpToSector(checked(blocks * VhdDynamicHeader.TableEntryLength));

        return Result<VhdDynamicLayout>.Success(
            new VhdDynamicLayout(diskSize, blockSize, (uint)blocks, tableBytes));
    }

    /// <summary>A one-line summary for verbose output.</summary>
    public override string ToString() =>
        $"{BlockCount} x {BlockSize}-byte blocks over {DiskSize} bytes, table at {TableOffset}";
}
