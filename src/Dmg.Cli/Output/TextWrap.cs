namespace Dmg.Cli.Output;

/// <summary>
/// Greedy word wrap, for the prose the CLI prints alongside its tables.
/// </summary>
/// <remarks>
/// <para>
/// The tool writes whole sentences in two places - the notes at the bottom of a
/// verb's help, and the reason a volume cannot be mounted - and both are written as
/// one string in the source, because a sentence broken into a list of lines is a
/// sentence nobody will keep punctuated. Wrapping them here means the source stays
/// readable and the output stays inside a terminal.
/// </para>
/// <para>
/// A word longer than the width - a path, usually - is left whole on its own line
/// rather than broken, because half a path is worse than a long line.
/// </para>
/// </remarks>
public static class TextWrap
{
    /// <summary>
    /// The width the CLI writes to. 78 rather than 80 so that a terminal which
    /// reserves a column, or a mail client that indents a pasted report by one,
    /// still does not fold a line and turn a two-column layout into porridge.
    /// </summary>
    public const int LineWidth = 78;

    /// <summary>Wraps <paramref name="text"/> to <paramref name="width"/> columns.</summary>
    /// <param name="text">One paragraph. Existing line breaks are not honoured.</param>
    /// <param name="width">The most characters a line may have. At least one.</param>
    /// <returns>
    /// The lines, never empty: an empty paragraph comes back as one empty line, so
    /// a caller building a blank-line-separated block gets its blank line.
    /// </returns>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        List<string> lines = [];
        List<string> current = [];
        int length = 0;

        foreach (string word in text.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Count > 0 && length + 1 + word.Length > width)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
                length = 0;
            }

            length += current.Count > 0 ? word.Length + 1 : word.Length;
            current.Add(word);
        }

        if (current.Count > 0)
        {
            lines.Add(string.Join(' ', current));
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    /// <summary>
    /// Wraps to the width left over after <paramref name="prefix"/>, and puts the
    /// prefix on every line - the shape of an indented continuation.
    /// </summary>
    /// <param name="text">One paragraph.</param>
    /// <param name="prefix">What every line starts with.</param>
    public static IReadOnlyList<string> Indent(string text, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        return [.. Wrap(text, Math.Max(1, LineWidth - prefix.Length)).Select(line => prefix + line)];
    }
}
