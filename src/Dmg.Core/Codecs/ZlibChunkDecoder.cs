using System.IO.Compression;

namespace Dmg.Core.Codecs;

/// <summary>
/// zlib chunks, <c>0x80000005</c> - the UDZO default, and so the codec most images
/// are made of.
/// </summary>
/// <remarks>
/// <para>
/// The inflate itself is <see cref="ZLibStream"/> from the BCL. What this class
/// adds is the framing UDIF needs and a stream does not provide: the chunk must
/// inflate to <em>exactly</em> the declared number of sectors. Not at least, not at
/// most.
/// </para>
/// <para>
/// Both directions of "not exactly" are rejected, and for different reasons. Short
/// output would leave the tail of the chunk holding whatever the buffer already
/// contained. Long output is the decompression bomb: a few hundred bytes of input
/// that want to become gigabytes. The destination span is already capped at the
/// declared length by the registry, so the extra data has nowhere to go - the job
/// here is to notice it and fail rather than stop quietly at the cap and hand back
/// a chunk that is not what the image said it was.
/// </para>
/// <para>
/// <b>One known gap.</b> A chunk whose deflate data is complete but whose four-byte
/// Adler-32 trailer has been cut off still decodes: <see cref="ZLibStream"/> reports
/// end of stream rather than a checksum failure, and there is no BCL API that will
/// tell us the difference. The bytes produced are the right bytes - the deflate
/// stream was entire - and the image's own UDIF checksums cover the rest, so this is
/// accepted rather than worked around with a byte-at-a-time inflate.
/// </para>
/// <para>
/// Nothing about a hostile chunk reaches the caller as an exception.
/// <see cref="ZLibStream"/> signals broken input by throwing, so those throws are
/// caught here and turned into <see cref="DmgExitCode.CorruptImage"/> failures.
/// </para>
/// </remarks>
public sealed class ZlibChunkDecoder : IChunkDecoder
{
    private ZlibChunkDecoder()
    {
    }

    /// <summary>The shared instance.</summary>
    public static ZlibChunkDecoder Instance { get; } = new();

    /// <inheritdoc />
    public uint EntryType => ChunkEntryType.Zlib;

    /// <inheritdoc />
    public string Name => "zlib";

    /// <summary>True: the compressed bytes come from the data fork.</summary>
    public bool ReadsDataFork => true;

    /// <summary>
    /// Inflates the chunk into <paramref name="destination"/>, insisting that it
    /// fills it exactly.
    /// </summary>
    public Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty && !destination.IsEmpty)
        {
            return Result<int>.Failure(ChunkDecodeErrors.Truncated(
                Name,
                $"A zlib chunk declaring {destination.Length} bytes carries no payload."));
        }

        // ZLibStream wants a Stream, and a Span cannot become one without a copy.
        // The copy is bounded by CompressedLength, which the caller has already read
        // and bounded against the data fork.
        byte[] compressed = source.ToArray();

        try
        {
            using MemoryStream input = new(compressed, writable: false);
            using ZLibStream inflater = new(input, CompressionMode.Decompress);

            int written = 0;

            while (written < destination.Length)
            {
                int read = inflater.Read(destination[written..]);

                if (read == 0)
                {
                    return Result<int>.Failure(ChunkDecodeErrors.ShortOutput(
                        Name, destination.Length, written));
                }

                written = checked(written + read);
            }

            // The chunk claimed to be this long. If the stream still has bytes to
            // give, it was lying, and the difference is exactly what a decompression
            // bomb looks like.
            Span<byte> probe = stackalloc byte[1];

            if (inflater.Read(probe) != 0)
            {
                return Result<int>.Failure(ChunkDecodeErrors.Overflow(
                    Name,
                    destination.Length,
                    "The compressed stream continues past the sectors the chunk declared."));
            }

            return Result<int>.Success(written);
        }
        catch (InvalidDataException ex)
        {
            return Result<int>.Failure(ChunkDecodeErrors.Malformed(Name, ex.Message));
        }
        catch (IOException ex)
        {
            // A truncated deflate stream surfaces here rather than as bad data.
            return Result<int>.Failure(ChunkDecodeErrors.Truncated(Name, ex.Message));
        }
    }
}
