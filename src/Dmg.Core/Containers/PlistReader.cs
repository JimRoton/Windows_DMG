using System.Globalization;
using System.Text;
using System.Xml;

namespace Dmg.Core.Containers;

/// <summary>
/// Reads the small subset of Apple's XML property list format that a UDIF
/// container uses: <c>dict</c>, <c>array</c>, <c>key</c>, <c>string</c>,
/// <c>data</c> and <c>integer</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entity expansion is off.</b> The reader runs with
/// <see cref="DtdProcessing.Prohibit"/> and a null <see cref="XmlResolver"/>, so
/// the billion-laughs family of attacks and every external-entity trick fail
/// closed rather than being merely discouraged.
/// </para>
/// <para>
/// That setting alone would reject every genuine image, because hdiutil writes
/// <c>&lt;!DOCTYPE plist PUBLIC …&gt;</c> at the top of the plist and
/// <see cref="DtdProcessing.Prohibit"/> throws on a doctype declaration. So the
/// declaration is removed from the prolog before parsing - and only from the
/// prolog, and only when it carries no internal subset. A doctype with a
/// <c>[ … ]</c> subset is the one that could define entities, and that is refused
/// outright. What reaches <see cref="XmlReader"/> therefore has no DTD at all, and
/// any <c>&amp;entity;</c> beyond the five built-ins is an undefined reference and
/// a parse failure.
/// </para>
/// <para>
/// Everything is bounded. <see cref="Read"/> refuses a declared XML length that
/// does not fit the file or exceeds <see cref="DefaultMaxLength"/> before it
/// allocates, and the parser caps nesting depth and node count so a small file
/// cannot expand into a large tree.
/// </para>
/// </remarks>
public static class PlistReader
{
    /// <summary>
    /// The largest property list this build will read into memory. Real ones run
    /// to a few kilobytes per blkx entry; a megabyte is already a thousand-region
    /// image, and 64 MiB is far past anything legitimate.
    /// </summary>
    public const long DefaultMaxLength = 64L * 1024 * 1024;

    /// <summary>How deeply <c>dict</c> and <c>array</c> may nest.</summary>
    public const int MaxDepth = 64;

    /// <summary>How many nodes a single property list may contain.</summary>
    public const int MaxNodes = 1_000_000;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>
    /// Reads the property list a koly trailer points at.
    /// </summary>
    /// <param name="stream">The image, positioned anywhere; this seeks.</param>
    /// <param name="offset">The trailer's <c>XMLOffset</c>.</param>
    /// <param name="length">The trailer's <c>XMLLength</c>.</param>
    /// <param name="maxLength">The refuse-to-allocate ceiling.</param>
    public static Result<PlistValue> Read(
        Stream stream,
        ulong offset,
        ulong length,
        long maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return Result<PlistValue>.Failure(DmgError.Internal(
                "A property list must be read from a seekable stream.",
                $"CanRead={stream.CanRead}, CanSeek={stream.CanSeek}."));
        }

