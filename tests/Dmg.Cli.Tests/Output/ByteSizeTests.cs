using Dmg.Cli.Output;

namespace Dmg.Cli.Tests.Output;

/// <summary>
/// Byte counts the way a person reads them, pinned exactly because a reader
/// checking a size against the exact byte count alongside it needs the rounding
/// to be predictable rather than merely plausible.
/// </summary>
public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0UL, "0 bytes")]
    [InlineData(1UL, "1 bytes")]
    [InlineData(512UL, "512 bytes")]
    [InlineData(1023UL, "1023 bytes")]
    public void WholeBytesBelowAKibibyteAreNotFractional(ulong bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData(1024UL, "1.00 KiB")]
    [InlineData(1536UL, "1.50 KiB")]
    [InlineData(9 * 1024UL, "9.00 KiB")]
    public void BelowTenUnitsGetsTwoDecimals(ulong bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData(10 * 1024UL, "10.0 KiB")]
    [InlineData(941 * 1024UL * 1024UL + 314573UL, "941.3 MiB")]
    public void TenAndAboveGetsOneDecimal(ulong bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void EachUnitStepsAtOneThousandTwentyFourBytes()
    {
        Assert.Equal("1.00 MiB", ByteSize.Format(1024UL * 1024UL));
        Assert.Equal("1.00 GiB", ByteSize.Format(1024UL * 1024UL * 1024UL));
        Assert.Equal("1.00 TiB", ByteSize.Format(1024UL * 1024UL * 1024UL * 1024UL));
    }

    [Fact]
    public void TheLargestUnitIsExbibytesAndDoesNotOverflowPastIt()
    {
        string formatted = ByteSize.Format(ulong.MaxValue);

        Assert.EndsWith("EiB", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedCountFormatsTheSameAsTheUnsignedOne() =>
        Assert.Equal(ByteSize.Format(2_576_980_377UL), ByteSize.Format(2_576_980_377L));

    [Fact]
    public void ANegativeSignedCountIsClampedToZeroRatherThanThrowing() =>
        Assert.Equal("0 bytes", ByteSize.Format(-1L));

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(999UL, "999")]
    [InlineData(1_000UL, "1,000")]
    [InlineData(5_033_164UL, "5,033,164")]
    public void CountsGetThousandsSeparators(ulong count, string expected) =>
        Assert.Equal(expected, ByteSize.Count(count));

    [Fact]
    public void AnIntCountFormatsTheSameAsTheUnsignedOne() =>
        Assert.Equal(ByteSize.Count(2_462UL), ByteSize.Count(2_462));

    [Fact]
    public void ANegativeIntCountIsClampedToZero() =>
        Assert.Equal("0", ByteSize.Count(-5));
}
