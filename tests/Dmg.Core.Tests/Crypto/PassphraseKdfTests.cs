using System.Security.Cryptography;
using System.Text;
using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// The key derivation, and the one number in it a hostile image controls.
/// </summary>
public sealed class PassphraseKdfTests
{
    [Fact]
    public void DerivesTheKeyLengthTheHeaderAsksFor()
    {
        EncryptedDmgKeyBlob blob = KeyBlob(iterations: 1000);

        Result<byte[]> result = PassphraseKdf.Derive("secret"u8, blob);

        Assert.True(result.TryGetValue(out byte[]? derived), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(24, derived.Length);
    }

    [Fact]
    public void MatchesTheBclOneShotForTheSameParameters()
    {
        // The derivation is PBKDF2-HMAC-SHA1 and nothing else - no pre-hashing, no
        // extra rounds, no salt massaging. Pinning it against the BCL primitive says
        // so in a way a future refactor cannot quietly break.
        byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        byte[] passphrase = Encoding.UTF8.GetBytes("dmg-test-passphrase");

        byte[] expected = Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            2048,
            HashAlgorithmName.SHA1,
            24);

        Result<byte[]> result = PassphraseKdf.Derive(passphrase, KeyBlob(2048, salt));

        Assert.True(result.TryGetValue(out byte[]? derived));
        Assert.Equal(expected, derived);
    }

    [Fact]
    public void ADifferentPassphraseDerivesADifferentKey()
    {
        EncryptedDmgKeyBlob blob = KeyBlob(iterations: 1000);

        Assert.True(PassphraseKdf.Derive("secret"u8, blob).TryGetValue(out byte[]? right));
        Assert.True(PassphraseKdf.Derive("secrey"u8, blob).TryGetValue(out byte[]? wrong));

        Assert.NotEqual(right, wrong);
    }

    [Fact]
    public void AnIterationCountAboveTheCapIsRefusedRatherThanAttempted()
    {
        // The whole point: this returns, and it returns quickly. A build that
        // obeyed the header would still be hashing when the test timed out.
        Result<byte[]> result = PassphraseKdf.Derive("secret"u8, KeyBlob(iterations: 2_000_000_000));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains("2,000,000,000", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("10,000,000", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PassphraseKdf.MaxIterationCount + 1u)]
    [InlineData(uint.MaxValue)]
    public void EverythingAboveTheCapIsRejected(uint iterations)
    {
        Assert.False(PassphraseKdf.CheckIterationCount(iterations).Ok);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(500_000u)]
    [InlineData(555_555u)]
    [InlineData(PassphraseKdf.MaxIterationCount)]
    public void TheCapItselfIsAllowed(uint iterations)
    {
        // hdiutil calibrates the count per machine, so the accepted range has to
        // cover both fixtures on this machine and much slower ones elsewhere.
        Assert.True(PassphraseKdf.CheckIterationCount(iterations).Ok);
    }

    [Fact]
    public void ZeroIterationsIsCorruption()
    {
        Result result = PassphraseKdf.CheckIterationCount(0);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnEmptySaltIsRefused()
    {
        Result<byte[]> result = PassphraseKdf.Derive("secret"u8, KeyBlob(1000, []));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnEmptyPassphraseIsStillDerivedFrom()
    {
        // An empty passphrase is a wrong passphrase, not a usage error: it has to
        // travel the same path and fail at the unwrap, so the message is about the
        // passphrase rather than about an argument.
        Result<byte[]> result = PassphraseKdf.Derive([], KeyBlob(iterations: 1000));

        Assert.True(result.Ok);
    }

    private static EncryptedDmgKeyBlob KeyBlob(uint iterations, byte[]? salt = null) => new(
        EncryptedDmgKeyBlob.Pbkdf2Algorithm,
        0,
        iterations,
        salt ?? [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
        new byte[8],
        192,
        EncryptedDmgKeyBlob.AesAlgorithm,
        7,
        6,
        new byte[64]);
}
