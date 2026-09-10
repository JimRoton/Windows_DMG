using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The reading primitives every other container parser is built on. If these are
/// wrong, everything above them is wrong in a way that looks like data corruption.
/// </summary>
public sealed class BigEndianTests
{
    [Fact]
    public void ReadsBigEndianNotLittleEndian()
    {
        byte[] bytes = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

        Assert.True(BigEndian.TryReadUInt16(bytes, 0, out ushort sixteen));
        Assert.Equal(0x1234, sixteen);

        Assert.True(BigEndian.TryReadUInt32(bytes, 0, out uint thirtyTwo));
        Assert.Equal(0x12345678u, thirtyTwo);

        Assert.True(BigEndian.TryReadUInt64(bytes, 0, out ulong sixtyFour));
        Assert.Equal(0x123456789ABCDEF0ul, sixtyFour);
    }

    [Fact]
    public void ReadsFromAnOffset()
    {
        byte[] bytes = [0x00, 0x00, 0xFF, 0xEE, 0xDD, 0xCC];

        Assert.True(BigEndian.TryReadUInt32(bytes, 2, out uint value));
        Assert.Equal(0xFFEEDDCCu, value);
    }

    [Theory]
    [InlineData(5)]   // four bytes wanted, three available
    [InlineData(8)]   // exactly at the end
    [InlineData(99)]  // far past the end
    [InlineData(-1)]  // negative offsets come out of arithmetic on hostile fields
    [InlineData(int.MinValue)]
    public void RefusesReadsThatDoNotFit(int offset)
    {
        byte[] bytes = new byte[8];

        Assert.False(BigEndian.TryReadUInt32(bytes, offset, out uint value));
        Assert.Equal(0u, value);
    }

    [Fact]
    public void AnEmptyBufferReadsNothing()
    {
        Assert.False(BigEndian.TryReadUInt16([], 0, out _));
        Assert.False(BigEndian.TryReadUInt32([], 0, out _));
        Assert.False(BigEndian.TryReadUInt64([], 0, out _));
    }

    [Fact]
    public void AFailedReadIsACorruptImageFailureThatNamesTheField()
    {
        Result<uint> result = BigEndian.ReadUInt32(new byte[2], 0, "koly.HeaderSize");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("koly.HeaderSize", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASucceedingReadCarriesTheValue()
    {
        Result<ulong> result = BigEndian.ReadUInt64([1, 0, 0, 0, 0, 0, 0, 0], 0, "mish.FirstSectorNumber");

        Assert.True(result.TryGetValue(out ulong value));
        Assert.Equal(0x0100000000000000ul, value);
    }

    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(4, 4, true)]
    [InlineData(8, 0, true)]
    [InlineData(5, 4, false)]
    [InlineData(-1, 4, false)]
    [InlineData(0, -4, false)]
    [InlineData(0, int.MaxValue, false)]
    public void SlicingIsBoundsChecked(int offset, int length, bool expected)
    {
        Assert.Equal(expected, BigEndian.TrySlice(new byte[8], offset, length, out _));
    }

    [Fact]
    public void FourCharCodeMatchesTheBytesInTheFile()
    {
        byte[] koly = "koly"u8.ToArray();

        Assert.True(BigEndian.TryReadUInt32(koly, 0, out uint value));
        Assert.Equal(BigEndian.FourCharCode("koly"), value);
        Assert.Equal(0x6B6F6C79u, value);
        Assert.Equal(0x6D697368u, BigEndian.FourCharCode("mish"));
    }

    [Theory]
    [InlineData("kol")]
    [InlineData("kolyx")]
    [InlineData("")]
    public void AMalformedTagConstantIsABugAndThrows(string code) =>
        Assert.Throws<ArgumentException>(() => BigEndian.FourCharCode(code));

    [Fact]
    public void ATagIsDescribedAsTextWhenPrintableAndHexWhenNot()
    {
        Assert.Equal("koly", BigEndian.DescribeFourCharCode(0x6B6F6C79u));
        Assert.Equal("0x00000000", BigEndian.DescribeFourCharCode(0));
        Assert.Equal("0xDEADBEEF", BigEndian.DescribeFourCharCode(0xDEADBEEFu));
    }

    [Fact]
    public void SectorArithmeticIsCheckedAndReportsOverflowAsAFailure()
    {
        Result<ulong> ok = BigEndian.SectorsToBytes(67647, "koly.SectorCount");
        Assert.True(ok.TryGetValue(out ulong bytes));
        Assert.Equal(67647ul * 512, bytes);

        // 2^55 sectors is 2^64 bytes: the classic wrap-to-zero.
        Result<ulong> wrapped = BigEndian.SectorsToBytes(1ul << 55, "koly.SectorCount");
        Assert.False(wrapped.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, wrapped.Error.Code);

        Result<ulong> justFits = BigEndian.SectorsToBytes(ulong.MaxValue / 512, "koly.SectorCount");
        Assert.True(justFits.Ok);
    }

    [Fact]
    public void AdditionAndMultiplicationNeverWrap()
    {
        Assert.False(BigEndian.Add(ulong.MaxValue, 1, "data fork range").Ok);
        Assert.True(BigEndian.Add(ulong.MaxValue, 0, "data fork range").Ok);
        Assert.False(BigEndian.Multiply(ulong.MaxValue, 2, "chunk table size").Ok);
        Assert.True(BigEndian.Multiply(0, ulong.MaxValue, "chunk table size").Ok);
    }

    [Theory]
    [InlineData(0ul, 100ul, 100ul, true)]
    [InlineData(100ul, 0ul, 100ul, true)]
    [InlineData(1ul, 100ul, 100ul, false)]
    [InlineData(101ul, 0ul, 100ul, false)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, 100ul, false)]
    public void RangeChecksDoNotOverflow(ulong offset, ulong length, ulong limit, bool expected) =>
        Assert.Equal(expected, BigEndian.RangeFitsWithin(offset, length, limit));
}
