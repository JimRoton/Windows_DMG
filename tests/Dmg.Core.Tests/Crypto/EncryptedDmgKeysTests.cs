using System.Security.Cryptography;
using System.Text;
using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// Unwrapping the key blob, and the padding check that turns a wrong passphrase
/// into exit code 4 instead of a cryptography exception.
/// </summary>
public sealed class EncryptedDmgKeysTests
{
    [Fact]
    public void UnwrapsAKeyBlobWrittenByHdiutil()
    {
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Assert.True(EncryptedDmgHeader.Read(file).TryGetValue(out EncryptedDmgHeader? header));

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(
            header,
            Encoding.UTF8.GetBytes(EncryptedFixtures.Passphrase));

        Assert.True(result.TryGetValue(out EncryptedDmgKeys? keys), result.Ok ? "" : result.Error.ToString());

        using (keys)
        {
            Assert.Equal(32, keys.AesKey.Length);
            Assert.Equal(EncryptedDmgKeys.HmacKeyBytes, keys.HmacKey.Length);
        }
    }

    [Fact]
    public void TheAes128FixtureYieldsAShorterAesKeyAndTheSameHmacKeyLength()
    {
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc128.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using EncryptedDmgKeys keys = UnwrapFixture("exfat-enc128.dmg", EncryptedFixtures.Passphrase);

        Assert.Equal(16, keys.AesKey.Length);
        Assert.Equal(20, keys.HmacKey.Length);
    }

    [Fact]
    public void ThePassphraseAsTypedWorksBecauseOfTheTrailingNewlineRetry()
    {
        // make-fixtures.sh pipes the passphrase with printf '%s\n', and hdiutil keys
        // the image off the terminator too - so the passphrase its author typed is
        // one byte shorter than the one the image wants. Without the retry this is
        // an unopenable image.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using EncryptedDmgKeys keys = UnwrapFixture("exfat-enc256.dmg", EncryptedFixtures.TypedPassphrase);

        Assert.Equal(32, keys.AesKey.Length);
    }

    [Fact]
    public void TheRetryDoesNotRescueAWrongPassphrase()
    {
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        Result<EncryptedDmgKeys> result = Attempt("exfat-enc256.dmg", "not-the-passphrase");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
    }

    [Fact]
    public void UnwrapExactlyRefusesThePassphraseWithoutItsTerminator()
    {
        // The retry lives in Unwrap, not in the mechanism underneath it, so the
        // mechanism can be tested for what it actually does.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Assert.True(EncryptedDmgHeader.Read(file).TryGetValue(out EncryptedDmgHeader? header));

        Result<EncryptedDmgKeys> exact = EncryptedDmgKeys.UnwrapExactly(
            header,
            Encoding.UTF8.GetBytes(EncryptedFixtures.TypedPassphrase));

        Assert.False(exact.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, exact.Error.Code);
    }

