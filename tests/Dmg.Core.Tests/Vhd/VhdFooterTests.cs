using System.Buffers.Binary;
using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Verifies the footer byte-for-byte against a vector assembled by hand from the
/// VHD specification's field table, rather than against whatever
/// <see cref="VhdFooter"/> happens to emit. The point of this suite is that a
/// footer written here is a footer Windows will accept, and the only way to check
/// that without Windows is to check it against the document Windows implements.
/// </summary>
public sealed class VhdFooterTests
{
    private const long SixteenMegabytes = 16L * 1024 * 1024;

    /// <summary>Chosen so its big-endian byte order is legible: 12 34 56 78 12 34 56 78 9A BC DE F0 12 34 56 78.</summary>
    private static readonly Guid SampleId = new("12345678-1234-5678-9abc-def012345678");

    private static readonly DateTimeOffset SampleTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 7305 days from 2000-01-01 to 2020-01-01 (twenty years, five of them leap),
    /// times 86400 seconds. 631152000 == 0x259E9D80.
    /// </summary>
    private const uint SampleTimeStamp = 631_152_000;

    /// <summary>
    /// The one's complement of the byte sum of <see cref="BuildSpecificationVector"/>
    /// with the checksum field zeroed. That sum is 5961 (0x1749), so the checksum is
    /// 0xFFFFFFFF - 0x1749 == 0xFFFFE8B6.
    /// </summary>
    private const uint SampleChecksum = 0xFFFF_E8B6;

