using System.Security.Cryptography;

namespace Dmg.Core.Crypto;

/// <summary>
/// The two keys an encrypted image is actually read with: the AES key for the
/// payload blocks and the HMAC-SHA1 key that generates each block's IV.
/// </summary>
/// <remarks>
/// <para>
/// Both come out of one wrapped blob in the header, unwrapped with a key derived
/// from the passphrase. The blob's plaintext is
/// <c>[aesKey][hmacKey (20 bytes)][a four-byte marker][0x00]</c> followed by PKCS#7
/// padding - 52 bytes of key material padded to 64 for AES-256, 36 padded to 48 for
/// AES-128. Those two sizes are the reason the wrapping cipher must have a 16-byte
/// block: 52 bytes PKCS#7-padded to 64 is impossible with 3DES, which would land on
/// 56.
/// </para>
/// <para>
/// <b>The instance owns its key bytes and zeroes them on <see cref="Dispose"/>.</b>
/// The keys are exposed as <see cref="ReadOnlySpan{T}"/> rather than arrays so that
/// no caller ends up holding a reference that outlives the disposal, and so that
/// nothing can hand them to a logger or a string.
/// </para>
/// <para>
/// <b>Wrong passphrases are caught here first.</b> A key derived from the wrong
/// passphrase decrypts the blob to noise, and noise ends in valid PKCS#7 padding
/// about one time in 255. That single check rejects almost every wrong passphrase
/// for the cost of one block, and the length check behind it - the unpadded
/// plaintext must be long enough to hold both keys - takes the residue down to
/// nothing worth worrying about. It is still not proof, which is why the
/// decrypted payload is checked as well when the stream is opened; but it is what
/// makes the common case fast and the message specific.
/// </para>
/// </remarks>
public sealed class EncryptedDmgKeys : IDisposable
{
    /// <summary>The HMAC-SHA1 key inside the blob is always 20 bytes.</summary>
    public const int HmacKeyBytes = 20;

    private readonly byte[] _aesKey;
    private readonly byte[] _hmacKey;
    private bool _disposed;

    private EncryptedDmgKeys(byte[] aesKey, byte[] hmacKey)
    {
        _aesKey = aesKey;
        _hmacKey = hmacKey;
    }

