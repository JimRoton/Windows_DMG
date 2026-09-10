using System.Buffers.Binary;
using System.Globalization;

namespace Dmg.Core.Containers;

/// <summary>
/// Bounds-checked big-endian reads over a <see cref="ReadOnlySpan{T}"/>, plus the
/// overflow-safe sector arithmetic every UDIF structure needs.
/// </summary>
/// <remarks>
/// <para>
/// Every multi-byte integer in a UDIF container is big-endian, and every one of
/// them arrives from a file we do not trust. The helpers here exist so that a
/// short read past the end of a buffer, or a <c>SectorCount</c> chosen to make
/// <c>SectorCount * 512</c> wrap around, becomes a <see cref="Result"/> failure at
/// the point it happens instead of a plausible-looking wrong number that only
/// causes trouble three layers up.
/// </para>
/// <para>
/// Two shapes are offered for each width. The <c>TryRead*</c> overloads return a
/// <see cref="bool"/> and are for hot paths and for callers that already know how
/// they want to describe the failure; the <c>Read*</c> overloads return a
/// <see cref="Result{T}"/> carrying a <see cref="DmgExitCode.CorruptImage"/> error
/// that names the field, and are what parsing code should normally use.
/// </para>
/// <para>
/// All arithmetic here is <c>checked</c>. Nothing in this class ever wraps: on
/// overflow it returns a failure.
/// </para>
/// </remarks>
public static class BigEndian
{
    /// <summary>The sector size every UDIF length and offset is expressed in.</summary>
    public const int SectorSize = 512;

    /// <summary>
    /// Reads a big-endian <see cref="ushort"/>, or returns false when
    /// <paramref name="offset"/> is negative or the two bytes do not fit.
    /// </summary>
    public static bool TryReadUInt16(ReadOnlySpan<byte> source, int offset, out ushort value)
    {
        if (!TrySlice(source, offset, sizeof(ushort), out ReadOnlySpan<byte> window))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(window);
        return true;
    }

    /// <summary>
    /// Reads a big-endian <see cref="uint"/>, or returns false when
    /// <paramref name="offset"/> is negative or the four bytes do not fit.
    /// </summary>
    public static bool TryReadUInt32(ReadOnlySpan<byte> source, int offset, out uint value)
    {
        if (!TrySlice(source, offset, sizeof(uint), out ReadOnlySpan<byte> window))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(window);
        return true;
    }

    /// <summary>
    /// Reads a big-endian <see cref="ulong"/>, or returns false when
    /// <paramref name="offset"/> is negative or the eight bytes do not fit.
    /// </summary>
    public static bool TryReadUInt64(ReadOnlySpan<byte> source, int offset, out ulong value)
    {
        if (!TrySlice(source, offset, sizeof(ulong), out ReadOnlySpan<byte> window))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64BigEndian(window);
        return true;
    }

    /// <summary>Reads a big-endian <see cref="ushort"/>, naming the field on failure.</summary>
    public static Result<ushort> ReadUInt16(ReadOnlySpan<byte> source, int offset, string field) =>
        TryReadUInt16(source, offset, out ushort value)
            ? Result<ushort>.Success(value)
            : OutOfRange<ushort>(source.Length, offset, sizeof(ushort), field);

    /// <summary>Reads a big-endian <see cref="uint"/>, naming the field on failure.</summary>
    public static Result<uint> ReadUInt32(ReadOnlySpan<byte> source, int offset, string field) =>
        TryReadUInt32(source, offset, out uint value)
            ? Result<uint>.Success(value)
            : OutOfRange<uint>(source.Length, offset, sizeof(uint), field);

    /// <summary>Reads a big-endian <see cref="ulong"/>, naming the field on failure.</summary>
    public static Result<ulong> ReadUInt64(ReadOnlySpan<byte> source, int offset, string field) =>
        TryReadUInt64(source, offset, out ulong value)
            ? Result<ulong>.Success(value)
            : OutOfRange<ulong>(source.Length, offset, sizeof(ulong), field);

