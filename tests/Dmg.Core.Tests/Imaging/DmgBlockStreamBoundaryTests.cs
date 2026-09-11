using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// S5.3: the arithmetic at chunk edges, proven with chunks several sectors wide
/// rather than one - see <see cref="FindOrdinal"/>-shaped remarks below.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DmgBlockStream"/> locates the extent under a position by binary
/// search over start sectors (see the private <c>FindOrdinal</c>). Every one of
/// these chunks spans more than one sector, so a position in the middle of a
/// chunk lands on a sector that is <em>not</em> any extent's <c>StartSector</c> -
/// <c>Array.BinarySearch</c> returns the bitwise complement of the insertion
/// point, and the method backs up one (<c>~ordinal - 1</c>) to find the extent
/// that contains it rather than starts at it. A suite built entirely from
/// one-sector chunks would never take that branch, because every sector would
/// also be a chunk start; multi-sector chunks are what makes the interpolating
/// path the common case here, not the exception.
/// </para>
/// </remarks>
public sealed class DmgBlockStreamBoundaryTests
{
    /// <summary>
    /// Five chunks, none of them one sector, spanning three codecs - wide enough
    /// that most positions in the disk fall strictly inside a chunk rather than
    /// on its first sector.
    /// </summary>
    private static SyntheticUdifImage MultiSectorImage() => SyntheticUdif.Build(
        SyntheticUdif.ChunkSpec.Zlib(4),
        SyntheticUdif.ChunkSpec.Raw(6),
        SyntheticUdif.ChunkSpec.Zlib(3),
        SyntheticUdif.ChunkSpec.Raw(5),
        SyntheticUdif.ChunkSpec.Zlib(7));

    [Fact]
    public void AReadSpanningExactlyTwoChunksIsAssembledCorrectly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        // Chunk 0 is sectors [0,4), chunk 1 is [4,10). Start inside chunk 0, end
        // inside chunk 1: the read crosses exactly one boundary.
        int start = 2 * 512;
        int length = image.ChunkStart(2) - start - 512;

        stream.Position = start;
        byte[] read = new byte[length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, length).ToArray(), read);
    }

    [Fact]
    public void AReadSpanningExactlyThreeChunksIsAssembledCorrectly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        // Start inside chunk 0, end inside chunk 2: two boundaries crossed in a
        // single Read call.
        int start = image.ChunkStart(0) + 512;
        int end = image.ChunkStart(2) + 512;
        int length = end - start;

        stream.Position = start;
        byte[] read = new byte[length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, length).ToArray(), read);
    }

    [Fact]
    public void APartialReadEntirelyInsideOneChunkTouchesOnlyThatChunk()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);
        using DmgBlockStream stream = OpenOver(counting);

        counting.Reset();

        // Chunk 1 (Raw, sectors [4,10)) is six sectors wide: read one sector from
        // its middle, touching neither neighbour.
        int chunk1Start = image.ChunkStart(1);
        int start = chunk1Start + (2 * 512);

        stream.Position = start;
        byte[] read = new byte[512];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, 512).ToArray(), read);
        Assert.Equal(1, counting.Reads);
    }

    [Fact]
    public void TheFirstSectorOfTheDiskReadsCorrectly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        byte[] read = new byte[512];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(0, 512).ToArray(), read);
    }

    [Fact]
    public void TheLastSectorOfTheDiskReadsCorrectly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        stream.Position = image.Length - 512;
        byte[] read = new byte[512];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(image.Length - 512, 512).ToArray(), read);
        Assert.Equal(image.Length, stream.Position);
    }

    [Fact]
    public void AReadEndingExactlyOnAChunkBoundaryStopsThereCleanly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        // Starts mid-chunk-0, ends exactly at chunk 1's first byte.
        int start = 512;
        int end = image.ChunkStart(1);

        stream.Position = start;
        byte[] read = new byte[end - start];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, end - start).ToArray(), read);
        Assert.Equal(end, stream.Position);
    }

    [Fact]
    public void AReadStartingExactlyOnAChunkBoundaryBeginsCleanly()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        // Chunk 2 starts here exactly - the position IS a StartSector, so this
        // exercises the binary search's exact-match branch rather than the
        // interpolating one, as the counterpart to every other test in this file.
        int start = image.ChunkStart(2);

        stream.Position = start;
        byte[] read = new byte[512];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, 512).ToArray(), read);
    }

    [Fact]
    public void ReadingExactlyOneWholeChunkAtItsOwnEdgesMatchesThatChunkAlone()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using var counting = new CountingStream(image.OpenSource(), leaveOpen: false);
        using DmgBlockStream stream = OpenOver(counting);

        counting.Reset();

        int start = image.ChunkStart(3);
        int length = image.ChunkStart(4) - start;

        stream.Position = start;
        byte[] read = new byte[length];
        stream.ReadExactly(read);

        Assert.Equal(image.Decoded.AsSpan(start, length).ToArray(), read);
        Assert.Equal(1, counting.Reads);
    }

    [Fact]
    public void AReadThatBeginsPastTheEndReturnsZeroImmediately()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        stream.Position = image.Length + (3 * 512);
        Assert.Equal(0, stream.Read(new byte[4096]));
    }

    [Fact]
    public void AReadThatWouldRunPastTheEndIsTruncatedToWhatRemains()
    {
        SyntheticUdifImage image = MultiSectorImage();
        using DmgBlockStream stream = Open(image);

        // Starts inside the last chunk, asks for more than is left.
        int remaining = 512 + 200;
        stream.Position = image.Length - remaining;

        byte[] buffer = new byte[4096];
        int read = stream.Read(buffer);

        Assert.Equal(remaining, read);
        Assert.Equal(image.Decoded.AsSpan(image.Length - remaining, remaining).ToArray(), buffer[..remaining]);
    }

    private static DmgBlockStream Open(SyntheticUdifImage image) =>
        OpenOver(image.OpenSource(), leaveOpen: false);

    private static DmgBlockStream OpenOver(Stream source, bool leaveOpen = true)
    {
        Result<DmgBlockStream> opened = DmgBlockStream.Open(source, leaveOpen);
        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        return opened.Value!;
    }
}