        if (length == 0)
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.UnsupportedFormat,
                "This image carries no XML property list.",
                "koly.XMLLength is zero; resource-fork-only images are not supported.");
        }

        if (length > (ulong)maxLength)
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.CorruptImage,
                "The image declares an implausibly large XML property list.",
                $"koly.XMLLength = {length}; the ceiling is {maxLength} bytes.");
        }

        long fileLength;

        try
        {
            fileLength = stream.Length;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Result<PlistValue>.Failure(DmgError.Internal(
                "Could not determine the size of the image file.",
                exception.Message));
        }

        if (!BigEndian.RangeFitsWithin(offset, length, (ulong)fileLength))
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.CorruptImage,
                "The XML property list lies outside the image file.",
                $"XMLOffset={offset}, XMLLength={length}, file is {fileLength} bytes.");
        }

        byte[] buffer = new byte[length];

        try
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.CorruptImage,
                "Could not read the XML property list.",
                exception.Message);
        }

        return Parse(buffer);
    }

    /// <summary>Parses a property list from its UTF-8 bytes.</summary>
    public static Result<PlistValue> Parse(ReadOnlySpan<byte> utf8)
    {
        // Skip a UTF-8 BOM; XmlReader over a TextReader would treat it as content.
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        string xml;

        try
        {
            xml = Encoding.UTF8.GetString(utf8);
        }
        catch (ArgumentException exception)
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.CorruptImage,
                "The XML property list is not valid UTF-8.",
                exception.Message);
        }

        return Parse(xml);
    }

    /// <summary>Parses a property list from its text.</summary>
    public static Result<PlistValue> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        Result<string> withoutDoctype = StripDoctype(xml);

        if (!withoutDoctype.TryGetValue(out string? cleaned))
        {
            return withoutDoctype.CastFailure<PlistValue>();
        }

        try
        {
            using var textReader = new StringReader(cleaned);
            using XmlReader reader = XmlReader.Create(textReader, ReaderSettings);

            return ParseDocument(reader);
        }
        catch (XmlException exception)
        {
            return Result<PlistValue>.Failure(
                DmgExitCode.CorruptImage,
                "The XML property list is malformed.",
                exception.Message);
        }
    }

    /// <summary>
    /// Removes a doctype declaration from the document prolog, refusing one that
    /// carries an internal subset. Only the prolog is examined, so the characters
    /// <c>&lt;!DOCTYPE</c> appearing inside element content are left alone.
    /// </summary>
    private static Result<string> StripDoctype(string xml)
    {
        int index = 0;

        while (index < xml.Length)
        {
            if (char.IsWhiteSpace(xml[index]))
            {
                index++;
                continue;
            }

            if (Matches(xml, index, "<?"))
            {
                int end = xml.IndexOf("?>", index, StringComparison.Ordinal);

                if (end < 0)
                {
                    return Result<string>.Success(xml); // Let XmlReader report it.
                }

                index = end + 2;
                continue;
            }

            if (Matches(xml, index, "<!--"))
            {
                int end = xml.IndexOf("-->", index, StringComparison.Ordinal);

                if (end < 0)
                {
                    return Result<string>.Success(xml);
                }

                index = end + 3;
                continue;
            }

            if (!Matches(xml, index, "<!DOCTYPE"))
            {
                return Result<string>.Success(xml); // The root element; nothing to strip.
            }

            for (int scan = index; scan < xml.Length; scan++)
            {
                if (xml[scan] == '[')
                {
                    return Result<string>.Failure(
                        DmgExitCode.UnsupportedFormat,
                        "The XML property list declares an internal DTD subset.",
                        "Entity definitions are refused: a plist in a disk image has no legitimate use for them.");
                }

                if (xml[scan] == '>')
                {
                    return Result<string>.Success(xml.Remove(index, scan - index + 1));
                }
            }

            return Result<string>.Failure(
                DmgExitCode.CorruptImage,
                "The XML property list has an unterminated doctype declaration.",
                $"'<!DOCTYPE' at offset {index} is never closed.");
        }

        return Result<string>.Success(xml);
    }

    private static bool Matches(string text, int index, string token) =>
        index + token.Length <= text.Length
        && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static Result<PlistValue> ParseDocument(XmlReader reader)
    {
        var stack = new Stack<Frame>();
        PlistValue? root = null;
        int nodes = 0;
        bool sawPlistElement = false;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (++nodes > MaxNodes)
                    {
                        return Corrupt("The XML property list has too many elements.",
                            $"Stopped after {MaxNodes} elements.");
                    }

                    string name = reader.Name;

                    if (string.Equals(name, "plist", StringComparison.Ordinal) && !sawPlistElement && stack.Count == 0)
                    {
                        sawPlistElement = true;

                        if (reader.IsEmptyElement)
                        {
                            return Corrupt("The XML property list is empty.", "<plist/> carries no value.");
                        }

                        continue;
                    }

                    Result<PlistValue?> element = ReadElement(reader, name, stack);

                    if (!element.Ok)
                    {
                        return element.CastFailure<PlistValue>();
                    }

                    if (element.GetValueOrDefault() is PlistValue produced)
                    {
                        Result attached = Attach(stack, produced, ref root);

                        if (!attached.Ok)
                        {
                            return attached.CastFailure<PlistValue>();
                        }
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (stack.Count == 0
                        || !string.Equals(reader.Name, ContainerName(stack.Peek().Kind), StringComparison.Ordinal))
                    {
                        continue; // </plist>, or the close of an element we skipped.
                    }

                    Frame frame = stack.Pop();

                    if (frame.HasPendingKey)
                    {
                        return Corrupt("The XML property list has a <key> with no value after it.",
                            $"Key '{frame.PendingKey}'.");
                    }

                    Result closed = Attach(stack, frame.Build(), ref root);

                    if (!closed.Ok)
                    {
                        return closed.CastFailure<PlistValue>();
                    }

                    break;

                default:
                    break;
            }
        }

        if (stack.Count != 0)
        {
            return Corrupt("The XML property list ends inside an unclosed element.",
                $"{stack.Count} container(s) left open.");
        }

        return root is null
            ? Corrupt("The XML property list contains no value.", "No dict, array, string, data or integer was found.")
            : Result<PlistValue>.Success(root);
    }

    /// <summary>
    /// Handles one start tag. Returns a value to attach, or a successful result
    /// with no value when the element was consumed without producing one (a
    /// <c>key</c>, a container that has been pushed, or a type we ignore).
    /// </summary>
    private static Result<PlistValue?> ReadElement(XmlReader reader, string name, Stack<Frame> stack)
    {
        switch (name)
        {
            case "dict":
            case "array":
            {
                PlistKind kind = string.Equals(name, "dict", StringComparison.Ordinal)
                    ? PlistKind.Dictionary
                    : PlistKind.Array;

                if (reader.IsEmptyElement)
                {
                    return Result<PlistValue?>.Success(new Frame(kind).Build());
                }

                if (stack.Count >= MaxDepth)
                {
                    return CorruptNode("The XML property list is nested too deeply.",
                        $"More than {MaxDepth} levels of dict/array.");
                }

                stack.Push(new Frame(kind));
                return NoValue;
            }

            case "key":
            {
                if (stack.Count == 0 || stack.Peek().Kind != PlistKind.Dictionary)
                {
                    return CorruptNode("The XML property list has a <key> outside a <dict>.", null);
                }

                Result<string> text = ReadTextContent(reader);

                if (!text.TryGetValue(out string? key))
                {
                    return text.CastFailure<PlistValue?>();
                }

                return stack.Peek().SetKey(key)
                    ? NoValue
                    : CorruptNode("The XML property list has two <key> elements in a row, or a duplicate key.",
                        $"Key '{key}'.");
            }

            case "string":
            case "data":
            case "integer":
            {
                Result<string> text = ReadTextContent(reader);

                if (!text.TryGetValue(out string? content))
                {
                    return text.CastFailure<PlistValue?>();
                }

                return name switch
                {
                    "string" => Result<PlistValue?>.Success(PlistValue.ForString(content)),
                    "data" => Result<PlistValue?>.Success(PlistValue.ForData(content)),
                    _ => ParseInteger(content),
                };
            }

            default:
            {
                // <true/>, <false/>, <real>, <date>: legal plist, but nothing in a
                // UDIF container needs them. Consume and drop, along with any key
                // that was waiting for a value.
                if (!SkipElement(reader))
                {
                    return CorruptNode("The XML property list ends inside an unclosed element.",
                        $"<{name}> is never closed.");
                }

                if (stack.Count > 0)
                {
                    stack.Peek().ClearKey();
                }

                return NoValue;
            }
        }
    }

    /// <summary>
    /// Consumes an element this reader does not model, stopping <em>on</em> its end
    /// tag. <see cref="XmlReader.Skip"/> stops one node further along, which would
    /// make the caller's loop swallow the following sibling.
    /// </summary>
    private static bool SkipElement(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return true;
        }

        int depth = 1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && --depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Result<PlistValue?> ParseInteger(string content)
    {
        string trimmed = content.Trim();

        bool isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        bool parsed = isHex
            ? long.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)
            : long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        return parsed
            ? Result<PlistValue?>.Success(PlistValue.ForInteger(value, trimmed))
            : CorruptNode("The XML property list has an <integer> that is not a number.", $"'{content}'.");
    }

    /// <summary>
    /// Collects the character data of the current element, leaving the reader on
    /// its end tag so the caller's loop advances exactly once afterwards.
    /// </summary>
    private static Result<string> ReadTextContent(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return Result<string>.Success(string.Empty);
        }

        string name = reader.Name;
        var builder = new StringBuilder();

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    builder.Append(reader.Value);
                    break;

                case XmlNodeType.EndElement:
                    return string.Equals(reader.Name, name, StringComparison.Ordinal)
                        ? Result<string>.Success(builder.ToString())
                        : Result<string>.Failure(
                            DmgExitCode.CorruptImage,
                            "The XML property list is malformed.",
                            $"<{name}> closed by </{reader.Name}>.");

                case XmlNodeType.Element:
                    return Result<string>.Failure(
                        DmgExitCode.CorruptImage,
                        $"The XML property list nests <{reader.Name}> inside <{name}>.",
                        "A key, string, data or integer element may only contain text.");

                default:
                    break;
            }
        }

        return Result<string>.Failure(
            DmgExitCode.CorruptImage,
            "The XML property list ends inside an unclosed element.",
            $"<{name}> is never closed.");
    }

    private static Result Attach(Stack<Frame> stack, PlistValue value, ref PlistValue? root)
    {
        if (stack.Count == 0)
        {
            if (root is not null)
            {
                return Result.Failure(
                    DmgExitCode.CorruptImage,
                    "The XML property list has more than one top-level value.");
            }

            root = value;
            return Result.Success();
        }

        return stack.Peek().Add(value);
    }

    private static string ContainerName(PlistKind kind) =>
        kind == PlistKind.Dictionary ? "dict" : "array";

    private static Result<PlistValue?> NoValue => Result<PlistValue?>.Success(null);

    private static Result<PlistValue> Corrupt(string message, string? detail) =>
        Result<PlistValue>.Failure(DmgExitCode.CorruptImage, message, detail);

    private static Result<PlistValue?> CorruptNode(string message, string? detail) =>
        Result<PlistValue?>.Failure(DmgExitCode.CorruptImage, message, detail);

    private sealed class Frame(PlistKind kind)
    {
        private readonly List<PlistValue> _items = [];
        private readonly Dictionary<string, PlistValue> _entries = new(StringComparer.Ordinal);
        private string? _key;

        public PlistKind Kind { get; } = kind;

        /// <summary>True when a &lt;key&gt; is still waiting for its value.</summary>
        public bool HasPendingKey => _key is not null;

        /// <summary>The waiting key, for the error message when the dict ends without a value.</summary>
        public string? PendingKey => _key;

        public bool SetKey(string key)
        {
            if (_key is not null || _entries.ContainsKey(key))
            {
                return false;
            }

            _key = key;
            return true;
        }

        public void ClearKey() => _key = null;

        public Result Add(PlistValue value)
        {
            if (Kind == PlistKind.Array)
            {
                _items.Add(value);
                return Result.Success();
            }

            if (_key is null)
            {
                return Result.Failure(
                    DmgExitCode.CorruptImage,
                    "The XML property list has a value inside a <dict> with no <key> before it.");
            }

            _entries[_key] = value;
            _key = null;
            return Result.Success();
        }

        public PlistValue Build() => Kind == PlistKind.Array
            ? PlistValue.ForArray(_items)
            : PlistValue.ForDictionary(_entries);
    }
}
