using System.Security.Cryptography;
using Dmg.Core.Imaging;

namespace Dmg.Core.Crypto;

/// <summary>
/// An encrypted disk image as an ordinary read-only, seekable <see cref="Stream"/>
/// of its plaintext.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a decorator on the byte source, not a mode inside the parser.</b> An
/// <c>encrcdsa</c> v2 file is a plaintext header followed by ciphertext that
/// decrypts to a complete, ordinary disk image - koly trailer, property list, data
/// fork and all. So the whole of decryption fits between the file and the container
/// reader:
/// </para>
/// <code>
/// FileStream ──▶ EncryptedBlockStream ──▶ container reader ──▶ DmgBlockStream
/// </code>
/// <para>
/// The container reader receives a plain seekable stream, finds the trailer at the
/// end of <em>that stream's</em> <see cref="Length"/>, and never learns that
/// encryption exists. No <c>isEncrypted</c> flag is threaded through the parser, no
/// branch in the trailer search, no second code path to keep correct.
/// </para>
/// <para>
/// <b><see cref="Length"/> is the plaintext length.</b> It reports
/// <c>header.DataSize</c>, not the number of ciphertext bytes in the file. This
/// looks like a detail and is the single most consequential line in the class: the
/// reader above searches for the koly trailer in the last 512 bytes of whatever
/// <see cref="Length"/> says, so reporting the ciphertext length points it at the
/// wrong 512 bytes and a perfectly good image comes back as "this is not a DMG".
/// The two differ whenever the last block is short.
/// </para>
/// <para>
/// <b>One block of cache.</b> Reads are served out of a single decrypted block, so
/// a read that crosses a block boundary decrypts each block once and a run of small
/// sequential reads inside one block decrypts nothing at all. A read that is not
/// block-aligned decrypts the block containing it and copies out the slice it
/// wanted; there is no partial-block decryption, because CBC has no such thing.
/// </para>
/// <para>
/// <b>Not thread-safe</b>, like every other <see cref="Stream"/>: one
/// <see cref="Position"/>, one cached block, one reader at a time.
/// </para>
/// <para>
/// Failures during a read are thrown as <see cref="DmgStreamException"/> - an
/// <see cref="IOException"/> carrying the <see cref="DmgError"/> and its exit code -
/// because <see cref="Read(Span{byte})"/> has nowhere to put a
/// <see cref="Result{T}"/>. Opening is still a <see cref="Result{T}"/>.
/// </para>
/// </remarks>
public sealed class EncryptedBlockStream : Stream
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly EncryptedDmgKeys _keys;
    private readonly Aes _aes;
    private readonly byte[] _ciphertext;
    private readonly byte[] _plaintext;
    private readonly long _ciphertextLength;

    private long _cachedBlock = -1;
    private int _cachedLength;
    private long _position;
    private bool _disposed;

    private EncryptedBlockStream(
        Stream source,
        bool leaveOpen,
        EncryptedDmgHeader header,
        EncryptedDmgKeys keys,
        Aes aes,
        long ciphertextLength)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        _keys = keys;
        _aes = aes;
        Header = header;
        _ciphertext = new byte[header.BlockSize];
        _plaintext = new byte[header.BlockSize];
        _ciphertextLength = ciphertextLength;
    }

    /// <summary>The parsed header this stream decrypts against.</summary>
    public EncryptedDmgHeader Header { get; }

    /// <summary>The size of one independently-encrypted block, from the header.</summary>
    public int BlockSize => (int)Header.BlockSize;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <summary>
    /// Always false. This build reads encrypted images; it does not write them, and
    /// writing one would mean re-encrypting a block on every touched byte.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// The <b>plaintext</b> length, from <c>header.DataSize</c> - never the number
    /// of ciphertext bytes. See the class remarks; getting this wrong is what makes
    /// a good image look like a corrupt one.
    /// </summary>
    public override long Length => Header.DataSize;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _position = value;
        }
    }

    /// <summary>
    /// Opens the encrypted image in <paramref name="source"/> with
    /// <paramref name="passphrase"/>.
    /// </summary>
    /// <param name="source">A readable, seekable stream over the whole file.</param>
    /// <param name="passphrase">
    /// The passphrase bytes. Read here and not retained; the caller still owns them
    /// and is responsible for zeroing them.
    /// </param>
    /// <param name="leaveOpen">True to leave <paramref name="source"/> open on dispose.</param>
    public static Result<EncryptedBlockStream> Open(
        Stream source,
        ReadOnlySpan<byte> passphrase,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        Result<EncryptedDmgHeader> parsed = EncryptedDmgHeader.Read(source);

        if (!parsed.TryGetValue(out EncryptedDmgHeader? header))
        {
            return parsed.CastFailure<EncryptedBlockStream>();
        }

        Result<EncryptedDmgKeys> unwrapped = EncryptedDmgKeys.Unwrap(header, passphrase);

        if (!unwrapped.TryGetValue(out EncryptedDmgKeys? keys))
        {
            return unwrapped.CastFailure<EncryptedBlockStream>();
        }

        return Create(source, header, keys, leaveOpen);
    }

    /// <summary>
    /// Wraps a source whose header is already parsed and whose keys are already
    /// unwrapped.
    /// </summary>
    /// <param name="source">A readable, seekable stream over the whole file.</param>
    /// <param name="header">The parsed header.</param>
    /// <param name="keys">
    /// The unwrapped keys. <b>Ownership transfers:</b> the returned stream disposes
    /// them, and on failure they are disposed here, so no caller ends up holding
    /// live key material after a failed open.
    /// </param>
    /// <param name="leaveOpen">True to leave <paramref name="source"/> open on dispose.</param>
    public static Result<EncryptedBlockStream> Create(
        Stream source,
        EncryptedDmgHeader header,
        EncryptedDmgKeys keys,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(keys);

        Result<long> validated = Validate(source, header, keys);

        if (!validated.TryGetValue(out long ciphertextLength))
        {
            keys.Dispose();
            return validated.CastFailure<EncryptedBlockStream>();
        }

        Aes aes = Aes.Create();

        try
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = keys.AesKey.ToArray();
        }
        catch (CryptographicException exception)
        {
            aes.Dispose();
            keys.Dispose();

            return Result<EncryptedBlockStream>.Failure(
                DmgExitCode.DecryptionFailed,
                "The key recovered from this image was not usable as an AES key.",
                exception.Message);
        }

        return Result<EncryptedBlockStream>.Success(
            new EncryptedBlockStream(source, leaveOpen, header, keys, aes, ciphertextLength));
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="DmgStreamException">The image could not be read or decrypted.</exception>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        int wanted = (int)Math.Min(buffer.Length, Length - _position);
        int copied = 0;

        while (copied < wanted)
        {
            long block = (_position + copied) / BlockSize;
            int within = (int)((_position + copied) % BlockSize);

            int decrypted = Decrypt(block);
            int usable = (int)Math.Min(decrypted, Length - (block * BlockSize));
            int available = usable - within;

            if (available <= 0)
            {
                // The header promised more plaintext than the ciphertext can hold.
                // Returning a short read here would look like a clean end of file.
                throw new DmgStreamException(new DmgError(
                    DmgExitCode.CorruptImage,
                    "The encrypted image ends before the payload it declares.",
                    $"Block {block} yielded {usable} usable bytes; DataSize says "
                    + $"{Length} bytes of plaintext."));
            }

            int take = Math.Min(available, wanted - copied);
            _plaintext.AsSpan(within, take).CopyTo(buffer[copied..]);
            copied += take;
        }

        _position += copied;

        return copied;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];

        return Read(one) == 1 ? one[0] : -1;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The seek lands before the start of the stream.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));

        _position = target;

        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("An encrypted image is opened read-only.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("An encrypted image is opened read-only.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("An encrypted image is opened read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            CryptographicOperations.ZeroMemory(_plaintext);
            CryptographicOperations.ZeroMemory(_ciphertext);
            _aes.Dispose();
            _keys.Dispose();

            if (!_leaveOpen)
            {
                _source.Dispose();
            }
        }

        _disposed = true;

        base.Dispose(disposing);
    }

    /// <summary>
    /// Makes sure <paramref name="block"/> is the decrypted block in
    /// <c>_plaintext</c> and returns how many bytes of it are real.
    /// </summary>
    private int Decrypt(long block)
    {
        if (_cachedBlock == block)
        {
            return _cachedLength;
        }

        long offset = block * BlockSize;
        int length = (int)Math.Min(BlockSize, _ciphertextLength - offset);

        if (length <= 0)
        {
            _cachedBlock = block;
            _cachedLength = 0;
            return 0;
        }

        if (length % 16 != 0)
        {
            // CBC has no partial blocks. A payload that does not end on a cipher
            // block boundary is a broken file, not something to decrypt as far as
            // it goes.
            throw new DmgStreamException(new DmgError(
                DmgExitCode.CorruptImage,
                "The encrypted image's payload does not end on a cipher block boundary.",
                $"The last block holds {length} bytes, which is not a multiple of 16."));
        }

        try
        {
            _source.Seek(Header.DataOffset + offset, SeekOrigin.Begin);
            _source.ReadExactly(_ciphertext, 0, length);
        }
        catch (EndOfStreamException exception)
        {
            throw new DmgStreamException(
                new DmgError(
                    DmgExitCode.CorruptImage,
                    "The encrypted image is shorter than its header says.",
                    $"Reading block {block} at {Header.DataOffset + offset} ran off the end."),
                exception);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new DmgStreamException(
                new DmgError(
                    DmgExitCode.CorruptImage,
                    "Could not read the encrypted image.",
                    exception.Message),
                exception);
        }

        Span<byte> iv = stackalloc byte[EncryptedBlockIv.Size];
        EncryptedBlockIv.Compute(_keys.HmacKey, block, iv);

        try
        {
            int written = _aes.DecryptCbc(
                _ciphertext.AsSpan(0, length),
                iv,
                _plaintext,
                PaddingMode.None);

            // The cache is only valid once the decrypt has actually happened; set
            // last so a throw leaves the previous block cached rather than a lie.
            _cachedBlock = block;
            _cachedLength = written;

            return written;
        }
        catch (CryptographicException exception)
        {
            _cachedBlock = -1;
            _cachedLength = 0;

            throw new DmgStreamException(
                new DmgError(
                    DmgExitCode.DecryptionFailed,
                    "A block of this image could not be decrypted.",
                    exception.Message),
                exception);
        }
    }

    private static Result<long> Validate(
        Stream source,
        EncryptedDmgHeader header,
        EncryptedDmgKeys keys)
    {
        if (!source.CanRead || !source.CanSeek)
        {
            return Result<long>.Failure(DmgError.Internal(
                "An encrypted image must be read from a readable, seekable stream.",
                $"CanRead={source.CanRead}, CanSeek={source.CanSeek}."));
        }

        if (keys.AesKey.Length != header.EncryptionKeyBytes)
        {
            return Result<long>.Failure(DmgError.Internal(
                "The unwrapped key is not the size the header declares.",
                $"Key is {keys.AesKey.Length} bytes, header says {header.EncryptionKeyBytes}."));
        }

        if (header.BlockSize == 0 || header.BlockSize % 16 != 0)
        {
            return Result<long>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image declares a block size that cannot be decrypted.",
                $"BlockSize={header.BlockSize}.");
        }

        long sourceLength;

        try
        {
            sourceLength = source.Length;
        }
        catch (Exception exception) when (
            exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Result<long>.Failure(DmgError.Internal(
                "Could not determine the size of the encrypted image.",
                exception.Message));
        }

        // The ciphertext runs from DataOffset to the end of the file. It is at
        // least DataSize bytes and may be a little more, because CBC cannot encrypt
        // a partial block: a payload that does not fill its last block is padded out
        // on disk, and DataSize is what says how much of that block is real.
        long ciphertextLength = sourceLength - header.DataOffset;

        if (ciphertextLength < header.DataSize)
        {
            return Result<long>.Failure(
                DmgExitCode.CorruptImage,
                "The encrypted image is shorter than the payload it declares.",
                $"{ciphertextLength} ciphertext bytes for {header.DataSize} bytes of plaintext.");
        }

        return Result<long>.Success(ciphertextLength);
    }
}
