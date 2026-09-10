using Dmg.Core.Imaging;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Round-trip conformance for the dynamic writer, against real images: the disk
/// a sparse VHD describes is the disk that went in, block table and all.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic dynamic tests prove the allocation arithmetic against images
/// this suite made up. These prove it against images <c>hdiutil</c> made, where
/// the empty parts of the disk are real UDIF zero-fill chunks rather than arrays
/// of zeros - which is the case the whole feature exists for.
/// </para>
/// <para>
/// <b>The anchor is the fixed VHD.</b> A dynamic and a fixed write of the same
/// image must describe the same disk, and the fixed one is already pinned to
/// hdiutil's own hash by <see cref="VhdRoundTripTests"/>. So a bug that affected
/// both writers would still be caught, one that affects only the sparse path is
/// caught here, and neither test has to trust the other's reader.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> No corpus, no hdiutil, no complaint.
/// </para>
/// </remarks>
public sealed class VhdDynamicRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-dynamic-roundtrip",
        Guid.NewGuid().ToString("N"));

    public VhdDynamicRoundTripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Raw chunks, zlib chunks, a mostly-empty volume, and interleaved zero-fill.</summary>
    public static TheoryData<string> RoundTrippableFixtures() =>
    [
        "exfat-udro.dmg",
        "exfat-zlib.dmg",
        "exfat-sparse.dmg",
        "zerofill.dmg",
    ];

    private static VhdWriteOptions Options(int blockSize = VhdDynamicHeader.DefaultBlockSize) => new()
    {
        BlockSize = blockSize,
        UniqueId = new Guid("9a7c3f10-1111-4222-8333-444455556666"),
        CreatedUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
    };

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheDynamicDiskIsByteIdenticalToTheDecodedSource(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = WriteDynamic(fixture, "dynamic.vhd", Options(), withMap: true);

        using DynamicVhdImage image = DynamicVhdImage.Open(vhdPath);
        using Stream disk = image.OpenDisk();

        using FileStream container = File.OpenRead(fixture.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Assert.Equal(source.Length, image.DiskSize);

        long offset = VhdRoundTripTests.Compare(source, disk, image.DiskSize);

        Assert.True(
            offset < 0,
            $"{name}: the sparse VHD's disk differs from the decoded image at byte {offset} "
            + $"(sector {offset / 512}, block {offset / image.Header.BlockSize}).");
    }

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheDynamicVhdDescribesTheSameDiskAsTheFixedOne(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string dynamicPath = WriteDynamic(fixture, "dynamic.vhd", Options(), withMap: true);
        string fixedPath = WriteFixed(fixture, "fixed.vhd", Options());

        using DynamicVhdImage image = DynamicVhdImage.Open(dynamicPath);
        using Stream disk = image.OpenDisk();

        using FileStream flat = File.OpenRead(fixedPath);

        // The fixed VHD's payload is everything before its footer.
        long payload = flat.Length - VhdFooter.Length;

        Assert.Equal(payload, image.DiskSize);
        Assert.True(VhdRoundTripTests.Compare(flat, disk, payload) < 0, $"{name}: the two writers disagree.");

        // Same disk, so the same geometry and the same capacity - only the disk type
        // and the data offset differ, which is what makes one sparse.
        byte[] fixedFooterBytes = new byte[VhdFooter.Length];
        flat.Position = payload;
        flat.ReadExactly(fixedFooterBytes);

        Result<VhdFooter> parsed = VhdFooter.Parse(fixedFooterBytes);

        Assert.True(parsed.TryGetValue(out VhdFooter? fixedFooter), parsed.Ok ? "" : parsed.Error.ToString());
        Assert.Equal(fixedFooter.DiskSize, image.Footer.DiskSize);
        Assert.Equal(fixedFooter.Geometry, image.Footer.Geometry);
        Assert.Equal(fixedFooter.UniqueId, image.Footer.UniqueId);
        Assert.Equal(VhdDiskType.Fixed, fixedFooter.DiskType);
        Assert.Equal(VhdDiskType.Dynamic, image.Footer.DiskType);
    }

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheMapChangesWhatIsReadAndNotWhatIsWritten(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;

        string mapped = WriteDynamic(fixture, "mapped.vhd", Options(), withMap: true);
        string blind = WriteDynamic(fixture, "blind.vhd", Options(), withMap: false);

        using FileStream left = File.OpenRead(mapped);
        using FileStream right = File.OpenRead(blind);

        Assert.Equal(left.Length, right.Length);
        Assert.True(
            VhdRoundTripTests.Compare(left, right, left.Length) < 0,
            $"{name}: consulting the chunk table changed the output, which it must not.");
    }

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void EveryRangeTheMapCallsZeroReallyIsZero(string name)
    {
        // The map's contract is one-sided: false is always safe, true had better be
        // true. A map that lied would produce a VHD with holes where data was.
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;

        using FileStream container = File.OpenRead(fixture.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        DmgSparseMap map = DmgSparseMap.For(source);

        const int Window = 64 * 1024;
        byte[] buffer = new byte[Window];
        int claimed = 0;

        for (long offset = 0; offset < source.Length; offset += Window)
        {
            int length = (int)Math.Min(Window, source.Length - offset);

            if (!map.IsKnownZero(offset, length))
            {
                continue;
            }

            claimed++;
            source.Position = offset;
            source.ReadExactly(buffer, 0, length);

            Assert.False(
                buffer.AsSpan(0, length).ContainsAnyExcept((byte)0),
                $"{name}: the map called [{offset}, {offset + length}) zeros and it is not.");
        }

        Console.WriteLine($"{name}: the map vouched for {claimed} of {(source.Length + Window - 1) / Window} windows.");
    }

    [Fact]
    public void AMostlyEmptyVolumeCostsAFractionOfItsFixedVhd()
    {
        // The reason the feature exists. exfat-sparse is a 48 MiB volume with almost
        // nothing in it, and hdiutil stored almost all of it as zero-fill chunks.
        const string Name = "exfat-sparse.dmg";

        if (DmgBlockStreamFixtureTests.Skipped(Name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = WriteDynamic(fixture, "cheap.vhd", Options(512 * 1024), withMap: true);

        using DynamicVhdImage image = DynamicVhdImage.Open(vhdPath);

        long fixedSize = VhdWriter.FixedFileSizeFor(image.DiskSize);

        Assert.True(image.UnallocatedBlocks > image.AllocatedBlocks, "Most of this volume is empty.");
        Assert.True(
            image.FileSize < fixedSize / 2,
            $"{Name}: the sparse VHD cost {image.FileSize} bytes against the fixed VHD's {fixedSize}.");

        Console.WriteLine(
            $"{Name}: {image.AllocatedBlocks} of {image.Table.Length} blocks allocated, "
            + $"{image.FileSize} bytes against a fixed {fixedSize}.");
    }

    [Fact]
    public void ThePrecheckWithAMapAsksForWhatTheWriteActuallyCosts()
    {
        const string Name = "exfat-sparse.dmg";

        if (DmgBlockStreamFixtureTests.Skipped(Name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;

        using FileStream container = File.OpenRead(fixture.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Result<long> estimated = VhdWriter.DynamicFileSizeFor(
            source.Length,
            512 * 1024,
            DmgSparseMap.For(source));

        Assert.True(estimated.TryGetValue(out long upperBound));

        string vhdPath = WriteDynamic(fixture, "estimated.vhd", Options(512 * 1024), withMap: true);
        long actual = new FileInfo(vhdPath).Length;

        // An upper bound, because a block the map did not vouch for can still turn
        // out to be zeros - but a tight one, or the precheck is no better than the
        // worst case.
        Assert.True(actual <= upperBound, $"The write cost {actual}, more than the {upperBound} predicted.");
        Assert.True(upperBound < VhdWriter.FixedFileSizeFor(source.Length));
    }

    private string WriteDynamic(FixtureRecord record, string fileName, VhdWriteOptions options, bool withMap)
    {
        string vhdPath = Path.Combine(_root, fileName);

        using FileStream container = File.OpenRead(record.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Result<VhdWriteResult> written = VhdWriter.WriteDynamicToFile(
            source,
            vhdPath,
            options,
            progress: null,
            freeSpaceProbe: null,
            withMap ? DmgSparseMap.For(source) : null);

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        return vhdPath;
    }

    private string WriteFixed(FixtureRecord record, string fileName, VhdWriteOptions options)
    {
        string vhdPath = Path.Combine(_root, fileName);

        using FileStream container = File.OpenRead(record.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(source, vhdPath, options);

        Assert.True(written.Ok, written.Ok ? "" : written.Error.ToString());

        return vhdPath;
    }
}
