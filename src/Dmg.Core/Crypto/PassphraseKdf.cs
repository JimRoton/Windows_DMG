using System.Security.Cryptography;

namespace Dmg.Core.Crypto;

/// <summary>
/// Turns a passphrase into the key that unwraps an image's key blob:
/// PBKDF2-HMAC-SHA1 over the salt and iteration count the header declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>The iteration count is attacker-controlled and therefore capped.</b> It is
/// four bytes read straight out of a file, and PBKDF2 does exactly as much work as
/// it says. A file claiming two billion iterations is not an expensive image, it is
/// a denial of service that costs the attacker nothing to write: the process sits
/// in a tight HMAC loop for hours with no output and no way to tell it apart from a
/// hang. <see cref="MaxIterationCount"/> is the ceiling, and anything above it is
/// refused with a message naming both numbers rather than attempted.
/// </para>
/// <para>
/// The cap is set well clear of anything Apple writes. The fixtures on this machine
/// carry 500 000 and 555 555 - <c>hdiutil</c> calibrates the count against the
/// machine that creates the image - so ten million leaves two orders of magnitude of
/// headroom for faster hardware while still bounding the work at a few seconds.
/// </para>
/// <para>
/// <b>Nothing here logs, formats or returns the passphrase.</b> It arrives as bytes
/// the caller owns and zeroes; this class reads them and produces a derived key the
/// caller is likewise expected to zero. The derived key is a <c>byte[]</c> rather
/// than an opaque type precisely so it can be handed to
/// <see cref="CryptographicOperations.ZeroMemory"/>.
/// </para>
/// </remarks>
public static class PassphraseKdf
{
    /// <summary>
    /// The most PBKDF2 iterations this build will perform. Above this an image is
    /// refused rather than obeyed.
    /// </summary>
    public const int MaxIterationCount = 10_000_000;

    /// <summary>The PRF Apple's PBKDF2 uses. Not configurable in the format.</summary>
    public static HashAlgorithmName PseudoRandomFunction => HashAlgorithmName.SHA1;

    /// <summary>
    /// Derives the key-wrapping key for <paramref name="keyBlob"/> from
    /// <paramref name="passphrase"/>.
    /// </summary>
    /// <param name="passphrase">The passphrase bytes, exactly as typed. Not copied, not retained.</param>
    /// <param name="keyBlob">The key entry whose salt, iteration count and key size to use.</param>
    /// <returns>
    /// <c>BlobEncKeyBits / 8</c> bytes of derived key, which the caller must zero.
    /// </returns>
    public static Result<byte[]> Derive(ReadOnlySpan<byte> passphrase, EncryptedDmgKeyBlob keyBlob)
    {
        ArgumentNullException.ThrowIfNull(keyBlob);

        Result checkedCount = CheckIterationCount(keyBlob.KdfIterationCount);

        if (!checkedCount.Ok)
        {
            return checkedCount.CastFailure<byte[]>();
        }

        if (keyBlob.KdfSalt.Length == 0)
        {
            return Result<byte[]>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image carries no salt, so no key can be derived from a passphrase.",
                "KdfSaltLen = 0.");
        }

        int keyBytes = keyBlob.BlobEncryptionKeyBytes;

        if (keyBytes <= 0)
        {
            return Result<byte[]>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a zero-length key-wrapping key.",
                $"BlobEncKeyBits = {keyBlob.BlobEncryptionKeyBits}.");
        }

        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            keyBlob.KdfSalt.Span,
            (int)keyBlob.KdfIterationCount,
            PseudoRandomFunction,
            keyBytes);

        return Result<byte[]>.Success(derived);
    }

    /// <summary>
    /// The cap check on its own, so a header can be rejected before any work starts
    /// and so the rejection can be tested without spending a real derivation.
    /// </summary>
    /// <param name="iterations">The count the image declares.</param>
    public static Result CheckIterationCount(uint iterations)
    {
        if (iterations == 0)
        {
            return Result.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares zero key-derivation iterations.",
                "KdfIterationCount = 0; PBKDF2 requires at least one.");
        }

        if (iterations > MaxIterationCount)
        {
            // Deliberately not "try it anyway with a progress bar". The only honest
            // answers are "refuse" and "hang", and one of them is a bug report.
            return Result.Failure(
                DmgExitCode.UnsupportedFormat,
                $"This encrypted image asks for {iterations:N0} key-derivation rounds, more than "
                + $"the {MaxIterationCount:N0} this build will perform. An image that demands "
                + "this much work is either damaged or hostile.",
                "encrcdsa.KdfIterationCount");
        }

        return Result.Success();
    }
}
