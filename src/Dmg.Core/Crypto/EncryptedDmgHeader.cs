using Dmg.Core.Containers;

namespace Dmg.Core.Crypto;

/// <summary>
/// The plaintext <c>encrcdsa</c> version 2 header that sits at the front of an
/// encrypted disk image, and the wrapped key material it carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything else is inside the ciphertext.</b> An <c>encrcdsa</c> v2 file is
/// this header, then <see cref="DataSize"/> bytes of AES-CBC ciphertext beginning
/// at <see cref="DataOffset"/>. That ciphertext decrypts to an ordinary, complete
/// disk image - koly trailer, property list, data fork and all - which is why
/// decryption is a decorator over the byte source and not a mode inside the
/// container parser. See <c>docs/04-encrypted-dmg-reference.md</c>.
/// </para>
/// <para>
/// <b>Every length in here is attacker-controlled.</b> The salt, the key-wrap IV
/// and the wrapped key blob are all variable-length fields stored inside
/// fixed-size containers, and the header declares how much of each container is
/// significant. A declared length larger than its container is the classic way to
/// walk a parser off the end of a buffer, so each one is checked against its
/// container before anything is sliced. <see cref="DataOffset"/> and
/// <see cref="DataSize"/> get the same treatment against the real file length.
/// </para>
/// <para>
/// <b>The offsets here were read off real images, not off the document.</b>
/// <c>docs/04</c> was written from notes and had the field layout shifted; the
/// fixtures produced by <c>tools/make-fixtures.sh</c> disagreed with it and the
/// fixtures won. The document has been corrected to match this file.
/// </para>
/// </remarks>
/// <param name="Version">Header version. Only 2 is supported.</param>
/// <param name="EncryptionIvSize">Size in bytes of the per-block cipher IV; 16 for AES.</param>
/// <param name="EncryptionMode">CDSA cipher mode for the payload. 5 is CBC-with-IV8.</param>
/// <param name="EncryptionAlgorithm">CDSA algorithm id for the payload cipher. <c>0x80000001</c> is Apple's AES.</param>
/// <param name="EncryptionKeyBits">Payload AES key size in bits. 128 or 256; nothing else is accepted.</param>
/// <param name="PrngAlgorithm">CDSA algorithm id of the PRNG that generated the keys. Informational.</param>
/// <param name="PrngKeySize">PRNG key size in bits. Informational.</param>
/// <param name="Uuid">The image's 16-byte UUID, as written.</param>
/// <param name="BlockSize">
/// The size of one independently-encrypted block, in bytes. 512 in images written
/// by current <c>hdiutil</c>; the field is authoritative, not the constant.
/// </param>
/// <param name="DataSize">The plaintext length in bytes: what the decrypted stream reports as its length.</param>
/// <param name="DataOffset">Where the ciphertext begins in the file.</param>
/// <param name="KeyCount">How many wrapped-key entries the header carries.</param>
/// <param name="KeyBlob">The passphrase-wrapped key entry this build knows how to unwrap.</param>
public sealed record EncryptedDmgHeader(
    uint Version,
    uint EncryptionIvSize,
    uint EncryptionMode,
    uint EncryptionAlgorithm,
    uint EncryptionKeyBits,
    uint PrngAlgorithm,
    uint PrngKeySize,
    ReadOnlyMemory<byte> Uuid,
    uint BlockSize,
    long DataSize,
    long DataOffset,
    uint KeyCount,
    EncryptedDmgKeyBlob KeyBlob)
{
    /// <summary>The eight-byte signature at offset zero.</summary>
    public static ReadOnlySpan<byte> Magic => "encrcdsa"u8;

    /// <summary>
    /// The legacy version 1 signature, which sits at the <em>end</em> of the file.
    /// Recognised so it can be refused by name; never parsed.
    /// </summary>
    public static ReadOnlySpan<byte> LegacyMagic => "cdsaencr"u8;

    /// <summary>The only header version this build reads.</summary>
    public const uint SupportedVersion = 2;

    /// <summary>Bytes of fixed header before the variable-length key entries.</summary>
    public const int FixedHeaderSize = 0x4C;

    /// <summary>Bytes in one entry of the key-pointer table.</summary>
    public const int KeyPointerSize = 20;

    /// <summary>The key-pointer type that means "wrapped with a passphrase-derived key".</summary>
    public const uint PassphraseKeyType = 1;

    /// <summary>
    /// The most key entries a header may declare. A file can claim four billion;
    /// real images carry one, and the table is read before anything validates it.
    /// </summary>
    public const uint MaxKeyCount = 64;

    /// <summary>The largest block size accepted, as a sanity bound on an eight-byte-driven read buffer.</summary>
    public const uint MaxBlockSize = 1 << 20;

    // Fixed header. Verified against hdiutil-written fixtures on macOS 26; see the
    // class remarks for why the document is not the authority here.
    private const int SignatureOffset = 0x00;
    private const int VersionOffset = 0x08;
    private const int EncryptionIvSizeOffset = 0x0C;
    private const int EncryptionModeOffset = 0x10;
    private const int EncryptionAlgorithmOffset = 0x14;
    private const int EncryptionKeyBitsOffset = 0x18;
    private const int PrngAlgorithmOffset = 0x1C;
    private const int PrngKeySizeOffset = 0x20;
    private const int UuidOffset = 0x24;
    private const int UuidSize = 16;
    private const int BlockSizeOffset = 0x34;
    private const int DataSizeOffset = 0x38;
    private const int DataOffsetOffset = 0x40;
    private const int KeyCountOffset = 0x48;
    private const int KeyPointerTableOffset = 0x4C;

    // One entry of the key-pointer table.
    private const int KeyPointerTypeOffset = 0x00;
    private const int KeyPointerOffsetOffset = 0x04;
    private const int KeyPointerSizeOffset = 0x0C;

    /// <summary>The payload AES key size in bytes.</summary>
    public int EncryptionKeyBytes => (int)(EncryptionKeyBits / 8);

    /// <summary>
    /// The number of ciphertext blocks the payload occupies, the last one possibly
    /// short.
    /// </summary>
    public long BlockCount => BlockSize == 0 ? 0 : (DataSize + BlockSize - 1) / BlockSize;

    /// <summary>
    /// Reads and validates the header at the front of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">A readable, seekable stream over the whole file.</param>
    /// <remarks>
    /// Leaves the stream position unspecified; every later read seeks first.
    /// </remarks>
    public static Result<EncryptedDmgHeader> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return Result<EncryptedDmgHeader>.Failure(DmgError.Internal(
                "An encrypted image must be opened from a readable, seekable stream.",
                $"CanRead={stream.CanRead}, CanSeek={stream.CanSeek}."));
        }

        long length;

        try
        {
            length = stream.Length;
        }
        catch (Exception exception) when (
            exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Result<EncryptedDmgHeader>.Failure(DmgError.Internal(
                "Could not determine the size of the encrypted image.",
                exception.Message));
        }

        if (length < FixedHeaderSize)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This file is too small to be an encrypted disk image.",
                $"{length} bytes; the encrcdsa header alone is {FixedHeaderSize}.");
        }

        // The key descriptor lives inside the header region, which hdiutil pads out
        // to several kilobytes. One bounded window covers every real layout and
        // cannot run off anything.
        int window = (int)Math.Min(length, ImageProbeContext.HeaderBytes);
        byte[] header = new byte[window];

        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(header);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "Could not read the header of the encrypted image.",
                exception.Message);
        }

        return Parse(header, length);
    }

    /// <summary>
    /// Parses and validates a header that has already been read.
    /// </summary>
    /// <param name="header">
    /// The bytes from offset zero. Long enough to cover the key descriptor, which
    /// in practice means the first few kilobytes.
    /// </param>
    /// <param name="fileLength">
    /// The size of the whole file. Every declared offset is checked against it, so
    /// passing a wrong value here weakens the bounds checks rather than the parse.
    /// </param>
    public static Result<EncryptedDmgHeader> Parse(ReadOnlySpan<byte> header, long fileLength)
    {
        if (fileLength < FixedHeaderSize)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This file is too small to be an encrypted disk image.",
                $"{fileLength} bytes; the encrcdsa header alone is {FixedHeaderSize}.");
        }

        if (header.Length < FixedHeaderSize)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image header is truncated.",
                $"Got {header.Length} bytes, need at least {FixedHeaderSize}.");
        }

        if (!header[SignatureOffset..(SignatureOffset + 8)].SequenceEqual(Magic))
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This is not an encrypted disk image: the encrcdsa signature is missing.",
                ImageFormatSignatures.DescribeLeadingBytes(header));
        }

        uint version = ReadUInt32(header, VersionOffset);

        if (version != SupportedVersion)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"This encrypted image is encrcdsa version {version}; this build reads version "
                + $"{SupportedVersion}.",
                "encrcdsa.Version");
        }

        uint encryptionKeyBits = ReadUInt32(header, EncryptionKeyBitsOffset);

        if (encryptionKeyBits is not (128 or 256))
        {
            // Not a best-effort: a key size we do not recognise means the whole
            // key-material layout below is a guess, and a guess that decrypts to
            // noise is worse than a refusal.
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"This encrypted image uses a {encryptionKeyBits}-bit key; this build reads "
                + "AES-128 and AES-256 images.",
                "encrcdsa.EncryptionKeyBits");
        }

        uint blockSize = ReadUInt32(header, BlockSizeOffset);

        if (blockSize == 0 || blockSize > MaxBlockSize || blockSize % 16 != 0)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a block size that cannot be decrypted.",
                $"encrcdsa.BlockSize = {blockSize}; it must be a non-zero multiple of the "
                + $"16-byte AES block and no larger than {MaxBlockSize}.");
        }

        ulong dataSize = ReadUInt64(header, DataSizeOffset);
        ulong dataOffset = ReadUInt64(header, DataOffsetOffset);

        if (!BigEndian.RangeFitsWithin(dataOffset, dataSize, (ulong)fileLength))
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image points at a payload outside the file.",
                $"DataOffset={dataOffset}, DataSize={dataSize}, file is {fileLength} bytes.");
        }

        if (dataOffset < FixedHeaderSize)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image claims its payload overlaps its own header.",
                $"DataOffset={dataOffset}, header is at least {FixedHeaderSize} bytes.");
        }

        uint keyCount = ReadUInt32(header, KeyCountOffset);

        if (keyCount == 0)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image carries no wrapped keys, so nothing can unlock it.",
                "encrcdsa.KeyCount = 0.");
        }

        if (keyCount > MaxKeyCount)
        {
            return Result<EncryptedDmgHeader>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares an implausible number of wrapped keys.",
                $"encrcdsa.KeyCount = {keyCount}; the ceiling is {MaxKeyCount}.");
        }

        Result<EncryptedDmgKeyBlob> blob = ReadPassphraseKey(header, fileLength, keyCount);

        if (!blob.TryGetValue(out EncryptedDmgKeyBlob? keyBlob))
        {
            return blob.CastFailure<EncryptedDmgHeader>();
        }

        return Result<EncryptedDmgHeader>.Success(new EncryptedDmgHeader(
            version,
            ReadUInt32(header, EncryptionIvSizeOffset),
            ReadUInt32(header, EncryptionModeOffset),
            ReadUInt32(header, EncryptionAlgorithmOffset),
            encryptionKeyBits,
            ReadUInt32(header, PrngAlgorithmOffset),
            ReadUInt32(header, PrngKeySizeOffset),
            header.Slice(UuidOffset, UuidSize).ToArray(),
            blockSize,
            (long)dataSize,
            (long)dataOffset,
            keyCount,
            keyBlob));
    }

    /// <summary>
    /// Walks the key-pointer table for the first passphrase-wrapped entry and parses
    /// the descriptor it points at.
    /// </summary>
    private static Result<EncryptedDmgKeyBlob> ReadPassphraseKey(
        ReadOnlySpan<byte> header,
        long fileLength,
        uint keyCount)
    {
        long tableEnd = (long)KeyPointerTableOffset + ((long)keyCount * KeyPointerSize);

        if (tableEnd > header.Length)
        {
            return Result<EncryptedDmgKeyBlob>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image's key table runs past the end of its header.",
                $"{keyCount} entries end at {tableEnd}, but only {header.Length} header bytes "
                + "were read.");
        }

        for (uint index = 0; index < keyCount; index++)
        {
            int entry = KeyPointerTableOffset + ((int)index * KeyPointerSize);

            if (ReadUInt32(header, entry + KeyPointerTypeOffset) != PassphraseKeyType)
            {
                continue;
            }

            ulong offset = ReadUInt64(header, entry + KeyPointerOffsetOffset);
            ulong size = ReadUInt64(header, entry + KeyPointerSizeOffset);

            if (!BigEndian.RangeFitsWithin(offset, size, (ulong)fileLength))
            {
                return Result<EncryptedDmgKeyBlob>.Failure(
                    DmgExitCode.CorruptImage,
                    "The encrypted image points at key material outside the file.",
                    $"Key {index}: offset={offset}, size={size}, file is {fileLength} bytes.");
            }

            if (offset + size > (ulong)header.Length)
            {
                return Result<EncryptedDmgKeyBlob>.Failure(
                    DmgExitCode.UnsupportedFormat,
                    "This encrypted image keeps its key material outside the header region, "
                    + "which this build does not read.",
                    $"Key {index}: offset={offset}, size={size}, header window is "
                    + $"{header.Length} bytes.");
            }

            return EncryptedDmgKeyBlob.Parse(header.Slice((int)offset, (int)size));
        }

        return Result<EncryptedDmgKeyBlob>.Failure(
            DmgExitCode.UnsupportedFormat,
            "This encrypted image has no passphrase-wrapped key; it is unlocked by a "
            + "certificate or a keychain entry, which this build does not support.",
            $"{keyCount} key entries, none of type {PassphraseKeyType}.");
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset) =>
        BigEndian.TryReadUInt32(span, offset, out uint value) ? value : 0;

    private static ulong ReadUInt64(ReadOnlySpan<byte> span, int offset) =>
        BigEndian.TryReadUInt64(span, offset, out ulong value) ? value : 0;
}
