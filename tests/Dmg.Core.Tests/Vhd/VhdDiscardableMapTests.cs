using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// The discardable map: ranges the caller has decided need not be written, even
/// though they are not known to be zeros.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these assert on reads, not just on output.</b> While this was being
/// wired up, the parameter was threaded through four public signatures and both
/// consuming loops but dropped in a private method in between - so the writer
/// accepted a map and never consulted it. That compiled, and only failed because
/// the block loop happened to live in a method that had lost the name. Had it
/// lived one level up it would have shipped looking wired: <c>--dynamic</c> would
/// have taken the map and skipped nothing.
/// </para>
/// <para>
/// So the load-bearing test here does not check that the file came out smaller. It
/// puts a stream underneath that throws if the supposedly-discarded bytes are read
/// at all. A map that is accepted and ignored fails it.
/// </para>
/// <para>
/// <b>This map is not <see cref="IVhdSparseMap"/>.</b> That one means "certainly
/// zeros" and preserves the source byte for byte. This one means "the filesystem
/// is not using it", which on a captured disk is not the same thing - so the disk
/// it produces is deliberately not a faithful copy. See
/// <see cref="IVhdDiscardableMap"/>.
/// </para>
/// </remarks>
public sealed class VhdDiscardableMapTests : IDisposable
{
    private const int Block = 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-discardable-tests",
        Guid.NewGuid().ToString("N"));

    public VhdDiscardableMapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheWriterNeverReadsARangeTheMapDiscarded()
    {
        // The one that catches a parameter which is accepted and then dropped. The
        // stream refuses to serve the first two blocks; the map says they may be
        // skipped. A write that succeeds is a write that never went looking.
        //
        // Note the blocks are NOT zeros - Image fills block 1 - so a sparse map
        // could not have skipped them. Only the discardable map can.
        CountingStream source = new(Image(blocks: 4, filled: [1, 3]).ToArray(), forbiddenBelow: 2 * Block);

        string path = PathFor("discarded.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            path,
            Options(),
            progress: null,
            freeSpaceProbe: null,
            sparseMap: null,
            cancellationToken: default,
            discardableMap: new StubDiscardableMap(0, 2 * Block));

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        // Blocks 0 and 1 discarded; block 2 is zeros and skipped anyway; block 3
        // holds data and is written.
        Assert.Equal(4, image.Table.Length);
        Assert.Equal(1, image.AllocatedBlocks);
        Assert.Equal(3, image.UnallocatedBlocks);
    }

    [Fact]
    public void ADiscardedBlockReadsBackAsZerosRatherThanItsOriginalBytes()
    {
        // The honest cost of this map, asserted rather than described: the block
        // held data in the source and comes back zeros. That is why extract and
        // verify must never be handed one.
        MemoryStream source = Image(blocks: 3, filled: [0, 1, 2]);

        string path = PathFor("not-faithful.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            path,
            Options(),
            progress: null,
            freeSpaceProbe: null,
            sparseMap: null,
            cancellationToken: default,
            discardableMap: new StubDiscardableMap(Block, Block));

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        byte[] decoded = image.ReadAllBytes();
        byte[] original = Image(blocks: 3, filled: [0, 1, 2]).ToArray();

        // Block 1 was discarded: zeros here, data in the source.
        Assert.True(
            decoded.AsSpan(Block, Block).IndexOfAnyExcept((byte)0) < 0,
            "The discarded block was written after all.");

        Assert.False(
            original.AsSpan(Block, Block).IndexOfAnyExcept((byte)0) < 0,
            "The source block was zeros, so this test proves nothing.");

        // The blocks either side are untouched.
        Assert.True(decoded.AsSpan(0, Block).SequenceEqual(original.AsSpan(0, Block)));
        Assert.True(decoded.AsSpan(2 * Block, Block).SequenceEqual(original.AsSpan(2 * Block, Block)));
    }

    [Fact]
    public void ThePrecheckCountsDiscardedBlocksOut()
    {
        // The half that decides whether a mount is refused before it starts. With
        // no map at all the answer is the worst case - every block allocated - which
        // is what turns a mostly-empty terabyte volume into "not enough space".
        Result<long> worstCase = VhdWriter.DynamicFileSizeFor(8 * Block, Block);

        Result<long> withMap = VhdWriter.DynamicFileSizeFor(
            8 * Block,
            Block,
            sparseMap: null,
            discardableMap: new StubDiscardableMap(0, 6 * Block));

        Assert.True(worstCase.TryGetValue(out long worst), worstCase.Ok ? "" : worstCase.Error.ToString());
        Assert.True(withMap.TryGetValue(out long mapped), withMap.Ok ? "" : withMap.Error.ToString());

        Assert.True(
            mapped < worst,
            $"The precheck asked for {mapped} bytes with six of eight blocks discardable, "
            + $"against {worst} for the worst case - it did not count them out.");
    }

    [Fact]
    public void NoMapAtAllStillMeansTheWorstCase()
    {
        // The behaviour every other caller depends on, and the one this change must
        // not have altered.
        Result<long> sized = VhdWriter.DynamicFileSizeFor(8 * Block, Block);

        Assert.True(sized.TryGetValue(out long required), sized.Ok ? "" : sized.Error.ToString());

        Result<VhdDynamicLayout> planned = VhdDynamicLayout.For(8 * Block, Block);

        Assert.True(planned.TryGetValue(out VhdDynamicLayout layout));
        Assert.Equal(layout.MaximumFileSize, required);
    }

    [Fact]
    public void TheTwoMapsCompose()
    {
        // A sparse map for what the image stores nothing for, a discardable map for
        // what the filesystem is not using. Either one may excuse a block.
        MemoryStream source = Image(blocks: 4, filled: [0, 1, 2, 3]);

        string path = PathFor("both.vhd");

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            path,
            Options(),
            progress: null,
            freeSpaceProbe: null,
            sparseMap: new StubSparseMap(0, Block),
            cancellationToken: default,
            discardableMap: new StubDiscardableMap(2 * Block, Block));

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        using DynamicVhdImage image = DynamicVhdImage.Open(path);

        // Block 0 excused by the sparse map, block 2 by the discardable map.
        Assert.Equal(2, image.AllocatedBlocks);
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static VhdWriteOptions Options(int blockSize = Block) => new()
    {
        DiskType = VhdDiskType.Dynamic,
        BlockSize = blockSize,
        BufferSize = 512,
        UniqueId = new Guid("aaaabbbb-cccc-dddd-eeee-ffff00001111"),
        CreatedUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
    };

    private static MemoryStream Image(int blocks, params int[] filled)
    {
        byte[] bytes = new byte[blocks * Block];

        foreach (int index in filled)
        {
            for (int offset = 0; offset < Block; offset++)
            {
                bytes[(index * Block) + offset] = (byte)(1 + (((index * 7) + offset) % 250));
            }
        }

        return new MemoryStream(bytes, writable: false);
    }

    private sealed class StubDiscardableMap(long start, long length) : IVhdDiscardableMap
    {
        public bool MayDiscard(long offset, long count) =>
            offset >= start && offset + count <= start + length;
    }

    private sealed class StubSparseMap(long start, long length) : IVhdSparseMap
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
                    $"The writer read byte {Position}, which the map said could be discarded.");
            }

            return base.Read(buffer);
        }
    }
}