    [Fact]
    public void MatchesTheSpecificationVectorByteForByte()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);

        Assert.True(built.TryGetValue(out VhdFooter? footer), "Building a 16 MB fixed footer should succeed.");

        Assert.Equal(BuildSpecificationVector(), footer.ToArray());
    }

    [Fact]
    public void FillsTheFieldsTheSpecificationRequiresOfAFixedDisk()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        byte[] bytes = footer.ToArray();

        Assert.Equal(512, bytes.Length);
        Assert.Equal("conectix"u8.ToArray(), bytes[..8]);

        // Features: the specification's reserved bit, and nothing else.
        Assert.Equal(0x0000_0002u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));

        // File format version 1.0.
        Assert.Equal(0x0001_0000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));

        // A fixed disk has no dynamic header, so Data Offset is all ones.
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(16)));

        // Original Size and Current Size both carry the payload size.
        Assert.Equal(SixteenMegabytes, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(40)));
        Assert.Equal(SixteenMegabytes, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(48)));

        // Disk Type 2 == fixed.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(60)));

        // Saved State is a single byte and stays clear.
        Assert.Equal(0, bytes[84]);

        // The final 427 bytes are reserved and must be zero.
        Assert.All(bytes[85..], reserved => Assert.Equal(0, reserved));
    }

    [Fact]
    public void StampsTheTimestampAsSecondsSinceTheYear2000NotTheUnixEpoch()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        Assert.Equal(SampleTimeStamp, BinaryPrimitives.ReadUInt32BigEndian(footer.ToArray().AsSpan(24)));
        Assert.Equal(SampleTime, footer.CreatedUtc);
        Assert.Equal(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), VhdFooter.Epoch);
    }

    [Fact]
    public void EveryMultiByteFieldIsBigEndian()
    {
        // 0x0100_0000 bytes is the trap: written little-endian the size field would
        // read 00 00 00 01 00 00 00 00 and Windows would see a 4 KB disk.
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        byte[] size = footer.ToArray()[48..56];

        Assert.Equal<byte[]>([0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00], size);
    }

    [Fact]
    public void WritesTheUniqueIdInBigEndianRfc4122ByteOrder()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        Assert.Equal<byte[]>(
            [0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78],
            footer.ToArray()[68..84]);
    }

    [Fact]
    public void ChecksumIsTheOnesComplementOfTheByteSum()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        Assert.Equal(SampleChecksum, footer.Checksum);
        Assert.Equal(SampleChecksum, BinaryPrimitives.ReadUInt32BigEndian(footer.ToArray().AsSpan(64)));
    }

    [Fact]
    public void TheByteSumAndTheStoredChecksumAreOnesComplements()
    {
        // The defining property: sum the footer with its checksum field treated as
        // zero, add the stored checksum, and every bit is set.
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(700L * 1024 * 1024, Guid.NewGuid(), SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        byte[] bytes = footer.ToArray();
        uint sum = 0;

        for (int index = 0; index < bytes.Length; index++)
        {
            if (index is >= 64 and < 68)
            {
                continue;
            }

            sum += bytes[index];
        }

        Assert.Equal(uint.MaxValue, sum + BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(64)));
    }

    [Theory]
    // An all-zero buffer sums to zero, so its checksum is ~0.
    [InlineData(0x00, 0xFFFF_FFFFu)]
    // 508 bytes of 0x01 (512 less the four skipped checksum bytes) sum to 508 == 0x1FC.
    [InlineData(0x01, 0xFFFF_FE03u)]
    // 508 bytes of 0xFF sum to 129540 == 0x1FA04.
    [InlineData(0xFF, 0xFFFE_05FBu)]
    public void ChecksumSkipsItsOwnFourBytes(byte fill, uint expected)
    {
        byte[] footer = new byte[512];
        Array.Fill(footer, fill);

        Assert.Equal(expected, VhdFooter.ComputeChecksum(footer));

        // Whatever is sitting in the checksum field must not change the answer.
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), 0xDEAD_BEEF);
        Assert.Equal(expected, VhdFooter.ComputeChecksum(footer));
    }

    [Fact]
    public void RoundTripsThroughParse()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? written));

        Result<VhdFooter> reread = VhdFooter.Parse(written.ToArray());
        Assert.True(reread.TryGetValue(out VhdFooter? read), "A footer this tool wrote must parse.");

        Assert.Equal(written.DiskSize, read.DiskSize);
        Assert.Equal(written.Geometry, read.Geometry);
        Assert.Equal(VhdDiskType.Fixed, read.DiskType);
        Assert.Equal(written.UniqueId, read.UniqueId);
        Assert.Equal(written.CreatedUtc, read.CreatedUtc);
        Assert.Equal(written.Checksum, read.Checksum);
        Assert.Equal("dmg ", read.CreatorApplication);
        Assert.Equal("Wi2k", read.CreatorHostOs);
        Assert.Equal(32768, read.TotalSectors);
    }

    [Fact]
    public void ParseRejectsAFooterWhoseChecksumDoesNotMatch()
    {
        byte[] bytes = BuildSpecificationVector();
        bytes[100] ^= 0xFF;

        Result<VhdFooter> parsed = VhdFooter.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, parsed.Error.Code);
        Assert.Contains("checksum", parsed.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRejectsSomethingThatIsNotAFooterAtAll()
    {
        byte[] bytes = new byte[512];
        Result<VhdFooter> parsed = VhdFooter.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, parsed.Error.Code);
        Assert.Contains("conectix", parsed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsTheWrongNumberOfBytes()
    {
        Result<VhdFooter> parsed = VhdFooter.Parse(new byte[511]);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, parsed.Error.Code);
    }

    [Fact]
    public void ParseRejectsAFixedDiskThatClaimsADynamicHeader()
    {
        byte[] bytes = BuildSpecificationVector();
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16), 512);
        Restamp(bytes);

        Result<VhdFooter> parsed = VhdFooter.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, parsed.Error.Code);
    }

    [Fact]
    public void ParseRejectsAnUnknownFileFormatVersion()
    {
        byte[] bytes = BuildSpecificationVector();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 0x0002_0000);
        Restamp(bytes);

        Result<VhdFooter> parsed = VhdFooter.Parse(bytes);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, parsed.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-512)]
    [InlineData(1000)]
    [InlineData(16 * 1024 * 1024 + 1)]
    public void RefusesASizeThatIsNotAPositiveWholeNumberOfSectors(long diskSize)
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(diskSize, SampleId, SampleTime);

        Assert.False(built.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, built.Error.Code);
    }

    [Fact]
    public void RefusesADiskLargerThanAVhdCanDescribe()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(
            VhdFooter.MaxDiskSize + VhdFooter.SectorSize,
            SampleId,
            SampleTime);

        Assert.False(built.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, built.Error.Code);
        Assert.Contains("2040", built.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsADiskExactlyAtTheMaximum()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(VhdFooter.MaxDiskSize, SampleId, SampleTime);

        Assert.True(built.TryGetValue(out VhdFooter? footer));
        Assert.Equal(VhdFooter.MaxDiskSize, footer.DiskSize);
        Assert.Equal(new VhdGeometry(65535, 16, 255), footer.Geometry);
    }

    [Fact]
    public void RefusesACreationTimeBeforeTheVhdEpoch()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(
            SixteenMegabytes,
            SampleId,
            new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero));

        Assert.False(built.Ok);
        Assert.Equal(DmgExitCode.InternalError, built.Error.Code);
    }

    [Fact]
    public void RefusesACreatorCodeThatIsNotFourPrintableCharacters()
    {
        Assert.False(VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime, "dmg").Ok);
        Assert.False(VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime, "dmgx1").Ok);
        Assert.False(VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime, "dmg ", "Mäc!").Ok);
    }

    [Fact]
    public void ConvertsALocalCreationTimeToUtcBeforeStampingIt()
    {
        DateTimeOffset local = new(2020, 1, 1, 5, 0, 0, TimeSpan.FromHours(5));

        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, local);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        Assert.Equal(SampleTimeStamp, BinaryPrimitives.ReadUInt32BigEndian(footer.ToArray().AsSpan(24)));
    }

    [Fact]
    public void WriteToInsistsOnExactlyFiveHundredAndTwelveBytes()
    {
        Result<VhdFooter> built = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Assert.True(built.TryGetValue(out VhdFooter? footer));

        Assert.Throws<ArgumentException>(() => footer.WriteTo(new byte[511]));
        Assert.Throws<ArgumentException>(() => footer.WriteTo(new byte[513]));
    }

    [Fact]
    public void TwoFootersForTheSameDiskDifferOnlyInIdAndTimestamp()
    {
        Result<VhdFooter> first = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);
        Result<VhdFooter> second = VhdFooter.ForFixedDisk(SixteenMegabytes, SampleId, SampleTime);

        Assert.True(first.TryGetValue(out VhdFooter? a));
        Assert.True(second.TryGetValue(out VhdFooter? b));

        Assert.Equal(a.ToArray(), b.ToArray());
    }

    /// <summary>
    /// The expected 512 bytes, laid out field by field straight from the
    /// specification's table. Deliberately written out longhand: if
    /// <see cref="VhdFooter"/> and this method ever agree only because they share
    /// code, the test is worthless.
    /// </summary>
    private static byte[] BuildSpecificationVector()
    {
        byte[] footer = new byte[512];

        "conectix"u8.CopyTo(footer.AsSpan(0));                                  // Cookie
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 0x0000_0002);   // Features: reserved bit
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(12), 0x0001_0000);  // File Format Version 1.0
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(16), ulong.MaxValue); // Data Offset: none
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(24), SampleTimeStamp); // Time Stamp
        "dmg "u8.CopyTo(footer.AsSpan(28));                                     // Creator Application
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(32), 0x0001_0000);  // Creator Version 1.0
        "Wi2k"u8.CopyTo(footer.AsSpan(36));                                     // Creator Host OS
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(40), SixteenMegabytes); // Original Size
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(48), SixteenMegabytes); // Current Size

        BinaryPrimitives.WriteUInt16BigEndian(footer.AsSpan(56), 481);          // Cylinders
        footer[58] = 4;                                                         // Heads
        footer[59] = 17;                                                        // Sectors per track

        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(60), 2);            // Disk Type: fixed
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), SampleChecksum); // Checksum

        // Unique Id, big-endian.
        ReadOnlySpan<byte> id =
            [0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78];
        id.CopyTo(footer.AsSpan(68));

        footer[84] = 0;                                                         // Saved State
        // 85..512 stay zero: reserved.

        return footer;
    }

    /// <summary>Recomputes and rewrites the checksum after a test has edited a field.</summary>
    private static void Restamp(byte[] footer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), VhdFooter.ComputeChecksum(footer));
    }
}