    [Fact]
    public void AWrongPassphraseIsDecryptionFailedNotCorruptImage()
    {
        // The single most important line in this file. A wrong passphrase reported
        // as a corrupt image sends the user looking for a damaged download.
        SyntheticImage image = SyntheticImage.Create("right-passphrase");

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(
            image.Header,
            "wrong-passphrase"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
        Assert.Contains("passphrase", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRightPassphraseRecoversExactlyTheKeysThatWereWrapped()
    {
        SyntheticImage image = SyntheticImage.Create("right-passphrase");

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(image.Header, "right-passphrase"u8);

        Assert.True(result.TryGetValue(out EncryptedDmgKeys? keys), result.Ok ? "" : result.Error.ToString());

        using (keys)
        {
            Assert.True(keys.AesKey.SequenceEqual(image.AesKey));
            Assert.True(keys.HmacKey.SequenceEqual(image.HmacKey));
        }
    }

    [Fact]
    public void KeysAreZeroedOnDispose()
    {
        SyntheticImage image = SyntheticImage.Create("right-passphrase");

        Assert.True(EncryptedDmgKeys.Unwrap(image.Header, "right-passphrase"u8).TryGetValue(out EncryptedDmgKeys? keys));

        keys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = keys.AesKey.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = keys.HmacKey.Length; });
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        SyntheticImage image = SyntheticImage.Create("right-passphrase");

        Assert.True(EncryptedDmgKeys.Unwrap(image.Header, "right-passphrase"u8).TryGetValue(out EncryptedDmgKeys? keys));

        keys.Dispose();
        keys.Dispose();
    }

    [Fact]
    public void AWrapCipherWeDoNotImplementIsUnsupportedNotADecryptionFailure()
    {
        SyntheticImage image = SyntheticImage.Create("right-passphrase", blobAlgorithm: 14);

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(image.Header, "right-passphrase"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AWrappedKeyThatIsNotAWholeNumberOfBlocksIsCorruption()
    {
        EncryptedHeaderBuilder builder = new()
        {
            WrappedKeyLength = 60,
            WrappedKey = new byte[60],
        };

        Assert.True(EncryptedDmgHeader.Parse(builder.ToArray(), builder.FileLength)
            .TryGetValue(out EncryptedDmgHeader? header));

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(header, "anything"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnUnwrappedBlobTooShortForBothKeysIsADecryptionFailure()
    {
        // Valid padding over a plaintext that cannot hold a 32-byte AES key and a
        // 20-byte HMAC key: the padding check passed and the length check has to
        // catch it.
        SyntheticImage image = SyntheticImage.Create("right-passphrase", keyMaterial: new byte[16]);

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(image.Header, "right-passphrase"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
    }

    [Fact]
    public void ACappedIterationCountIsNotRetriedWithANewline()
    {
        // The retry exists for a wrong passphrase, not for a refusal. A second
        // PBKDF2 pass on a header we already refused would be pure waste.
        EncryptedHeaderBuilder builder = new() { KdfIterationCount = 2_000_000_000 };

        Assert.True(EncryptedDmgHeader.Parse(builder.ToArray(), builder.FileLength)
            .TryGetValue(out EncryptedDmgHeader? header));

        Result<EncryptedDmgKeys> result = EncryptedDmgKeys.Unwrap(header, "anything"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    private static Result<EncryptedDmgKeys> Attempt(string fixture, string passphrase)
    {
        using FileStream file = File.OpenRead(EncryptedFixtures.Path(fixture));
        Assert.True(EncryptedDmgHeader.Read(file).TryGetValue(out EncryptedDmgHeader? header));

        return EncryptedDmgKeys.Unwrap(header, Encoding.UTF8.GetBytes(passphrase));
    }

    private static EncryptedDmgKeys UnwrapFixture(string fixture, string passphrase)
    {
        Result<EncryptedDmgKeys> result = Attempt(fixture, passphrase);
        Assert.True(result.TryGetValue(out EncryptedDmgKeys? keys), result.Ok ? "" : result.Error.ToString());
        return keys;
    }
}

/// <summary>
/// An encrcdsa header this test suite wrapped itself, so the keys inside it are
/// known and the passphrase is whatever the test says it is.
/// </summary>
/// <remarks>
/// Built the way <c>hdiutil</c> builds one - PBKDF2 to a 192-bit key-wrapping key,
/// AES-CBC over the blob with PKCS#7 padding - but with a small iteration count, so
/// a test costs microseconds rather than the half-second a real image's 500 000
/// rounds would.
/// </remarks>
internal sealed record SyntheticImage(
    EncryptedDmgHeader Header,
    byte[] AesKey,
    byte[] HmacKey,
    byte[] File)
{
    /// <summary>Iterations used by every synthetic image. Real ones use half a million.</summary>
    public const uint Iterations = 64;

    /// <summary>Builds an image whose payload is <paramref name="plaintext"/>.</summary>
    public static SyntheticImage Create(
        string passphrase,
        uint keyBits = 256,
        uint blobAlgorithm = EncryptedDmgKeyBlob.AesAlgorithm,
        byte[]? keyMaterial = null,
        ReadOnlySpan<byte> plaintext = default,
        uint blockSize = 512)
    {
        int aesKeyBytes = (int)(keyBits / 8);
        byte[] material = keyMaterial ?? Material(aesKeyBytes);
        byte[] aesKey = material.Length >= aesKeyBytes ? material[..aesKeyBytes] : material;
        byte[] hmacKey = material.Length >= aesKeyBytes + 20
            ? material.AsSpan(aesKeyBytes, 20).ToArray()
            : new byte[20];

        byte[] salt = [.. Enumerable.Range(0, 20).Select(i => (byte)(0x40 + i))];
        byte[] iv = [.. Enumerable.Range(0, 8).Select(i => (byte)(0x90 + i))];

        byte[] kek = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            (int)Iterations,
            HashAlgorithmName.SHA1,
            24);

        byte[] wrapped = Wrap(material, kek, iv, blobAlgorithm);

        byte[] ciphertext = plaintext.IsEmpty
            ? []
            : EncryptPayload(plaintext, aesKey, hmacKey, (int)blockSize);

        EncryptedHeaderBuilder builder = new()
        {
            EncryptionKeyBits = keyBits,
            BlockSize = blockSize,
            KdfIterationCount = Iterations,
            Salt = salt,
            Iv = iv,
            BlobAlgorithm = blobAlgorithm,
            WrappedKey = wrapped,
            WrappedKeyLength = (uint)wrapped.Length,
            DataSize = (ulong)plaintext.Length,
        };

        byte[] file = builder.ToFile(ciphertext);

        Result<EncryptedDmgHeader> parsed = EncryptedDmgHeader.Parse(file, file.Length);
        Assert.True(parsed.TryGetValue(out EncryptedDmgHeader? header), parsed.Ok ? "" : parsed.Error.ToString());

        return new SyntheticImage(header, aesKey, hmacKey, file);
    }

    /// <summary>The whole file as a stream a reader can be pointed at.</summary>
    public MemoryStream OpenRead() => new(File, writable: false);

    /// <summary>
    /// Encrypts a payload exactly the way an encrcdsa image does: one CBC run per
    /// block, each with an IV that is a pure function of the block index.
    /// </summary>
    internal static byte[] EncryptPayload(
        ReadOnlySpan<byte> plaintext,
        byte[] aesKey,
        byte[] hmacKey,
        int blockSize)
    {
        int blocks = (plaintext.Length + blockSize - 1) / blockSize;
        byte[] ciphertext = new byte[blocks * blockSize];

        using Aes aes = Aes.Create();
        aes.Key = aesKey;

        for (int index = 0; index < blocks; index++)
        {
            int offset = index * blockSize;
            int take = Math.Min(blockSize, plaintext.Length - offset);

            // The last block is padded with zeros to a whole block; DataSize is what
            // tells a reader how much of it is real.
            byte[] block = new byte[blockSize];
            plaintext.Slice(offset, take).CopyTo(block);

            byte[] iv = BlockIv(hmacKey, index);
            aes.EncryptCbc(block, iv, PaddingMode.None).CopyTo(ciphertext, offset);
        }

        return ciphertext;
    }

    /// <summary>The per-block IV, computed independently of the production code.</summary>
    internal static byte[] BlockIv(byte[] hmacKey, int index)
    {
        byte[] counter =
        [
            (byte)(index >> 24),
            (byte)(index >> 16),
            (byte)(index >> 8),
            (byte)index,
        ];

        return HMACSHA1.HashData(hmacKey, counter)[..16];
    }

    private static byte[] Material(int aesKeyBytes)
    {
        // aesKey || hmacKey || "CKIE" || 0x00, the shape hdiutil writes.
        byte[] material = new byte[aesKeyBytes + 20 + 5];

        for (int index = 0; index < aesKeyBytes + 20; index++)
        {
            material[index] = (byte)(index * 7 + 3);
        }

        "CKIE"u8.CopyTo(material.AsSpan(aesKeyBytes + 20));

        return material;
    }

    private static byte[] Wrap(byte[] material, byte[] kek, byte[] iv, uint algorithm)
    {
        using SymmetricAlgorithm cipher = algorithm == EncryptedDmgKeyBlob.TripleDesAlgorithm
            ? TripleDES.Create()
            : Aes.Create();

        cipher.Key = kek;

        byte[] fullIv = new byte[cipher.BlockSize / 8];
        iv.AsSpan(0, Math.Min(iv.Length, fullIv.Length)).CopyTo(fullIv);

        return cipher.EncryptCbc(material, fullIv, PaddingMode.PKCS7);
    }
}
