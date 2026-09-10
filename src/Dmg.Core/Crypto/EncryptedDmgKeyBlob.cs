using Dmg.Core.Containers;

namespace Dmg.Core.Crypto;

/// <summary>
/// One passphrase-wrapped key entry from an <c>encrcdsa</c> v2 header: the PBKDF2
/// parameters that turn a passphrase into a key-wrapping key, and the wrapped blob
/// that key opens.
/// </summary>
/// <remarks>
/// <para>
/// Three of these fields are variable-length data inside a fixed-size container -
/// the salt in 32 bytes, the wrap IV in 32, the wrapped blob in whatever is left of
/// the descriptor - and each is preceded by its own declared length. Those three
/// lengths are the parser's whole attack surface, so <see cref="Parse"/> checks
/// each against its container before slicing and refuses rather than clamping. A
/// clamp would keep going with a truncated salt and produce a wrong key, which
/// surfaces as "wrong passphrase" and sends the user chasing their own typing.
/// </para>
/// <para>
/// The CDSA algorithm ids are recorded as written rather than mapped to an enum.
/// They are Apple's vendor-defined constants and the only two this build acts on
/// are <see cref="AesAlgorithm"/> and <see cref="TripleDesAlgorithm"/>; the rest
/// are carried so a refusal can quote them.
/// </para>
/// </remarks>
/// <param name="KdfAlgorithm">CDSA algorithm id of the key-derivation function. 103 is PBKDF2.</param>
/// <param name="KdfPrngAlgorithm">CDSA algorithm id of the PBKDF2 PRF. Zero in current images.</param>
/// <param name="KdfIterationCount">PBKDF2 iterations, as declared. Attacker-controlled; see <see cref="PassphraseKdf"/>.</param>
/// <param name="KdfSalt">The PBKDF2 salt, already trimmed to its declared length.</param>
/// <param name="BlobEncryptionIv">The key-unwrap CBC IV, already trimmed to its declared length.</param>
/// <param name="BlobEncryptionKeyBits">Size of the key-wrapping key in bits. 192 in current images.</param>
/// <param name="BlobEncryptionAlgorithm">CDSA algorithm id of the key-wrapping cipher.</param>
/// <param name="BlobEncryptionPadding">CDSA padding id. 7 is PKCS#7.</param>
/// <param name="BlobEncryptionMode">CDSA cipher mode id. 6 is CBC with padding and an eight-byte IV.</param>
/// <param name="WrappedKey">The wrapped key blob, already trimmed to its declared length.</param>
public sealed record EncryptedDmgKeyBlob(
    uint KdfAlgorithm,
    uint KdfPrngAlgorithm,
    uint KdfIterationCount,
    ReadOnlyMemory<byte> KdfSalt,
    ReadOnlyMemory<byte> BlobEncryptionIv,
    uint BlobEncryptionKeyBits,
    uint BlobEncryptionAlgorithm,
    uint BlobEncryptionPadding,
    uint BlobEncryptionMode,
    ReadOnlyMemory<byte> WrappedKey)
{
    /// <summary>Bytes of descriptor before the wrapped blob itself.</summary>
    public const int FixedSize = 0x68;

    /// <summary>The fixed container the salt is stored in.</summary>
    public const int SaltContainerSize = 32;

    /// <summary>The fixed container the unwrap IV is stored in.</summary>
    public const int IvContainerSize = 32;

    /// <summary>
    /// The most wrapped-key bytes accepted. The blob holds an AES key and a 20-byte
    /// HMAC key with a little padding; anything near this ceiling is already absurd.
    /// </summary>
    public const int MaxWrappedKeySize = 1024;

    /// <summary>CDSA <c>CSSM_ALGID_PKCS5_PBKDF2</c>.</summary>
    public const uint Pbkdf2Algorithm = 103;

    /// <summary>
    /// Apple's vendor-defined AES id, <c>CSSM_ALGID_VENDOR_DEFINED + 1</c>. This is
    /// what current <c>hdiutil</c> wraps keys with.
    /// </summary>
    public const uint AesAlgorithm = 0x8000_0001;

    /// <summary>CDSA <c>CSSM_ALGID_3DES_3KEY_EDE</c>, used by older images.</summary>
    public const uint TripleDesAlgorithm = 17;

    private const int KdfAlgorithmOffset = 0x00;
    private const int KdfPrngAlgorithmOffset = 0x04;
    private const int KdfIterationCountOffset = 0x08;
    private const int KdfSaltLengthOffset = 0x0C;
    private const int KdfSaltOffset = 0x10;
    private const int IvLengthOffset = 0x30;
    private const int IvOffset = 0x34;
    private const int BlobKeyBitsOffset = 0x54;
    private const int BlobAlgorithmOffset = 0x58;
    private const int BlobPaddingOffset = 0x5C;
    private const int BlobModeOffset = 0x60;
    private const int WrappedKeyLengthOffset = 0x64;
    private const int WrappedKeyOffset = 0x68;

    /// <summary>The key-wrapping key size in bytes.</summary>
    public int BlobEncryptionKeyBytes => (int)(BlobEncryptionKeyBits / 8);

    /// <summary>
    /// Parses one key descriptor.
    /// </summary>
    /// <param name="descriptor">
    /// The descriptor bytes the header's key-pointer table pointed at.
    /// </param>
    public static Result<EncryptedDmgKeyBlob> Parse(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < FixedSize)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image's key description is truncated.",
                $"Got {descriptor.Length} bytes, need at least {FixedSize}.");
        }

        uint kdfAlgorithm = ReadUInt32(descriptor, KdfAlgorithmOffset);

        if (kdfAlgorithm != Pbkdf2Algorithm)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This encrypted image derives its key with a function this build does not "
                + "implement.",
                $"KdfAlgorithm={kdfAlgorithm}; only PBKDF2 ({Pbkdf2Algorithm}) is supported.");
        }

        uint saltLength = ReadUInt32(descriptor, KdfSaltLengthOffset);

        if (saltLength == 0 || saltLength > SaltContainerSize)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a salt that does not fit the field holding it.",
                $"KdfSaltLen={saltLength}, container is {SaltContainerSize} bytes.");
        }

        uint ivLength = ReadUInt32(descriptor, IvLengthOffset);

        if (ivLength == 0 || ivLength > IvContainerSize)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a key-unwrap IV that does not fit the field "
                + "holding it.",
                $"BlobEncIvSize={ivLength}, container is {IvContainerSize} bytes.");
        }

        uint wrappedLength = ReadUInt32(descriptor, WrappedKeyLengthOffset);
        long available = descriptor.Length - WrappedKeyOffset;

        if (wrappedLength == 0 || wrappedLength > available || wrappedLength > MaxWrappedKeySize)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a wrapped key that does not fit the field "
                + "holding it.",
                $"EncryptedKeyblobSize={wrappedLength}, {available} bytes remain in the "
                + $"{descriptor.Length}-byte key description, ceiling is {MaxWrappedKeySize}.");
        }

        uint blobKeyBits = ReadUInt32(descriptor, BlobKeyBitsOffset);

        if (blobKeyBits is not (128 or 192 or 256))
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"This encrypted image wraps its key with a {blobKeyBits}-bit key, which this "
                + "build does not recognise.",
                "BlobEncKeyBits");
        }

        return Result<EncryptedDmgKeyBlob>.Success(new EncryptedDmgKeyBlob(
            kdfAlgorithm,
            ReadUInt32(descriptor, KdfPrngAlgorithmOffset),
            ReadUInt32(descriptor, KdfIterationCountOffset),
            descriptor.Slice(KdfSaltOffset, (int)saltLength).ToArray(),
            descriptor.Slice(IvOffset, (int)ivLength).ToArray(),
            blobKeyBits,
            ReadUInt32(descriptor, BlobAlgorithmOffset),
            ReadUInt32(descriptor, BlobPaddingOffset),
            ReadUInt32(descriptor, BlobModeOffset),
            descriptor.Slice(WrappedKeyOffset, (int)wrappedLength).ToArray()));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset) =>
        BigEndian.TryReadUInt32(span, offset, out uint value) ? value : 0;
}
