using System.Security.Cryptography;

namespace Dmg.Core.Crypto;

/// <summary>
/// A passphrase, held as bytes and zeroed the moment it is no longer needed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a <see cref="string"/>, deliberately.</b> A .NET string is immutable and
/// interned into a managed heap the process cannot scrub; once a passphrase has
/// been a string it is in memory until a garbage collection happens to overwrite
/// it, and it can be copied by any compaction in between. Bytes in an array can be
/// zeroed on the spot, which is what <see cref="Dispose"/> does.
/// </para>
/// <para>
/// The type is <see cref="IDisposable"/> so that <c>using</c> puts the zeroing on
/// every exit path there is, including the exception ones. That is the only way to
/// keep the guarantee under a throw from something further down the call.
/// </para>
/// </remarks>
public sealed class Passphrase : IDisposable
{
    private readonly byte[] _bytes;
    private bool _disposed;

    private Passphrase(byte[] bytes) => _bytes = bytes;

    /// <summary>The passphrase bytes.</summary>
    /// <exception cref="ObjectDisposedException">The passphrase has been zeroed.</exception>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    /// <summary>How many bytes the passphrase is. Readable after disposal; always zero then.</summary>
    public int Length => _disposed ? 0 : _bytes.Length;

    /// <summary>
    /// Takes ownership of <paramref name="bytes"/>. The caller must not keep a
    /// reference: this instance will zero the array.
    /// </summary>
    /// <param name="bytes">The passphrase bytes, taken as-is.</param>
    public static Passphrase Adopt(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return new Passphrase(bytes);
    }

    /// <summary>
    /// Copies <paramref name="bytes"/> into a passphrase this instance owns.
    /// </summary>
    /// <param name="bytes">The bytes to copy.</param>
    public static Passphrase CopyFrom(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8.
    /// </summary>
    /// <param name="text">The passphrase as text.</param>
    /// <remarks>
    /// <b>The string itself cannot be scrubbed.</b> Use this only where the
    /// passphrase already exists as a string - an environment variable, a test -
    /// and never as a step on the way from a keystroke to a key.
    /// </remarks>
    public static Passphrase FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new Passphrase(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Zeroes the passphrase bytes. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _disposed = true;
    }
}
