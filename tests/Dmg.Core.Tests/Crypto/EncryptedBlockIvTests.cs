using System.Security.Cryptography;
using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// The per-block IV. It is a pure function of the block index, and that purity is
/// the property the whole seekable design rests on.
/// </summary>
public sealed class EncryptedBlockIvTests
{
    private static readonly byte[] HmacKey =
        [.. Enumerable.Range(0, 20).Select(i => (byte)(i * 11 + 5))];

    [Fact]
    public void IsTheFirstSixteenBytesOfHmacSha1OverTheBigEndianBlockNumber()
    {
        // Computed here from the primitive rather than from a recorded constant, so
        // the test states the rule instead of memorising an output.
        byte[] counter = [0, 0, 0x01, 0x2C];
        byte[] expected = HMACSHA1.HashData(HmacKey, counter)[..16];

        Assert.Equal(expected, EncryptedBlockIv.Compute(HmacKey, 300));
    }

    [Fact]
    public void BlockZeroUsesAnAllZeroCounter()
    {
        byte[] expected = HMACSHA1.HashData(HmacKey, new byte[4])[..16];

        Assert.Equal(expected, EncryptedBlockIv.Compute(HmacKey, 0));
    }

    [Fact]
    public void IsSixteenBytesLong()
    {
        Assert.Equal(16, EncryptedBlockIv.Compute(HmacKey, 7).Length);
    }

    [Fact]
    public void DependsOnNothingButTheIndex()
    {
        // Recomputing an IV out of order must give the same answer as computing it
        // in sequence; if it did not, seeking would silently corrupt reads.
        byte[] inOrder = EncryptedBlockIv.Compute(HmacKey, 5);

        for (long block = 100; block > 0; block--)
        {
            EncryptedBlockIv.Compute(HmacKey, block);
        }

        Assert.Equal(inOrder, EncryptedBlockIv.Compute(HmacKey, 5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(255, 256)]
    [InlineData(65535, 65536)]
    public void AdjacentBlocksGetDifferentIvs(long left, long right)
    {
        Assert.NotEqual(EncryptedBlockIv.Compute(HmacKey, left), EncryptedBlockIv.Compute(HmacKey, right));
    }

    [Fact]
    public void ADifferentHmacKeyGivesADifferentIv()
    {
        byte[] other = [.. HmacKey];
        other[0] ^= 0xFF;

        Assert.NotEqual(EncryptedBlockIv.Compute(HmacKey, 42), EncryptedBlockIv.Compute(other, 42));
    }

    [Fact]
    public void TheCounterIsBigEndian()
    {
        // A little-endian counter would give block 0x01000000 the same IV a
        // big-endian one gives block 1. Asserting the byte order directly is the
        // only way to notice.
        byte[] expected = HMACSHA1.HashData(HmacKey, new byte[] { 0x01, 0x00, 0x00, 0x00 })[..16];

        Assert.Equal(expected, EncryptedBlockIv.Compute(HmacKey, 0x0100_0000));
    }

    [Fact]
    public void ANegativeBlockNumberIsARejectedArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EncryptedBlockIv.Compute(HmacKey, -1));
    }

    [Fact]
    public void ABlockNumberPastTheCountersRangeIsRefusedRatherThanWrapped()
    {
        // Wrapping would hand two blocks the same IV. There is no such image, but
        // there is also no reason for the failure mode to be silent.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EncryptedBlockIv.Compute(HmacKey, (long)uint.MaxValue + 1));
    }

    [Fact]
    public void ADestinationOfTheWrongSizeIsRejected()
    {
        byte[] tooShort = new byte[15];

        Assert.Throws<ArgumentException>(() => EncryptedBlockIv.Compute(HmacKey, 0, tooShort));
    }
}
