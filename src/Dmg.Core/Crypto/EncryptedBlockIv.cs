using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dmg.Core.Crypto;

/// <summary>
/// The per-block initialisation vector: <c>HMAC-SHA1(hmacKey, BE32(blockNumber))</c>
/// truncated to the AES block size.
/// </summary>
/// <remarks>
/// <para>
/// <b>This function is why an encrypted image is seekable.</b> Every 512-byte block
/// is its own CBC run, and the IV that starts it depends on nothing but the block's
/// index - not on the block before it, not on any running state. So block 24,575 of
/// a twelve-megabyte image can be decrypted without touching blocks 0 to 24,574,
/// which is what lets the UDIF reader above read a koly trailer off the end of a
/// multi-gigabyte image immediately. Chain the blocks instead and the same read
/// costs a full decryption of the file.
/// </para>
/// <para>
/// The block number is counted from the start of the ciphertext, not from the start
/// of the file: block 0 is the one at <c>DataOffset</c>.
/// </para>
/// <para>
/// SHA-1 produces 20 bytes and AES takes 16, so the last four are dropped. That is
/// the format, not a choice; and the HMAC here is a key-derivation step, not an
/// integrity check, so SHA-1's collision weaknesses do not bear on it.
/// </para>
/// </remarks>
public static class EncryptedBlockIv
{
    /// <summary>Bytes of IV an AES-CBC block needs.</summary>
    public const int Size = 16;

    /// <summary>Bytes HMAC-SHA1 produces, of which the first <see cref="Size"/> are used.</summary>
    public const int HashSize = 20;

    /// <summary>
    /// Computes the IV for one block.
    /// </summary>
    /// <param name="hmacKey">The 20-byte HMAC-SHA1 key from the unwrapped key blob.</param>
    /// <param name="blockNumber">The block's index, counted from the start of the ciphertext.</param>
    /// <param name="destination">Exactly <see cref="Size"/> bytes to write the IV into.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not <see cref="Size"/> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockNumber"/> is negative.</exception>
    public static void Compute(ReadOnlySpan<byte> hmacKey, long blockNumber, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);

        if (destination.Length != Size)
        {
            throw new ArgumentException(
                $"A block IV is exactly {Size} bytes; got {destination.Length}.",
                nameof(destination));
        }

        // The counter is four bytes big-endian. A block number past uint.MaxValue
        // would wrap and hand two different blocks the same IV, so it is refused
        // rather than truncated - though at 512 bytes a block that is a two-terabyte
        // image and no such .dmg exists.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockNumber, uint.MaxValue);

        Span<byte> counter = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(counter, (uint)blockNumber);

        Span<byte> hash = stackalloc byte[HashSize];
        HMACSHA1.HashData(hmacKey, counter, hash);

        hash[..Size].CopyTo(destination);
    }

    /// <summary>
    /// Computes the IV for one block into a fresh array. For tests and for callers
    /// that are not in a read loop.
    /// </summary>
    /// <param name="hmacKey">The 20-byte HMAC-SHA1 key from the unwrapped key blob.</param>
    /// <param name="blockNumber">The block's index, counted from the start of the ciphertext.</param>
    public static byte[] Compute(ReadOnlySpan<byte> hmacKey, long blockNumber)
    {
        byte[] iv = new byte[Size];
        Compute(hmacKey, blockNumber, iv);
        return iv;
    }
}
