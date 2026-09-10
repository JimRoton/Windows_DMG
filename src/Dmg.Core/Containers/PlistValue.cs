using System.Diagnostics.CodeAnalysis;

namespace Dmg.Core.Containers;

/// <summary>The plist element kinds this reader understands.</summary>
public enum PlistKind
{
    /// <summary><c>&lt;dict&gt;</c> - keys to values.</summary>
    Dictionary,

    /// <summary><c>&lt;array&gt;</c> - an ordered list.</summary>
    Array,

    /// <summary><c>&lt;string&gt;</c>.</summary>
    String,

    /// <summary><c>&lt;data&gt;</c> - base64 text, left undecoded here.</summary>
    Data,

    /// <summary><c>&lt;integer&gt;</c>.</summary>
    Integer,
}

/// <summary>
/// A node in a parsed property list.
/// </summary>
/// <remarks>
/// <para>
/// One type with a <see cref="Kind"/> rather than a hierarchy of five, because
/// every consumer of this is walking a known path - <c>resource-fork</c> then
/// <c>blkx</c> then each entry's <c>Data</c> - and wants to ask "is this a dict?"
/// without a cast at every step.
/// </para>
/// <para>
/// <c>&lt;data&gt;</c> keeps its base64 <em>text</em>. Decoding is where a plist
/// turns into megabytes of memory, so it is deliberately a separate, bounded step
/// rather than something that happens implicitly during the parse.
/// </para>
/// </remarks>
public sealed class PlistValue
{
    private static readonly IReadOnlyList<PlistValue> NoItems = [];
    private static readonly IReadOnlyDictionary<string, PlistValue> NoEntries =
        new Dictionary<string, PlistValue>(StringComparer.Ordinal);

    private PlistValue(
        PlistKind kind,
        string text,
        long integer,
        IReadOnlyList<PlistValue> items,
        IReadOnlyDictionary<string, PlistValue> entries)
    {
        Kind = kind;
        Text = text;
        Integer = integer;
        Items = items;
        Entries = entries;
    }

    /// <summary>Which plist element this node came from.</summary>
    public PlistKind Kind { get; }

    /// <summary>
    /// The element's text: the string for <see cref="PlistKind.String"/>, the raw
    /// base64 (whitespace and all) for <see cref="PlistKind.Data"/>, the literal
    /// digits for <see cref="PlistKind.Integer"/>, and empty for containers.
    /// </summary>
    public string Text { get; }

    /// <summary>The parsed value of an <see cref="PlistKind.Integer"/> node; 0 otherwise.</summary>
    public long Integer { get; }

    /// <summary>The elements of an <see cref="PlistKind.Array"/>; empty otherwise.</summary>
    public IReadOnlyList<PlistValue> Items { get; }

    /// <summary>The entries of a <see cref="PlistKind.Dictionary"/>; empty otherwise.</summary>
    public IReadOnlyDictionary<string, PlistValue> Entries { get; }

    /// <summary>Creates a string node.</summary>
    public static PlistValue ForString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PlistValue(PlistKind.String, text, 0, NoItems, NoEntries);
    }

    /// <summary>Creates a data node from its undecoded base64 text.</summary>
    public static PlistValue ForData(string base64Text)
    {
        ArgumentNullException.ThrowIfNull(base64Text);
        return new PlistValue(PlistKind.Data, base64Text, 0, NoItems, NoEntries);
    }

    /// <summary>Creates an integer node.</summary>
    public static PlistValue ForInteger(long value, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PlistValue(PlistKind.Integer, text, value, NoItems, NoEntries);
    }

    /// <summary>Creates an array node.</summary>
    public static PlistValue ForArray(IReadOnlyList<PlistValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PlistValue(PlistKind.Array, string.Empty, 0, items, NoEntries);
    }

    /// <summary>Creates a dictionary node.</summary>
    public static PlistValue ForDictionary(IReadOnlyDictionary<string, PlistValue> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new PlistValue(PlistKind.Dictionary, string.Empty, 0, NoItems, entries);
    }

    /// <summary>Looks up a dictionary entry. False for any non-dictionary node.</summary>
    public bool TryGetEntry(string key, [NotNullWhen(true)] out PlistValue? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Kind is PlistKind.Dictionary && Entries.TryGetValue(key, out PlistValue? found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Looks up a dictionary entry and requires it to be of <paramref name="kind"/>.</summary>
    public bool TryGetEntry(string key, PlistKind kind, [NotNullWhen(true)] out PlistValue? value)
    {
        if (TryGetEntry(key, out PlistValue? found) && found.Kind == kind)
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Looks up a string entry's text.</summary>
    public bool TryGetString(string key, [NotNullWhen(true)] out string? text)
    {
        if (TryGetEntry(key, PlistKind.String, out PlistValue? found))
        {
            text = found.Text;
            return true;
        }

        text = null;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        PlistKind.Dictionary => $"dict({Entries.Count})",
        PlistKind.Array => $"array({Items.Count})",
        PlistKind.Data => $"data({Text.Length} base64 chars)",
        _ => $"{Kind}({Text})",
    };
}
