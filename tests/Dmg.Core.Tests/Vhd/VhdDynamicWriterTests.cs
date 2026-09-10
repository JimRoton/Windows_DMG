using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers the dynamic (sparse) writer: what it leaves out, what it writes, and
/// what it refuses.
/// </summary>
/// <remarks>
/// Small blocks throughout - 1 KiB rather than the 2 MiB default - so a few
/// kilobytes of synthetic image reaches the cases that matter: a partly allocated
/// table, a short final block, a block of zeros in the middle of real data.
/// </remarks>
public sealed class VhdDynamicWriterTests : IDisposable
{
    private const int Block = 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-dynamic-tests",
        Guid.NewGuid().ToString("N"));

    public VhdDynamicWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static VhdWriteOptions Options(int blockSize = Block) => new()
    {
        DiskType = VhdDiskType.Dynamic,
        BlockSize = blockSize,
        BufferSize = 512,
        UniqueId = new Guid("11112222-3333-4444-5555-666677778888"),
        CreatedUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
    };

    /// <summary>
    /// An image of <paramref name="blocks"/> blocks, with the ones named in
    /// <paramref name="filled"/> carrying data and the rest left as zeros.
    /// </summary>
    private static MemoryStream Image(int blocks, params int[] filled)
    {
        byte[] bytes = new byte[blocks * Block];

        foreach (int index in filled)
        {
            for (int offset = 0; offset < Block; offset++)
            {
                bytes[(index * Block) + offset] = (byte)(1 + ((index * 7 + offset) % 250));
            }
        }

        return new MemoryStream(bytes, writable: false);
    }

    private string WriteImage(string name, Stream source, VhdWriteOptions? options = null, IVhdSparseMap? map = null)
    {
        string path = PathFor(name);

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            path,
            options ?? Options(),
            progress: null,
            freeSpaceProbe: null,
            map);

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        return path;
    }

    [Fact]
    public void WritesOnlyTheBlocksThatAreNotAllZeros()
    {
        string path = WriteImage("sparse.vhd", Image(blocks: 8, filled: [1, 6]));

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        Assert.Equal(8, image.Table.Length);
        Assert.Equal(2, image.AllocatedBlocks);
        Assert.Equal(6, image.UnallocatedBlocks);

        for (int index = 0; index < 8; index++)
        {
            bool allocated = image.Table[index] != VhdDynamicHeader.UnusedBlockEntry;

            Assert.Equal(index is 1 or 6, allocated);
        }
    }

    [Fact]
    public void AMostlyEmptyImageCostsAFractionOfTheFixedVhd()
    {
        // Sixty-four blocks of disk, one of them with anything in it.
        MemoryStream source = Image(blocks: 64, filled: [40]);
        long payload = source.Length;

        string path = WriteImage("mostly-empty.vhd", source);

        long dynamicSize = new FileInfo(path).Length;
        long fixedSize = VhdWriter.FixedFileSizeFor(payload);

        Assert.True(
            dynamicSize < fixedSize / 8,
            $"A one-in-sixty-four image cost {dynamicSize} bytes against the fixed VHD's {fixedSize}.");
    }

    [Fact]
    public void TheDiskReadsBackByteForByteThroughTheAllocationTable()
    {
        byte[] expected = Image(blocks: 9, filled: [0, 3, 4, 8]).ToArray();

        string path = WriteImage("readback.vhd", new MemoryStream(expected, writable: false));

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        Assert.Equal(expected.Length, image.DiskSize);
        Assert.True(image.ReadAllBytes().AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void AnUnallocatedBlockReadsBackAsZeros()
    {
        string path = WriteImage("holes.vhd", Image(blocks: 4, filled: [2]));

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        byte[] disk = image.ReadAllBytes();

        Assert.False(disk.AsSpan(0, 2 * Block).ContainsAnyExcept((byte)0));
        Assert.True(disk.AsSpan(2 * Block, Block).ContainsAnyExcept((byte)0));
        Assert.False(disk.AsSpan(3 * Block, Block).ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void PadsAShortFinalBlockWithZerosAndStillDeclaresTheWholeDisk()
    {
        // Two and a bit blocks: the last one is 512 bytes of disk in a 1024-byte
        // block, and the source does not end on a sector boundary either.
        byte[] bytes = new byte[(2 * Block) + 300];
        Array.Fill(bytes, (byte)0xA5);

        string path = WriteImage("short-tail.vhd", new MemoryStream(bytes, writable: false));

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        Assert.Equal(3, image.Table.Length);
        Assert.Equal(3, image.AllocatedBlocks);

        // The disk is rounded up to a whole sector, not to a whole block.
        Assert.Equal((2 * Block) + 512, image.DiskSize);

        byte[] disk = image.ReadAllBytes();

        Assert.True(disk.AsSpan(0, bytes.Length).SequenceEqual(bytes));
        Assert.False(disk.AsSpan(bytes.Length).ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void TheFileSizeIsExactlyTheMetadataPlusTheAllocatedBlocks()
    {
        MemoryStream source = Image(blocks: 10, filled: [2, 5, 9]);

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            PathFor("sized.vhd"),
            Options());

        Assert.True(written.TryGetValue(out VhdWriteResult? result));

        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(10 * Block, Block);

        Assert.True(planned.TryGetValue(out VhdDynamicLayout layout));

        Assert.Equal(layout.FileSizeFor(3), result.TotalBytes);
        Assert.Equal(layout.FileSizeFor(3), new FileInfo(PathFor("sized.vhd")).Length);
        Assert.Equal(VhdDiskType.Dynamic, result.Footer.DiskType);
        Assert.Equal(10 * Block, result.DiskSize);
    }

    [Fact]
    public void ASparseMapSparesTheWriterFromEverReadingTheEmptyParts()
    {
        // The map claims blocks 0 and 1 are zeros; the stream refuses to serve those
        // bytes at all. A write that succeeds is a write that never went looking.
        CountingStream source = new(Image(blocks: 4, filled: [3]).ToArray(), forbiddenBelow: 2 * Block);

        string path = WriteImage("mapped.vhd", source, Options(), new StubMap(0, 2 * Block));

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        Assert.Equal(1, image.AllocatedBlocks);
        Assert.Equal(VhdDynamicHeader.UnusedBlockEntry, image.Table[0]);
        Assert.Equal(VhdDynamicHeader.UnusedBlockEntry, image.Table[1]);
    }

    [Fact]
    public void TheSameFileComesOutWithOrWithoutAMap()
    {
        // The map is an optimisation. If it changed the output it would be a bug.
        byte[] bytes = Image(blocks: 6, filled: [4]).ToArray();

        string withMap = WriteImage(
            "with-map.vhd",
            new MemoryStream(bytes, writable: false),
            Options(),
            new StubMap(0, 4 * Block));

        string withoutMap = WriteImage("without-map.vhd", new MemoryStream(bytes, writable: false));

        Assert.True(File.ReadAllBytes(withMap).AsSpan().SequenceEqual(File.ReadAllBytes(withoutMap)));
    }

    [Fact]
    public void TheFooterAppearsAtBothEndsAndParsesToTheSameDynamicDisk()
    {
        string path = WriteImage("footers.vhd", Image(blocks: 3, filled: [1]));

        byte[] file = File.ReadAllBytes(path);

        Result<VhdFooter> head = VhdFooter.Parse(file.AsSpan(0, VhdFooter.Length));
        Result<VhdFooter> tail = VhdFooter.Parse(file.AsSpan(file.Length - VhdFooter.Length));

        Assert.True(head.TryGetValue(out VhdFooter? first), head.Ok ? "" : head.Error.ToString());
        Assert.True(tail.TryGetValue(out VhdFooter? last), tail.Ok ? "" : tail.Error.ToString());

        Assert.True(file.AsSpan(0, VhdFooter.Length).SequenceEqual(file.AsSpan(file.Length - VhdFooter.Length)));
        Assert.Equal(VhdDiskType.Dynamic, first.DiskType);
        Assert.Equal(first.DiskSize, last.DiskSize);

        // The footer's data offset is what points a reader at the cxsparse header.
        // All ones there - the fixed-disk sentinel - is a VHD Windows will not attach.
        Assert.Equal((ulong)VhdFooter.Length, first.DataOffset);
    }

    [Fact]
    public void FixedIsStillTheDefaultAndTheDispatcherHonoursTheFlag()
    {
        byte[] bytes = Image(blocks: 4, filled: [1]).ToArray();

        string byDefault = PathFor("default.vhd");
        string byFlag = PathFor("flagged.vhd");

        Assert.True(VhdWriter.WriteToFile(new MemoryStream(bytes, writable: false), byDefault).Ok);
        Assert.True(VhdWriter.WriteToFile(new MemoryStream(bytes, writable: false), byFlag, Options()).Ok);

        Result<VhdFooter> plain = VhdFooter.Parse(Tail(byDefault));
        Result<VhdFooter> sparse = VhdFooter.Parse(Tail(byFlag));

        Assert.True(plain.TryGetValue(out VhdFooter? fixedFooter));
        Assert.True(sparse.TryGetValue(out VhdFooter? dynamicFooter));

        Assert.Equal(VhdDiskType.Fixed, fixedFooter.DiskType);
        Assert.Equal(VhdDiskType.Dynamic, dynamicFooter.DiskType);

        // And the default really is the whole payload plus a footer.
        Assert.Equal(bytes.Length + VhdFooter.Length, new FileInfo(byDefault).Length);
    }

    [Fact]
    public void ReportsProgressFromZeroToTheWholeDiskEvenAcrossTheBlocksItSkips()
    {
        List<VhdWriteProgress> reports = [];

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            Image(blocks: 8, filled: [7]),
            PathFor("progress.vhd"),
            Options(),
            new ImmediateProgress(reports.Add));

        Assert.True(written.Ok);
        Assert.NotEmpty(reports);
        Assert.Equal(0, reports[0].BytesWritten);
        Assert.Equal(reports[^1].TotalBytes, reports[^1].BytesWritten);

        // Monotonic: a bar that goes backwards is worse than no bar.
        for (int index = 1; index < reports.Count; index++)
        {
            Assert.True(reports[index].BytesWritten >= reports[index - 1].BytesWritten);
        }
    }

    [Fact]
    public void RefusesADestinationItCannotSeekBackTo()
    {
        // The allocation table is filled in after the blocks it points at, so a
        // forward-only destination cannot carry a dynamic VHD.
        Result<VhdWriteResult> written = VhdWriter.WriteDynamic(
            Image(blocks: 2, filled: [0]),
            new ForwardOnlyStream(),
            Options());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void RefusesToStartPartWayIntoAFile()
    {
        // Every table entry is an absolute sector number, so a dynamic VHD that does
        // not start at byte zero would describe blocks that are not where it says.
        MemoryStream destination = new();
        destination.Write(new byte[512]);

        Result<VhdWriteResult> written = VhdWriter.WriteDynamic(
            Image(blocks: 2, filled: [0]),
            destination,
            Options());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void RefusesABlockSizeThatIsNotAPowerOfTwo()
    {
        Result<VhdWriteResult> written = VhdWriter.WriteDynamic(
            Image(blocks: 2, filled: [0]),
            new MemoryStream(),
            Options() with { BlockSize = 1536 });

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InternalError, written.Error.Code);
    }

    [Fact]
    public void LeavesNoFileBehindWhenTheImageEndsEarly()
    {
        string path = PathFor("truncated.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            new TruncatedStream(4 * Block, servedBytes: Block + 16),
            path,
            Options());

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, written.Error.Code);
        Assert.False(File.Exists(path), "A partly written VHD is worse than none.");
    }

    [Fact]
    public void RefusesBeforeWritingAnythingWhenTheVolumeHasNoRoom()
    {
        string path = PathFor("no-room.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            Image(blocks: 4, filled: [1]),
            path,
            Options(),
            progress: null,
            new StubProbe(1024));

        Assert.False(written.Ok);
        Assert.Equal(DmgExitCode.InsufficientSpace, written.Error.Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ThePrecheckAsksForTheWorstCaseWithoutAMapAndTheRealFigureWithOne()
    {
        long payload = 8 * Block;

        Result<long> blind = VhdWriter.DynamicFileSizeFor(payload, Block);
        Result<long> informed = VhdWriter.DynamicFileSizeFor(payload, Block, new StubMap(0, 6 * Block));

        Assert.True(blind.TryGetValue(out long worstCase));
        Assert.True(informed.TryGetValue(out long expected));

        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(payload, Block);

        Assert.True(planned.TryGetValue(out VhdDynamicLayout layout));

        Assert.Equal(layout.MaximumFileSize, worstCase);
        Assert.Equal(layout.FileSizeFor(2), expected);

        // Without a map, a dynamic write asks for more room than a fixed one. That
        // is the honest answer and the reason to pass a map.
        Assert.True(worstCase > VhdWriter.FixedFileSizeFor(payload));
    }

    private static byte[] Tail(string path)
    {
        byte[] file = File.ReadAllBytes(path);

        return file[^VhdFooter.Length..];
    }

    /// <summary>Says a fixed prefix of the disk is zeros.</summary>
    private sealed class StubMap(long start, long length) : IVhdSparseMap
    {
        public bool IsKnownZero(long offset, long count) =>
            offset >= start && offset + count <= start + length;
    }

    /// <summary>Throws if anything below a given offset is ever read.</summary>
    private sealed class CountingStream(byte[] bytes, long forbiddenBelow)
        : MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            if (Position < forbiddenBelow)
            {
                throw new InvalidOperationException(
                    $"The writer read byte {Position}, which the map said was zeros.");
            }

            return base.Read(buffer);
        }
    }

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    /// <summary>Claims one length and serves fewer bytes.</summary>
    private sealed class TruncatedStream(int length, int servedBytes) : MemoryStream(new byte[length], writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            long left = servedBytes - Position;

            return left <= 0
                ? 0
                : base.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        }
    }

    private sealed class StubProbe(long availableBytes) : IFreeSpaceProbe
    {
        public Result<long> AvailableBytes(string path) => Result<long>.Success(availableBytes);
    }

    private sealed class ImmediateProgress(Action<VhdWriteProgress> handler) : IProgress<VhdWriteProgress>
    {
        public void Report(VhdWriteProgress value) => handler(value);
    }
}
