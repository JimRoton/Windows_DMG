using System.Text;
using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// A passphrase held as bytes, zeroed on every exit path - S4.6's central
/// guarantee. These tests hold <see cref="Passphrase"/> to the letter of that: the
/// bytes it was built from are gone after <see cref="Passphrase.Dispose"/>, not just
/// inaccessible through the type.
/// </summary>
public sealed class PassphraseTests
{
    [Fact]
    public void AdoptTakesOwnershipOfTheArrayItIsGiven()
    {
        byte[] bytes = [1, 2, 3, 4];
        using Passphrase passphrase = Passphrase.Adopt(bytes);

        Assert.Equal(4, passphrase.Length);
        Assert.True(passphrase.Bytes.SequenceEqual(bytes));
    }

    [Fact]
    public void DisposeZeroesTheAdoptedArrayInPlace()
    {
        // The whole point of Adopt: the caller's own array is the one that gets
        // scrubbed, not a private copy the caller has no way to verify.
        byte[] bytes = [0xAA, 0xBB, 0xCC, 0xDD];
        Passphrase passphrase = Passphrase.Adopt(bytes);

        passphrase.Dispose();

        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CopyFromDoesNotZeroTheSourceOnDispose()
    {
        // CopyFrom's contract is the opposite of Adopt's: the source span is not
        // owned, so disposing the passphrase must never reach back into it.
        byte[] source = [0x11, 0x22, 0x33];
        using Passphrase passphrase = Passphrase.CopyFrom(source);

        passphrase.Dispose();

        Assert.Equal([0x11, 0x22, 0x33], source);
    }

    [Fact]
    public void CopyFromReadsBackTheSameBytesBeforeDisposal()
    {
        byte[] source = [9, 8, 7];
        using Passphrase passphrase = Passphrase.CopyFrom(source);

        Assert.True(passphrase.Bytes.SequenceEqual(source));
    }

    [Fact]
    public void FromStringEncodesAsUtf8()
    {
        using Passphrase passphrase = Passphrase.FromString("open sesame");

        Assert.True(passphrase.Bytes.SequenceEqual(Encoding.UTF8.GetBytes("open sesame")));
    }

    [Fact]
    public void FromStringHandlesNonAsciiText()
    {
        // UTF-8, not the platform's default encoding - a passphrase with an accent
        // or an emoji must round-trip byte for byte.
        const string text = "correct-horse-é🔑";
        using Passphrase passphrase = Passphrase.FromString(text);

        Assert.True(passphrase.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void BytesThrowsAfterDisposal()
    {
        Passphrase passphrase = Passphrase.Adopt([1, 2, 3]);
        passphrase.Dispose();

        Assert.Throws<ObjectDisposedException>(() => passphrase.Bytes.Length);
    }

    [Fact]
    public void LengthIsZeroAfterDisposalRatherThanThrowing()
    {
        // Length is documented to survive disposal - callers that just want to know
        // "is there anything here" should not need a try/catch.
        Passphrase passphrase = Passphrase.Adopt([1, 2, 3]);
        passphrase.Dispose();

        Assert.Equal(0, passphrase.Length);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        Passphrase passphrase = Passphrase.Adopt([1, 2, 3]);

        passphrase.Dispose();
        passphrase.Dispose();

        Assert.Equal(0, passphrase.Length);
    }

    [Fact]
    public void AnEmptyPassphraseIsAllowed()
    {
        // Empty is a legitimate, if useless, passphrase - it is not this type's job
        // to second-guess what was typed or piped in.
        using Passphrase passphrase = Passphrase.Adopt([]);

        Assert.Equal(0, passphrase.Length);
        Assert.True(passphrase.Bytes.IsEmpty);
    }

    [Fact]
    public void AdoptRejectsANullArray() =>
        Assert.Throws<ArgumentNullException>(() => Passphrase.Adopt(null!));

    [Fact]
    public void FromStringRejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => Passphrase.FromString(null!));
}