    /// <summary>
    /// Slices <paramref name="length"/> bytes at <paramref name="offset"/>, returning
    /// false rather than throwing when the window is not wholly inside
    /// <paramref name="source"/>. Negative arguments are a failed slice, not an
    /// exception: they routinely come from arithmetic on attacker-chosen fields.
    /// </summary>
    public static bool TrySlice(
        ReadOnlySpan<byte> source,
        int offset,
        int length,
        out ReadOnlySpan<byte> window)
    {
        if (offset < 0 || length < 0 || offset > source.Length - length)
        {
            window = default;
            return false;
        }

        window = source.Slice(offset, length);
        return true;
    }

    /// <summary>
    /// The 32-bit value of a four-character ASCII tag such as <c>koly</c> or
    /// <c>mish</c>, read the way it sits in the file (big-endian).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not exactly four ASCII characters. Tags are
    /// compile-time constants in this codebase, so getting one wrong is a bug.
    /// </exception>
    public static uint FourCharCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length != 4)
        {
            throw new ArgumentException(
                $"A four-character code must be exactly four characters; got '{code}'.",
                nameof(code));
        }

        uint value = 0;

        foreach (char character in code)
        {
            if (character > 0x7F)
            {
                throw new ArgumentException(
                    $"A four-character code must be ASCII; got '{code}'.",
                    nameof(code));
            }

            value = (value << 8) | character;
        }

        return value;
    }

    /// <summary>
    /// Renders a tag for an error message: the printable ASCII form when all four
    /// bytes are printable, otherwise the hexadecimal value. Used so a "bad magic"
    /// message can say what was actually there.
    /// </summary>
    public static string DescribeFourCharCode(uint value)
    {
        Span<char> characters = stackalloc char[4];

        for (int index = 0; index < 4; index++)
        {
            int shift = (3 - index) * 8;
            char character = (char)((value >> shift) & 0xFF);

            if (character is < ' ' or > '~')
            {
                return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
            }

            characters[index] = character;
        }

        return new string(characters);
    }

    /// <summary>
    /// <c>sectors * 512</c> in checked arithmetic. A sector count large enough to
    /// wrap the multiplication is a corrupt image, not a very large disk.
    /// </summary>
    public static Result<ulong> SectorsToBytes(ulong sectors, string field) =>
        Multiply(sectors, SectorSize, field);

    /// <summary>Checked addition. Overflow is a failure, never a wrap.</summary>
    public static Result<ulong> Add(ulong left, ulong right, string what)
    {
        try
        {
            return Result<ulong>.Success(checked(left + right));
        }
        catch (OverflowException)
        {
            return Result<ulong>.Failure(
                DmgExitCode.CorruptImage,
                $"The image declares an impossible {what}.",
                $"{left} + {right} overflows a 64-bit unsigned integer.");
        }
    }

    /// <summary>Checked multiplication. Overflow is a failure, never a wrap.</summary>
    public static Result<ulong> Multiply(ulong left, ulong right, string what)
    {
        try
        {
            return Result<ulong>.Success(checked(left * right));
        }
        catch (OverflowException)
        {
            return Result<ulong>.Failure(
                DmgExitCode.CorruptImage,
                $"The image declares an impossible {what}.",
                $"{left} * {right} overflows a 64-bit unsigned integer.");
        }
    }

    /// <summary>
    /// True when <c>[offset, offset + length)</c> lies wholly inside a region of
    /// <paramref name="limit"/> bytes, with the addition itself checked. An empty
    /// range at exactly <paramref name="limit"/> fits.
    /// </summary>
    public static bool RangeFitsWithin(ulong offset, ulong length, ulong limit)
    {
        if (offset > limit)
        {
            return false;
        }

        return length <= limit - offset;
    }

    private static Result<T> OutOfRange<T>(int available, int offset, int size, string field) =>
        Result<T>.Failure(
            DmgExitCode.CorruptImage,
            $"The image is truncated: {field} is missing.",
            $"Wanted {size} bytes at offset {offset} of a {available}-byte structure.");
}
