namespace Dmg.Core.Containers;

/// <summary>
/// Decodes the base64 that Apple's property lists wrap across lines.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Convert.FromBase64String(string)"/> is documented as ignoring
/// whitespace, and does - but only some of it, and it throws a
/// <see cref="FormatException"/> on anything it does not like, which is the wrong
/// shape of answer for a parser that must not throw on hostile input. So the text
/// is stripped of every whitespace character first and decoded through
/// <see cref="Convert.TryFromBase64Chars"/>, which reports failure instead.
/// </para>
/// <para>
/// The output size is worked out from the stripped length and checked against a
/// ceiling <em>before</em> either buffer is allocated. A property list of a few
/// kilobytes cannot be allowed to name a gigabyte of output.
/// </para>
/// </remarks>
public static class Base64Payload
{
    /// <summary>
    /// Decodes <paramref name="text"/>, refusing anything that would decode to
    /// more than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="text">Base64, with any amount of whitespace anywhere.</param>
    /// <param name="maxBytes">The refuse-to-allocate ceiling.</param>
    /// <param name="what">What is being decoded, for the error message.</param>
    public static Result<byte[]> Decode(string text, long maxBytes, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        int significant = 0;

        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                significant++;
            }
        }

        if (significant == 0)
        {
            return Result<byte[]>.Success([]);
        }

        // Four base64 characters carry three bytes; padding only ever shrinks that.
        long upperBound = ((long)significant / 4 * 3) + 3;

        if (upperBound > maxBytes)
        {
            return Result<byte[]>.Failure(
                DmgExitCode.CorruptImage,
                $"The image declares an implausibly large {what}.",
                $"{significant} base64 characters decode to about {upperBound} bytes; " +
                $"the ceiling is {maxBytes}.");
        }

        char[] stripped = new char[significant];
        int index = 0;

        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                stripped[index++] = character;
            }
        }

        byte[] buffer = new byte[upperBound];

        if (!Convert.TryFromBase64Chars(stripped, buffer, out int written))
        {
            return Result<byte[]>.Failure(
                DmgExitCode.CorruptImage,
                $"The {what} is not valid base64.",
                $"{significant} characters after whitespace was stripped.");
        }

        if (written != buffer.Length)
        {
            Array.Resize(ref buffer, written);
        }

        return Result<byte[]>.Success(buffer);
    }
}
