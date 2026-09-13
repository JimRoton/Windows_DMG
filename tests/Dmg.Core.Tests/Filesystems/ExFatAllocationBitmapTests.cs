using Dmg.Core.Filesystems;
using Dmg.Core.Tests.Projection;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// The allocation bitmap, read off a volume <c>hdiutil</c> actually wrote.
/// </summary>
/// <remarks>
/// <para>
/// This bitmap exists to let <c>dmg mount --dynamic</c> skip free space instead of
/// writing it. That makes a wrong answer expensive in one direction only: reporting
/// a range free when it holds data means the mounted volume has a hole where a file
/// should be, silently. So the assertions below are weighted towards proving the
/// bitmap says <b>not free</b> where it must - over a known file's data, and over
/// the structures below the cluster heap - rather than towards proving it finds
/// free space, which is the harmless direction to get wrong.
/// </para>
/// <para>
/// <b>The bitmap describes allocation, not content.</b> It is deliberately not an
/// <c>IVhdSparseMap</c>: an unallocated cluster on a captured disk holds whatever
/// was deleted from it, which is not zeros. See the type's remarks.
/// </para>
/// </remarks>
public sealed class ExFatAllocationBitmapTests
{
    [SkippableFact]
    public void ThePopulatedVolumeHasABitmapCoveringItsClusterHeap()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);

        Assert.True(bitmap.ClusterCount > 0, "The bitmap covers no clusters at all.");
    }

    [SkippableFact]
    public void AMostlyEmptyVolumeReportsMostOfItsClustersFree()
    {
        // The fixture is a 12 MiB volume holding three small files, so the great
        // majority of it is free. An off-by-two on the bit indexing, or reading the
        // wrong chain, would not produce a number anywhere near this.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);

        Assert.True(
            bitmap.AllocatedClusters > 0,
            "Not one cluster is in use, on a volume with three files on it.");

        Assert.True(
            bitmap.AllocatedClusters < bitmap.ClusterCount / 2,
            $"{bitmap.AllocatedClusters} of {bitmap.ClusterCount} clusters are in use on a "
            + "volume that holds three small files, which is far more than it should be.");
    }

    [SkippableFact]
    public void TheRegionHoldingAKnownFileIsNotFree()
    {
        // The assertion that matters. DATA.BIN is 64 KiB of known content; the
        // clusters under it must never be reported free, because a dynamic write
        // that skipped them would produce a volume with a hole where the file is.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);
        ExFatEntry data = Find(fixture.Reader, "DATA.BIN");

        long fileOffset =
            bitmap.HeapOffsetBytes + ((data.FirstCluster - 2L) * bitmap.BytesPerCluster);

        Assert.False(
            bitmap.IsRangeFree(fileOffset, bitmap.BytesPerCluster),
            "The first cluster of DATA.BIN is reported free, so a dynamic write would "
            + "leave a hole where the file is.");
    }

    [SkippableFact]
    public void AnythingBelowTheClusterHeapIsNeverFree()
    {
        // The boot sector, the FAT and the bitmap itself all live below the heap and
        // are described by no bit in it. Skipping them would produce a volume that
        // does not mount at all.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);

        Assert.False(bitmap.IsRangeFree(0, 512));
        Assert.False(bitmap.IsRangeFree(bitmap.HeapOffsetBytes - 512, 1024));
    }

    [SkippableFact]
    public void NonsenseRangesAreNeverFree()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);

        Assert.False(bitmap.IsRangeFree(-1, 512));
        Assert.False(bitmap.IsRangeFree(bitmap.HeapOffsetBytes, 0));
        Assert.False(bitmap.IsRangeFree(long.MaxValue - 8, 512));

        // Past the end of the heap: the writer pads the disk out to whole blocks and
        // that tail is not the bitmap's to describe.
        long pastTheEnd =
            bitmap.HeapOffsetBytes + ((long)bitmap.ClusterCount * bitmap.BytesPerCluster);

        Assert.False(bitmap.IsRangeFree(pastTheEnd, 512));
    }

    [SkippableFact]
    public void AClusterOutsideTheHeapReadsAsInUse()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ExFatAllocationBitmap bitmap = Read(fixture.Reader);

        Assert.True(bitmap.IsAllocated(0));
        Assert.True(bitmap.IsAllocated(1));
        Assert.True(bitmap.IsAllocated(bitmap.ClusterCount + 2));
        Assert.True(bitmap.IsAllocated(uint.MaxValue));
    }

    private static ExFatAllocationBitmap Read(ExFatReader reader)
    {
        Result<ExFatAllocationBitmap?> read = reader.ReadAllocationBitmap();

        Assert.True(read.Ok, read.Ok ? "" : read.Error.ToString());
        Assert.True(read.Value is not null, "This volume declares no allocation bitmap.");

        return read.Value!;
    }

    private static ExFatEntry Find(ExFatReader reader, string name)
    {
        Result<IReadOnlyList<ExFatEntry>> listed = reader.ReadRootDirectory();

        Assert.True(
            listed.TryGetValue(out IReadOnlyList<ExFatEntry>? entries),
            listed.Ok ? "" : listed.Error.ToString());

        ExFatEntry? found = entries.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        Assert.True(found is not null, $"'{name}' is not in the root directory.");

        return found!;
    }
}
