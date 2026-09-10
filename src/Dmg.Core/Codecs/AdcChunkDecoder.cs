namespace Dmg.Core.Codecs;

/// <summary>
/// Apple Data Compression, <c>0x80000004</c> - the LZ77 variant in UDCO images.
/// Written from scratch, because there is no decoder for it anywhere in the BCL and
/// this project takes no third-party dependencies.
/// </summary>
/// <remarks>
/// <para>
/// The format is three tokens, chosen by the top bits of the first byte. All the
/// multi-byte fields are big-endian, and every match reaches backwards into the
/// output produced so far:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>First byte</term><description>Token</description>
///   </listheader>
///   <item>
///     <term><c>1xxxxxxx</c></term>
///     <description>
///     Literal run: <c>(b &amp; 0x7F) + 1</c> bytes follow verbatim. 1 to 128 bytes.
///     </description>
///   </item>
///   <item>
///     <term><c>01xxxxxx</c></term>
///     <description>
///     Long match, three bytes total: length <c>(b &amp; 0x3F) + 4</c> (4 to 67),
///     offset <c>(next1 &lt;&lt; 8 | next2) + 1</c> (1 to 65536).
///     </description>
///   </item>
///   <item>
///     <term><c>00xxxxxx</c></term>
///     <description>
///     Short match, two bytes total: length <c>((b &gt;&gt; 2) &amp; 0x0F) + 3</c>
///     (3 to 18), offset <c>((b &amp; 0x03) &lt;&lt; 8 | next) + 1</c> (1 to 1024).
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>The long match is a three-byte token with a full 16-bit offset.</b> The format
/// note in <c>docs/03-udif-format-reference.md</c> used to describe it as a two-byte
/// token sharing the short match's encoding, which would make the two tokens
/// identical and the long one pointless. A UDCO image produced by <c>hdiutil</c>
/// settles it: decoded with the two-byte reading, every ADC chunk in that image
/// fails on a back-reference that points before the start of the chunk; decoded as
/// above, all six chunks reproduce the raw image byte for byte. The doc was
/// corrected to match.
/// </para>
/// <para>
/// <b>Overlapping matches are legal and load-bearing.</b> A run of 67 identical
/// bytes is encoded as one byte of literal followed by a match of length 67 at
/// offset 1 - it reads bytes this same loop is still writing. That is why the copy
/// is a byte-at-a-time loop and not a block move: a <c>CopyTo</c> would read the
/// pre-existing contents of the overlap and produce garbage.
/// </para>
/// <para>
/// Every read is bounded against the end of the input and every write against the
/// end of the output before it happens, so a hostile chunk produces a
/// <see cref="DmgExitCode.CorruptImage"/> failure rather than an exception. The
/// decode also insists that the token stream ends exactly where the sectors do:
/// Apple's own encoder consumes its input to the last byte, and a chunk with tokens
/// left over is not the chunk the image described.
/// </para>
/// </remarks>
public sealed class AdcChunkDecoder : IChunkDecoder
{
    private AdcChunkDecoder()
    {
    }

    /// <summary>The shared instance.</summary>
    public static AdcChunkDecoder Instance { get; } = new();

    /// <inheritdoc />
    public uint EntryType => ChunkEntryType.AppleAdc;

    /// <inheritdoc />
    public string Name => "ADC";

    /// <summary>True: the token stream comes from the data fork.</summary>
    public bool ReadsDataFork => true;

    /// <summary>
    /// Expands one ADC chunk into <paramref name="destination"/>, which is exactly
    /// as long as the chunk declared.
    /// </summary>
    public Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int input = 0;
        int output = 0;

        while (output < destination.Length)
        {
            if (input >= source.Length)
            {
                return Result<int>.Failure(ChunkDecodeErrors.Truncated(
                    Name,
                    $"The token stream ended after {output} of "
                    + $"{destination.Length} bytes."));
            }

            byte token = source[input];
            int length;
            int offset;

            if ((token & 0x80) != 0)
            {
                // Literal run: the bytes follow the token.
                length = (token & 0x7F) + 1;
                input = checked(input + 1);

                if (length > source.Length - input)
                {
                    return Result<int>.Failure(ChunkDecodeErrors.Truncated(
                        Name,
                        $"A literal run of {length} bytes at input offset {input} "
                        + $"runs {length - (source.Length - input)} bytes past the end "
                        + "of the chunk."));
                }

                if (length > destination.Length - output)
                {
                    return Result<int>.Failure(ChunkDecodeErrors.Overflow(
                        Name,
                        destination.Length,
                        $"A literal run of {length} bytes at output offset {output} "
                        + "does not fit."));
                }

                source.Slice(input, length).CopyTo(destination.Slice(output, length));
                input = checked(input + length);
                output = checked(output + length);
                continue;
            }

            if ((token & 0x40) != 0)
            {
                // Long match: three-byte token, 16-bit offset.
                if (source.Length - input < 3)
                {
                    return Result<int>.Failure(ChunkDecodeErrors.Truncated(
                        Name,
                        $"A long match at input offset {input} is missing its offset bytes."));
                }

                length = (token & 0x3F) + 4;
                offset = ((source[input + 1] << 8) | source[input + 2]) + 1;
                input = checked(input + 3);
            }
            else
            {
                // Short match: two-byte token, 10-bit offset.
                if (source.Length - input < 2)
                {
                    return Result<int>.Failure(ChunkDecodeErrors.Truncated(
                        Name,
                        $"A short match at input offset {input} is missing its offset byte."));
                }

                length = ((token >> 2) & 0x0F) + 3;
                offset = (((token & 0x03) << 8) | source[input + 1]) + 1;
                input = checked(input + 2);
            }

            if (offset > output)
            {
                return Result<int>.Failure(ChunkDecodeErrors.Malformed(
                    Name,
                    $"A match at output offset {output} reaches {offset} bytes back, "
                    + "before the start of the chunk."));
            }

            if (length > destination.Length - output)
            {
                return Result<int>.Failure(ChunkDecodeErrors.Overflow(
                    Name,
                    destination.Length,
                    $"A match of {length} bytes at output offset {output} does not fit."));
            }

            // Byte at a time, deliberately. Matches are allowed to overlap the bytes
            // this loop is still writing, and a block copy would read the overlap as
            // it was before the match rather than as it is being produced.
            int from = output - offset;

            for (int i = 0; i < length; i++)
            {
                destination[output + i] = destination[from + i];
            }

            output = checked(output + length);
        }

        if (input != source.Length)
        {
            return Result<int>.Failure(ChunkDecodeErrors.Malformed(
                Name,
                $"The chunk's sectors were produced after {input} bytes but the chunk "
                + $"carries {source.Length}."));
        }

        return Result<int>.Success(output);
    }
}
