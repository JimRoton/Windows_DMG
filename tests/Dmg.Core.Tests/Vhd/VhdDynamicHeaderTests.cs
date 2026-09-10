using System.Buffers.Binary;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers the 1024-byte <c>cxsparse</c> header and the layout arithmetic that
/// goes with it.
/// </summary>
public sealed class VhdDynamicHeaderTests
{
    private static VhdDynamicHeader Create(
        ulong tableOffset = 1536,
        uint entries = 8,
        int blockSize = VhdDynamicHeader.DefaultBlockSize)
    {
        Result<VhdDynamicHeader> created = VhdDynamicHeader.Create(tableOffset, entries, blockSize);

        Assert.True(created.TryGetValue(out VhdDynamicHeader? header), created.Ok ? "" : created.Error.ToString());

        return header;
    }

    [Fact]
    public void WritesTheCookieTheVersionAndTheTableAtTheSpecifiedOffsets()
    {
        byte[] bytes = Create(tableOffset: 1536, entries: 24, blockSize: 2 * 1024 * 1024).ToArray();

        Assert.Equal(VhdDynamicHeader.Length, bytes.Length);
        Assert.Equal("cxsparse", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));

        // Data Offset here is the reserved field, all ones - unlike the footer's
        // field of the same name, which is where this header's own offset goes.
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFuL, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8)));
        Assert.Equal(1536uL, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(16)));
        Assert.Equal(0x0001_0000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24)));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28)));
        Assert.Equal(0x0020_0000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32)));
    }

    [Fact]
    public void LeavesEveryParentFieldZeroSoItIsNotMistakenForADifferencingDisk()
    {
        byte[] bytes = Create().ToArray();

        // Parent unique id, parent timestamp, reserved, parent unicode name and all
        // eight parent locators: everything from offset 40 to the end.
        Assert.False(bytes.AsSpan(40).ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void RoundTripsThroughParse()
    {
        VhdDynamicHeader header = Create(tableOffset: 1536, entries: 4096, blockSize: 512 * 1024);

        Result<VhdDynamicHeader> parsed = VhdDynamicHeader.Parse(header.ToArray());

        Assert.True(parsed.TryGetValue(out VhdDynamicHeader? read), parsed.Ok ? "" : parsed.Error.ToString());
        Assert.Equal(header.TableOffset, read.TableOffset);
        Assert.Equal(header.MaxTableEntries, read.MaxTableEntries);
        Assert.Equal(header.BlockSize, read.BlockSize);
        Assert.Equal(header.Checksum, read.Checksum);
        Assert.True(read.ToArray().AsSpan().SequenceEqual(header.ToArray()));
    }

    [Fact]
    public void TheChecksumIsTheOnesComplementSumOfEveryOtherByte()
    {
        byte[] bytes = Create().ToArray();

        uint stored = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(36));

        Assert.Equal(stored, VhdDynamicHeader.ComputeChecksum(bytes));

        // Flipping any byte must invalidate it.
        bytes[100] ^= 0xFF;

        Assert.NotEqual(stored, VhdDynamicHeader.ComputeChecksum(bytes));
        Assert.False(VhdDynamicHeader.Parse(bytes).Ok);
    }

    [Fact]
    public void RefusesAHeaderWithoutTheCookie()
    {
        byte[] bytes = Create().ToArray();
        bytes[0] = (byte)'x';

        Result<VhdDynamicHeader> parsed = VhdDynamicHeader.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, parsed.Error.Code);
    }

    [Fact]
    public void RefusesADifferencingDiskRatherThanReadingItAsAPlainOne()
    {
        // A differencing disk's blocks are deltas against another image. Reading one
        // as a plain dynamic disk would hand a caller somebody else's data.
        byte[] bytes = Create().ToArray();
        bytes[40] = 0x01;

        // Re-checksum, so the refusal is about the parent field and not the sum.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36), VhdDynamicHeader.ComputeChecksum(bytes));

        Result<VhdDynamicHeader> parsed = VhdDynamicHeader.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, parsed.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(1536)]
    [InlineData(3 * 1024 * 1024)]
    [InlineData(128 * 1024 * 1024)]
    public void RefusesABlockSizeThatIsNotAPowerOfTwoInRange(int blockSize)
    {
        Assert.False(VhdDynamicHeader.ValidateBlockSize(blockSize).Ok);
        Assert.False(VhdDynamicHeader.Create(1536, 4, blockSize).Ok);
    }

    [Fact]
    public void RefusesATableOffsetThatIsNotASectorBoundary()
    {
        Assert.False(VhdDynamicHeader.Create(1537, 4, VhdDynamicHeader.DefaultBlockSize).Ok);
        Assert.False(VhdDynamicHeader.Create(0, 4, VhdDynamicHeader.DefaultBlockSize).Ok);
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(4096, 512)]
    [InlineData(512 * 1024, 512)]
    [InlineData(2 * 1024 * 1024, 512)]
    [InlineData(8 * 1024 * 1024, 2048)]
    public void TheSectorBitmapIsOneBitPerSectorRoundedUpToAWholeSector(int blockSize, int expected)
    {
        Assert.Equal(expected, VhdDynamicHeader.BitmapBytesFor(blockSize));

        // Whatever the block size, the bitmap is a whole number of sectors and has
        // room for a bit per sector of the block.
        Assert.Equal(0, expected % VhdFooter.SectorSize);
        Assert.True(expected * 8 >= blockSize / VhdFooter.SectorSize);
    }

    [Fact]
    public void TheLayoutPutsTheTableAfterBothHeadersAndTheBlocksAfterTheTable()
    {
        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(16 * 1024 * 1024, 2 * 1024 * 1024);

        Assert.True(planned.TryGetValue(out VhdDynamicLayout layout));

        Assert.Equal(0, VhdDynamicLayout.FooterCopyOffset);
        Assert.Equal(512, VhdDynamicLayout.HeaderOffset);
        Assert.Equal(1536, VhdDynamicLayout.TableOffset);

        Assert.Equal(8u, layout.BlockCount);
        Assert.Equal(512, layout.TableBytes);
        Assert.Equal(2048, layout.FirstBlockOffset);
        Assert.Equal(512 + (2 * 1024 * 1024), layout.BlockStride);

        // Nothing allocated is the metadata and the two footers, and nothing else.
        Assert.Equal(2048 + 512, layout.FileSizeFor(0));
        Assert.Equal(layout.MetadataBytes, layout.FileSizeFor(0));

        // Everything allocated is larger than the fixed VHD would have been, by the
        // header, the table and the per-block bitmaps.
        Assert.True(layout.MaximumFileSize > VhdWriter.FixedFileSizeFor(16 * 1024 * 1024));
    }

    [Fact]
    public void TheLastBlockIsShortWhenTheDiskIsNotAWholeNumberOfBlocks()
    {
        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(diskSize: 5 * 512, blockSize: 1024);

        Assert.True(planned.TryGetValue(out VhdDynamicLayout layout));

        Assert.Equal(3u, layout.BlockCount);
        Assert.Equal(1024, layout.DiskBytesInBlock(0));
        Assert.Equal(1024, layout.DiskBytesInBlock(1));
        Assert.Equal(512, layout.DiskBytesInBlock(2));

        // A short final block still costs a whole block on disk: the table has no
        // way to express a partial one.
        Assert.Equal(layout.FirstBlockOffset + (3 * layout.BlockStride) + 512, layout.MaximumFileSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-512)]
    [InlineData(513)]
    public void TheLayoutRefusesADiskSizeThatIsNotAPositiveWholeNumberOfSectors(long diskSize)
    {
        Assert.False(VhdDynamicLayout.For(diskSize, VhdDynamicHeader.DefaultBlockSize).Ok);
    }

    [Fact]
    public void TheLayoutRefusesMoreBlocksThanItWillHoldATableFor()
    {
        // 512-byte blocks over a 40 GB image is eighty million entries. The answer
        // is to ask for bigger blocks, and the message says so.
        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(40L * 1024 * 1024 * 1024, 512);

        Assert.False(planned.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, planned.Error.Code);
    }
}
