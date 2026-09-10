using Dmg.Core.Imaging;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Round-trip conformance for the fixed VHD writer, against real images: what
/// went in comes back out byte for byte, and the footer that was written parses
/// back to the geometry that was intended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not covered by the synthetic writer tests.</b> Those feed the
/// writer a <see cref="MemoryStream"/> of bytes they made up, so they prove the
/// copy loop and the padding arithmetic and nothing else. Here the source is a
/// <see cref="DmgBlockStream"/> over an image <c>hdiutil</c> produced: the reads
/// come back in chunk-sized pieces, some of them decompressed, some of them
/// zero-fill that was never stored at all, and a short read at an extent boundary
/// would be invisible to a MemoryStream test and catastrophic here.
/// </para>
/// <para>
/// <b>The property being checked is equality, not a hash.</b> A hash comparison
/// says "different" and stops; this says which byte, which sector and which
/// extent, because a codec bug shows itself in <i>where</i> the streams diverge.
/// The manifest's own hash is checked too, so a writer that faithfully reproduced
/// a wrongly decoded stream would still be caught.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> <c>fixtures/generated/</c> is gitignored and
/// <c>hdiutil</c> is macOS-only, so a clean checkout and every non-Mac runner has
/// no corpus. Those runs say so and pass.
/// </para>
/// </remarks>
public sealed class VhdRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-vhd-roundtrip",
        Guid.NewGuid().ToString("N"));

    public VhdRoundTripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// One fixture per storage strategy the writer's source can use.
    /// </summary>
    /// <remarks>
    /// <c>exfat-udro.dmg</c> is the raw one - real <c>UDIF_RAW</c> chunks, stored
    /// uncompressed - and <c>exfat-zlib.dmg</c> the zlib one; they are the same
    /// 12 MiB volume written two ways, so a difference between them is the codec
    /// and nothing else. <c>exfat-sparse.dmg</c> is the mostly-empty 48 MiB volume,
    /// which is almost entirely zero-fill chunks that occupy no bytes in the
    /// container at all - the case where the writer has to materialise data that
    /// was never stored. <c>zerofill.dmg</c> and <c>adc.dmg</c> come along to cover
    /// interleaved zero-fill and the ADC codec.
    /// </remarks>
    public static TheoryData<string> RoundTrippableFixtures() =>
    [
        "exfat-udro.dmg",
        "exfat-zlib.dmg",
        "exfat-sparse.dmg",
        "zerofill.dmg",
        "adc.dmg",
    ];

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheSectorRangeIsByteIdenticalToTheSourceStream(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = Write(fixture, out VhdWriteResult result);

        using FileStream vhd = File.OpenRead(vhdPath);
        using FileStream container = File.OpenRead(fixture.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Assert.Equal(source.Length, result.SourceBytes);

        long offset = Compare(source, vhd, result.SourceBytes);

        Assert.True(
            offset < 0,
            $"{name}: the VHD's sector range differs from the decoded image at byte {offset} "
            + $"(sector {offset / 512}).");
    }

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheFooterParsesBackToTheGeometryItWasWrittenWith(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = Write(fixture, out VhdWriteResult result);

        byte[] tail = ReadTail(vhdPath, VhdFooter.Length);

        Result<VhdFooter> parsed = VhdFooter.Parse(tail);

        Assert.True(parsed.TryGetValue(out VhdFooter? footer), $"{name}: the written footer must parse.");

        // The geometry is the field a hypervisor actually acts on, and the one an
        // off-by-one in the CHS arithmetic would corrupt silently: an image that
        // attaches and reports the wrong number of sectors is worse than one that
        // refuses to attach.
        Assert.Equal(result.Footer.Geometry, footer.Geometry);
        Assert.Equal(result.Footer.DiskSize, footer.DiskSize);
        Assert.Equal(VhdDiskType.Fixed, footer.DiskType);
        Assert.Equal(result.Footer.UniqueId, footer.UniqueId);
        Assert.Equal(result.Footer.CreatorApplication, footer.CreatorApplication);
        Assert.Equal(result.Footer.CreatorHostOs, footer.CreatorHostOs);
        Assert.Equal(result.Footer.DataOffset, footer.DataOffset);
        Assert.Equal(result.Footer.Checksum, footer.Checksum);

        // The geometry is the one the disk size derives, and it is short of the
        // disk by less than a cylinder. The specification's CHS algorithm divides
        // with truncation, so C * H * S is normally a little under the real sector
        // count - that shortfall is what Windows itself writes, and the size fields
        // rather than the geometry are what carries the true capacity. What would
        // be a bug is a geometry claiming MORE sectors than the disk has, or one
        // short by a whole cylinder or more, which means the arithmetic slipped.
        long cylinder = (long)footer.Geometry.Heads * footer.Geometry.SectorsPerTrack;

        Assert.Equal(VhdGeometry.ForDiskSize(footer.DiskSize), footer.Geometry);

        Assert.InRange(
            footer.TotalSectors - footer.Geometry.TotalSectors,
            0,
            cylinder - 1);

        // Byte-identical, not merely equivalent: the parse must be lossless.
        Assert.True(footer.ToArray().AsSpan().SequenceEqual(tail));
    }

    [Theory]
    [MemberData(nameof(RoundTrippableFixtures))]
    public void TheFileIsExactlyTheDiskPlusOneFooterAndTheTailIsSectorAligned(string name)
    {
        if (DmgBlockStreamFixtureTests.Skipped(name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = Write(fixture, out VhdWriteResult result);

        long fileSize = new FileInfo(vhdPath).Length;

        Assert.Equal(result.TotalBytes, fileSize);
        Assert.Equal(result.DiskSize + VhdFooter.Length, fileSize);
        Assert.Equal(VhdWriter.FixedFileSizeFor(result.SourceBytes), fileSize);
        Assert.Equal(0, result.DiskSize % VhdFooter.SectorSize);

        // Every one of these images is already a whole number of sectors, so
        // nothing should have been padded; if that ever stops being true the
        // padding still has to be zeros, which the next assertion covers.
        Assert.Equal(result.SourceBytes, result.PayloadBytes);

        if (result.WasPadded)
        {
            using FileStream vhd = File.OpenRead(vhdPath);
            vhd.Position = result.SourceBytes;

            byte[] padding = new byte[result.PayloadBytes - result.SourceBytes];
            vhd.ReadExactly(padding);

            Assert.False(padding.AsSpan().ContainsAnyExcept((byte)0));
        }
    }

    [Fact]
    public void TheVhdCarriesTheDecodedStreamApplesOwnDecoderProduced()
    {
        // Byte-for-byte agreement with our own reader would still be agreement with
        // our own bug. This pins the written payload to hdiutil's hash of the same
        // image, so the round trip is anchored to something we did not compute.
        const string Name = "exfat-zlib.dmg";

        if (DmgBlockStreamFixtureTests.Skipped(Name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = Write(fixture, out VhdWriteResult result);

        Assert.True(
            result.DiskSize >= fixture.DecodedSize,
            $"{Name}: the VHD is {result.DiskSize} bytes but the ground truth covers {fixture.DecodedSize}.");

        using FileStream vhd = File.OpenRead(vhdPath);

        // The footer is not part of the disk, so it is not part of the hash.
        using SubStream disk = new(vhd, result.DiskSize);

        (string hash, bool tailIsZero) = DmgBlockStreamFixtureTests.HashPrefix(disk, fixture.DecodedSize);

        Assert.True(tailIsZero, $"{Name}: bytes past the ground truth's reach are not all zero.");
        Assert.Equal(fixture.DecodedSha256, hash);
    }

    [Fact]
    public void AFlatRawImageRoundTripsThroughTheWriterUntouched()
    {
        // UDRW output has no koly trailer at all - it is already a flat sector
        // image - so it never reaches DmgBlockStream. It is still a stream the
        // writer must copy verbatim, and it is the one fixture where "byte
        // identical to the source" can be checked against the file on disk rather
        // than against another decode.
        const string Name = "exfat-raw.dmg";

        if (DmgBlockStreamFixtureTests.Skipped(Name, out FixtureRecord? record))
        {
            return;
        }

        FixtureRecord fixture = record!;
        string vhdPath = Path.Combine(_root, "flat.vhd");

        using (FileStream flat = File.OpenRead(fixture.Path))
        {
            Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(flat, vhdPath);

            Assert.True(written.TryGetValue(out VhdWriteResult? result), Because(written));
            Assert.Equal(fixture.ImageSize, result.SourceBytes);
            Assert.Equal(fixture.ImageSize + VhdFooter.Length, result.TotalBytes);
        }

        using FileStream original = File.OpenRead(fixture.Path);
        using FileStream vhd = File.OpenRead(vhdPath);

        Assert.True(Compare(original, vhd, fixture.ImageSize) < 0, $"{Name}: the payload was altered.");
        Assert.True(VhdFooter.Parse(ReadTail(vhdPath, VhdFooter.Length)).Ok);
    }

    [Fact]
    public void TwoWritesOfTheSameImageProduceTheSameFileWhenTheFooterFieldsAreFixed()
    {
        // The unique id and the timestamp are the only things that vary between
        // runs, so pinning them must make the whole file reproducible. Anything
        // else that differs is non-determinism in the decoder or the copy loop.
        const string Name = "exfat-sparse.dmg";

        if (DmgBlockStreamFixtureTests.Skipped(Name, out FixtureRecord? record))
        {
            return;
        }

        VhdWriteOptions options = new()
        {
            UniqueId = new Guid("2f1c9b6e-0000-4000-8000-abcdefabcdef"),
            CreatedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            BufferSize = 512 * 1024,
        };

        FixtureRecord fixture = record!;

        string first = WriteWith(fixture, "first.vhd", options with { BufferSize = 512 * 1024 });
        string second = WriteWith(fixture, "second.vhd", options with { BufferSize = VhdFooter.SectorSize * 3 });

        using FileStream left = File.OpenRead(first);
        using FileStream right = File.OpenRead(second);

        Assert.Equal(left.Length, right.Length);
        Assert.True(Compare(left, right, left.Length) < 0, $"{Name}: the two writes differ.");
    }

    /// <summary>Writes a fixture out as a fixed VHD and returns the path.</summary>
    private string Write(FixtureRecord record, out VhdWriteResult result)
    {
        string vhdPath = Path.Combine(_root, record.Name + ".vhd");

        using FileStream container = File.OpenRead(record.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(source, vhdPath);

        Assert.True(written.TryGetValue(out VhdWriteResult? value), Because(written));

        result = value;

        return vhdPath;
    }

    /// <summary>Writes a fixture out with particular options, and returns the path.</summary>
    private string WriteWith(FixtureRecord record, string fileName, VhdWriteOptions options)
    {
        string vhdPath = Path.Combine(_root, fileName);

        using FileStream container = File.OpenRead(record.Path);
        using DmgBlockStream source = DmgBlockStreamFixtureTests.Open(container);

        Result<VhdWriteResult> written = VhdWriter.WriteFixedToFile(source, vhdPath, options);

        Assert.True(written.Ok, Because(written));

        return vhdPath;
    }

    /// <summary>
    /// Compares <paramref name="length"/> bytes of two streams from their current
    /// position, and returns the offset of the first difference, or -1 when there
    /// is none.
    /// </summary>
    /// <remarks>
    /// Streamed in 64 KiB pieces rather than read into two arrays: the sparse
    /// fixture decodes to 48 MB and the multipart one to 80 MB, and a round-trip
    /// test that only works while the image fits in memory is not testing the
    /// thing the writer exists for.
    /// </remarks>
    private static long Compare(Stream expected, Stream actual, long length)
    {
        byte[] left = new byte[64 * 1024];
        byte[] right = new byte[64 * 1024];
        long position = 0;

        while (position < length)
        {
            int wanted = (int)Math.Min(left.Length, length - position);

            expected.ReadExactly(left, 0, wanted);
            actual.ReadExactly(right, 0, wanted);

            if (!left.AsSpan(0, wanted).SequenceEqual(right.AsSpan(0, wanted)))
            {
                for (int index = 0; index < wanted; index++)
                {
                    if (left[index] != right[index])
                    {
                        return position + index;
                    }
                }
            }

            position += wanted;
        }

        return -1;
    }

    /// <summary>The last <paramref name="length"/> bytes of a file.</summary>
    private static byte[] ReadTail(string path, int length)
    {
        using FileStream file = File.OpenRead(path);

        file.Position = file.Length - length;

        byte[] tail = new byte[length];
        file.ReadExactly(tail);

        return tail;
    }

    private static string Because<T>(Result<T> result) => result.Ok ? "" : result.Error.ToString();

    /// <summary>
    /// A read-only window over the first <paramref name="length"/> bytes of a
    /// stream, so the payload can be hashed without the footer and without copying
    /// it anywhere.
    /// </summary>
    private sealed class SubStream(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int wanted = (int)Math.Min(buffer.Length, length - _position);

            if (wanted <= 0)
            {
                return 0;
            }

            int read = inner.Read(buffer[..wanted]);

            _position += read;

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
