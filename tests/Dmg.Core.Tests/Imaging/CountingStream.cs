namespace Dmg.Core.Tests.Imaging;

/// <summary>
/// A pass-through stream that counts what was asked of the thing underneath it.
/// </summary>
/// <remarks>
/// Used to assert things the return value of a read cannot show: that a zero-fill
/// extent touched the data fork not at all, that a cache hit did no I/O, that
/// disposing the block stream did or did not dispose its source.
/// </remarks>
internal sealed class CountingStream(Stream inner, bool leaveOpen = true) : Stream
{
    private readonly Stream _inner = inner;
    private readonly bool _leaveOpen = leaveOpen;

    /// <summary>How many times <c>Read</c> was called.</summary>
    internal int Reads { get; private set; }

    /// <summary>How many bytes came back from those reads.</summary>
    internal long BytesRead { get; private set; }

    /// <summary>How many times the position was moved.</summary>
    internal int Seeks { get; private set; }

    /// <summary>True once this stream has been disposed.</summary>
    internal bool Disposed { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set
        {
            Seeks++;
            _inner.Position = value;
        }
    }

    /// <summary>Forgets everything counted so far.</summary>
    internal void Reset()
    {
        Reads = 0;
        BytesRead = 0;
        Seeks = 0;
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        Reads++;
        int read = _inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Seeks++;
        return _inner.Seek(offset, origin);
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        Disposed = true;

        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