    /// <summary>The payload AES key, 16 or 32 bytes.</summary>
    public ReadOnlySpan<byte> AesKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _aesKey;
        }
    }

    /// <summary>The 20-byte HMAC-SHA1 key the per-block IVs come from.</summary>
    public ReadOnlySpan<byte> HmacKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _hmacKey;
        }
    }

    /// <summary>
    /// Unwraps the key blob in <paramref name="header"/> using
    /// <paramref name="passphrase"/>.
    /// </summary>
    /// <param name="header">A parsed encrcdsa v2 header.</param>
    /// <param name="passphrase">
    /// The passphrase bytes. Not retained; the caller still owns them and should
    /// zero them.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The trailing-newline retry.</b> If the passphrase as given does not unwrap
    /// the blob, it is tried once more with a single <c>\n</c> appended. This is not
    /// guesswork: <c>hdiutil -stdinpass</c> keys the image off everything it read
    /// from standard input, terminator included, so an image created by a script
    /// that pipes <c>printf '%s\n'</c> - which is every scripted image, and every
    /// fixture in this repository - has a passphrase one byte longer than the one
    /// its author typed. Without the retry those images are unopenable with the
    /// passphrase their author believes they set. The retry costs one extra PBKDF2
    /// pass and only on the path that was about to fail anyway.
    /// </para>
    /// </remarks>
    public static Result<EncryptedDmgKeys> Unwrap(
        EncryptedDmgHeader header,
        ReadOnlySpan<byte> passphrase)
    {
        ArgumentNullException.ThrowIfNull(header);

        Result<EncryptedDmgKeys> first = UnwrapExactly(header, passphrase);

        if (first.Ok)
        {
            return first;
        }

        // Only a failed unwrap is worth retrying. A capped iteration count or an
        // unsupported wrap cipher would fail identically the second time.
        if (first.Error.Code != DmgExitCode.DecryptionFailed)
        {
            return first;
        }

        byte[] terminated = new byte[passphrase.Length + 1];

        try
        {
            passphrase.CopyTo(terminated);
            terminated[^1] = (byte)'\n';

            Result<EncryptedDmgKeys> second = UnwrapExactly(header, terminated);

            return second.Ok ? second : first;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(terminated);
        }
    }

    /// <summary>
    /// One unwrap attempt with the passphrase exactly as given, and no retry.
    /// </summary>
    /// <param name="header">A parsed encrcdsa v2 header.</param>
    /// <param name="passphrase">The passphrase bytes to try.</param>
    public static Result<EncryptedDmgKeys> UnwrapExactly(
        EncryptedDmgHeader header,
        ReadOnlySpan<byte> passphrase)
    {
        ArgumentNullException.ThrowIfNull(header);

        Result<byte[]> derivation = PassphraseKdf.Derive(passphrase, header.KeyBlob);

        if (!derivation.TryGetValue(out byte[]? kek))
        {
            return derivation.CastFailure<EncryptedDmgKeys>();
        }

        byte[]? plaintext = null;

        try
        {
            Result<byte[]> decrypted = DecryptBlob(header.KeyBlob, kek);

            if (!decrypted.TryGetValue(out plaintext))
            {
                return decrypted.CastFailure<EncryptedDmgKeys>();
            }

            return Split(header, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_aesKey);
        CryptographicOperations.ZeroMemory(_hmacKey);
        _disposed = true;
    }

    /// <summary>
    /// Decrypts the wrapped blob with the derived key, leaving the PKCS#7 padding in
    /// place for <see cref="Split"/> to check.
    /// </summary>
    private static Result<byte[]> DecryptBlob(EncryptedDmgKeyBlob keyBlob, byte[] kek)
    {
        Result<SymmetricAlgorithm> created = CreateWrapCipher(keyBlob);

        if (!created.TryGetValue(out SymmetricAlgorithm? cipher))
        {
            return created.CastFailure<byte[]>();
        }

        using (cipher)
        {
            int blockBytes = cipher.BlockSize / 8;

            if (keyBlob.WrappedKey.Length == 0 || keyBlob.WrappedKey.Length % blockBytes != 0)
            {
                return Result<byte[]>.Failure(
                    DmgExitCode.CorruptImage,
                    "The encrypted image's wrapped key is not a whole number of cipher blocks.",
                    $"{keyBlob.WrappedKey.Length} bytes with a {blockBytes}-byte block.");
            }

            if (kek.Length * 8 != keyBlob.BlobEncryptionKeyBits)
            {
                return Result<byte[]>.Failure(DmgError.Internal(
                    "The derived key is not the size the header asked for.",
                    $"Derived {kek.Length * 8} bits, header says {keyBlob.BlobEncryptionKeyBits}."));
            }

            // The header declares an eight-byte IV even for a 16-byte-block cipher.
            // Apple zero-extends it, and a wrong IV would corrupt only the first
            // block anyway - which is where the AES key lives, so it is not
            // something that could pass unnoticed.
            byte[] iv = new byte[blockBytes];
            int copy = Math.Min(blockBytes, keyBlob.BlobEncryptionIv.Length);
            keyBlob.BlobEncryptionIv.Span[..copy].CopyTo(iv);

            try
            {
                cipher.Key = kek;

                return Result<byte[]>.Success(
                    cipher.DecryptCbc(keyBlob.WrappedKey.Span, iv, PaddingMode.None));
            }
            catch (CryptographicException exception)
            {
                return Result<byte[]>.Failure(
                    DmgExitCode.DecryptionFailed,
                    "The encrypted image's key material could not be unwrapped.",
                    exception.Message);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(iv);
            }
        }
    }

    /// <summary>
    /// Builds the key-wrapping cipher the header names.
    /// </summary>
    /// <remarks>
    /// Current <c>hdiutil</c> writes Apple's vendor-defined AES id here. Images from
    /// the 10.5 era name 3DES instead, which is supported for their sake and is the
    /// one algorithm in this codebase a machine policy can refuse to provide.
    /// </remarks>
    private static Result<SymmetricAlgorithm> CreateWrapCipher(EncryptedDmgKeyBlob keyBlob)
    {
        switch (keyBlob.BlobEncryptionAlgorithm)
        {
            case EncryptedDmgKeyBlob.AesAlgorithm:
                Aes aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                return Result<SymmetricAlgorithm>.Success(aes);

            case EncryptedDmgKeyBlob.TripleDesAlgorithm:
                return CreateTripleDes();

            default:
                return Result<SymmetricAlgorithm>.Failure(
                    DmgExitCode.UnsupportedFormat,
                    "This encrypted image wraps its key with a cipher this build does not "
                    + "implement.",
                    $"BlobEncAlgorithm=0x{keyBlob.BlobEncryptionAlgorithm:X8}; this build reads "
                    + $"AES (0x{EncryptedDmgKeyBlob.AesAlgorithm:X8}) and 3DES "
                    + $"({EncryptedDmgKeyBlob.TripleDesAlgorithm}).");
        }
    }

    /// <summary>
    /// Triple-DES, or a policy failure said in those words.
    /// </summary>
    /// <remarks>
    /// A machine in FIPS mode refuses to hand out 3DES, because it is not approved
    /// for new use. That is a configuration answer, not a broken image and not a
    /// wrong passphrase, and reporting it as either sends the user looking in
    /// entirely the wrong place. There is no workaround short of implementing 3DES
    /// by hand, which this project will not do.
    /// </remarks>
    private static Result<SymmetricAlgorithm> CreateTripleDes()
    {
        try
        {
            TripleDES tripleDes = TripleDES.Create();
            tripleDes.Mode = CipherMode.CBC;
            tripleDes.Padding = PaddingMode.None;
            return Result<SymmetricAlgorithm>.Success(tripleDes);
        }
        catch (Exception exception) when (
            exception is CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            return Result<SymmetricAlgorithm>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This machine's cryptography policy disallows Triple-DES, which this image's "
                + "key wrapping requires. The image is fine; the policy is the obstacle. Open "
                + "it on a machine that is not in FIPS mode.",
                exception.Message);
        }
    }

    /// <summary>
    /// Checks the PKCS#7 padding and cuts the two keys out of the plaintext.
    /// </summary>
    private static Result<EncryptedDmgKeys> Split(EncryptedDmgHeader header, byte[] plaintext)
    {
        int aesKeyBytes = header.EncryptionKeyBytes;
        int blockBytes = plaintext.Length;

        Result<int> unpadded = Unpad(plaintext);

        if (!unpadded.TryGetValue(out int length))
        {
            return unpadded.CastFailure<EncryptedDmgKeys>();
        }

        if (length < aesKeyBytes + HmacKeyBytes)
        {
            return Result<EncryptedDmgKeys>.Failure(
                DmgExitCode.DecryptionFailed,
                "The passphrase did not unlock this image.",
                $"The unwrapped key material is {length} bytes, too short for a "
                + $"{aesKeyBytes}-byte AES key and a {HmacKeyBytes}-byte HMAC key "
                + $"(blob is {blockBytes} bytes).");
        }

        byte[] aesKey = plaintext[..aesKeyBytes];
        byte[] hmacKey = plaintext.AsSpan(aesKeyBytes, HmacKeyBytes).ToArray();

        return Result<EncryptedDmgKeys>.Success(new EncryptedDmgKeys(aesKey, hmacKey));
    }

    /// <summary>
    /// Validates PKCS#7 padding and returns the length without it.
    /// </summary>
    /// <remarks>
    /// Done by hand rather than by asking the cipher for <see cref="PaddingMode.PKCS7"/>
    /// because the failure has to come back as a <see cref="Result{T}"/> carrying
    /// exit code 4 with a sentence about the passphrase. Left to the BCL it arrives
    /// as a <see cref="CryptographicException"/> reading "padding is invalid and
    /// cannot be removed", which is true, unhelpful, and the single most common
    /// thing a user of this tool will ever see.
    /// </remarks>
    private static Result<int> Unpad(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty)
        {
            return Result<int>.Failure(
                DmgExitCode.DecryptionFailed,
                "The passphrase did not unlock this image.",
                "The unwrapped key material was empty.");
        }

        int pad = plaintext[^1];

        if (pad < 1 || pad > plaintext.Length)
        {
            return WrongPassphrase(pad, plaintext.Length);
        }

        foreach (byte b in plaintext[^pad..])
        {
            if (b != pad)
            {
                return WrongPassphrase(pad, plaintext.Length);
            }
        }

        return Result<int>.Success(plaintext.Length - pad);
    }

    private static Result<int> WrongPassphrase(int pad, int length) =>
        Result<int>.Failure(
            DmgExitCode.DecryptionFailed,
            "The passphrase did not unlock this image.",
            $"The unwrapped key material does not end in valid PKCS#7 padding "
            + $"(trailing byte {pad}, blob {length} bytes), which means the key it was "
            + "unwrapped with was wrong.");
}
