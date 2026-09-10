using System.Security.Cryptography;
using Dmg.Core.Containers;
using Dmg.Core.Imaging;
using Dmg.Core.Tests.Codecs;

namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// The block stream against Apple's own decoder: read the whole of a real image
/// through <see cref="DmgBlockStream"/> and check the hash against the manifest.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic tests prove the arithmetic. This one proves the arithmetic was
/// applied to the right format, because the expected hash was produced by
/// <c>hdiutil</c> and never by us - see <see cref="FixtureCorpus"/> for why
/// nothing here is a committed constant.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> <c>fixtures/generated/</c> is gitignored and
/// <c>hdiutil</c> is macOS-only, so a clean checkout and every non-Mac CI runner
/// simply has no corpus. Those runs report that and pass, rather than going red
/// over something the machine could never have had.
/// </para>
/// </remarks>
public sealed class DmgBlockStreamFixtureTests
{
    /// <summary>
    /// Fixtures whose whole decoded stream this build is expected to produce.
    /// </summary>
    /// <remarks>
    /// <c>exfat-raw.dmg</c> is deliberately absent. <c>hdiutil</c>'s UDRW output is
    /// a flat sector image with no <c>koly</c> trailer at all - its size is exactly
    /// its decoded size - so it is the raw-image probe's business, not this one's.
    /// The encrypted pair are absent for the same kind of reason: they are an
    /// <c>encrcdsa</c> container, and the decrypting decorator that unwraps them is
    /// E4's.
    /// </remarks>
    public static TheoryData<string> DecodableFixtures() =>
    [
        "exfat-zlib.dmg",
        "exfat-sparse.dmg",
        "adc.dmg",
        "fat32.dmg",
        "hfsplus.dmg",
        "apfs.dmg",
        "multipart.dmg",
    ];

    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void TheWholeImageReadsBackToApplesHash(string name)
    {
        if (Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream stream = Open(file);

        Assert.True(
            stream.Length >= record.DecodedSize,
            $"{name}: the disk is {stream.Length} bytes but the manifest's ground truth "
            + $"covers {record.DecodedSize}.");

        (string hash, bool tailIsZero) = HashPrefix(stream, record.DecodedSize);

        Assert.True(
            tailIsZero,
            $"{name}: bytes past the manifest's decoded_size are not all zero, so the "
            + "ground-truth hash cannot cover the rest of the disk.");

        Assert.Equal(record.DecodedSha256, hash);
    }

    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void SeekingToEachChunkReadsTheSameBytesAsAStraightPass(string name)
    {
        // The straight pass walks the extents in order and never seeks. This one
        // jumps to a different chunk every time, which is the access pattern a
        // filesystem driver actually produces.
        if (Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        using DmgBlockStream stream = Open(file);

        Assert.True(stream.Length <= int.MaxValue, $"{name} is too large for this test.");

        byte[] sequential = new byte[stream.Length];
        stream.ReadExactly(sequential);

        IReadOnlyList<Extent> extents = stream.Image.Index.Extents;

        // Read the last sector of every extent, back to front, so no read ever
        // continues where the previous one stopped.
        for (int ordinal = extents.Count - 1; ordinal >= 0; ordinal--)
        {
            Extent extent = extents[ordinal];
            long lastSector = (long)(extent.StartSector + extent.SectorCount - 1);
            long offset = lastSector * 512;

            stream.Position = offset;

            byte[] sector = new byte[512];
            stream.ReadExactly(sector);

            Assert.True(
                sector.AsSpan().SequenceEqual(sequential.AsSpan((int)offset, 512)),
                $"{name}: sector {lastSector} (extent {ordinal}, {extent.EntryType}) differs "
                + "between a sequential pass and a direct seek.");
        }
    }

    [Fact]
    public void AnImageInAnUnsupportedCodecOpensButWillNotRead()
    {
        // bzip2. `dmg info` has to work on this image, so opening must succeed and
        // only the read may be refused.
        if (Skipped("bzip2.dmg", out FixtureRecord? record))
        {
            return;
        }

        using FileStream file = File.OpenRead(record!.Path);
        Result<DmgImage> opened = DmgImage.Open(file);

        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());

        using DmgBlockStream stream = Open(file);

        DmgStreamException failure = Assert.Throws<DmgStreamException>(
            () => stream.ReadExactly(new byte[(int)Math.Min(stream.Length, 1 << 20)]));

        Assert.Equal(DmgExitCode.UnsupportedFormat, failure.Code);
    }

    /// <summary>
    /// Hashes the first <paramref name="limit"/> bytes and reports whether the rest
    /// is zeros.
    /// </summary>
    /// <remarks>
    /// <c>hdiutil convert -format UDTO</c> stops writing at the last non-zero
    /// sector, so for an image with trailing free space the ground-truth hash
    /// covers a prefix of the disk. Everything past the limit is still read - it
    /// just has to be zeros.
    /// </remarks>
    internal static (string Sha256, bool TailIsZero) HashPrefix(Stream stream, long limit)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[1 << 16];
        long position = 0;
        bool tailIsZero = true;

        while (true)
        {
            int read = stream.Read(buffer);

            if (read == 0)
            {
                break;
            }

            int toHash = limit < 0 ? read : (int)Math.Clamp(limit - position, 0, read);

            if (toHash > 0)
            {
                hash.AppendData(buffer, 0, toHash);
            }

            if (toHash < read && buffer.AsSpan(toHash, read - toHash).ContainsAnyExcept((byte)0))
            {
                tailIsZero = false;
            }

            position += read;
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), tailIsZero);
    }

    /// <summary>Opens a fixture through the block stream, failing the test if it will not open.</summary>
    internal static DmgBlockStream Open(Stream source)
    {
        Result<DmgBlockStream> opened = DmgBlockStream.Open(source, leaveOpen: true);
        Assert.True(opened.Ok, opened.Ok ? "" : opened.Error.ToString());
        return opened.Value!;
    }

    /// <summary>
    /// True when there is no usable copy of this fixture, having said so. xunit v2
    /// has no dynamic skip, so a missing corpus is reported and passed rather than
    /// failing a machine that could never have had one.
    /// </summary>
    internal static bool Skipped(string name, out FixtureRecord? record)
    {
        record = FixtureCorpus.Find(name);

        if (record is not null)
        {
            return false;
        }

        Console.WriteLine(
            $"SKIPPED: {name} is not available. "
            + (FixtureCorpus.UnavailableReason ?? FixtureCorpus.Regenerate));

        return true;
    }
}
