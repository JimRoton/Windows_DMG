using Dmg.Core.Containers;

namespace Dmg.Core.Codecs;

/// <summary>
/// The <c>uint</c> view of <see cref="Containers.ChunkEntryType"/> that the codec
/// layer works in, plus the names we print for each value.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Containers.ChunkEntryType"/> is the one place the wire values
/// themselves are written down; every constant here is a cast of that enum rather
/// than a second copy of the hex, so the two can no longer drift apart the way two
/// independently hand-maintained lists of the same ten values did. This type used
/// to define its own constants under the same name as that enum - two public
/// <c>ChunkEntryType</c>s in different namespaces, which is exactly the kind of
/// thing that produces a <c>CS0104</c> ambiguity the moment one file needs both
/// namespaces. It is named <see cref="ChunkEntryTypeCodes"/> so that can't happen
/// again.
/// </para>
/// <para>
/// The codec layer stays on <c>uint</c> rather than the enum deliberately:
/// <see cref="IChunkDecoder.EntryType"/>, <see cref="ChunkDecoderRegistry"/> and
/// <see cref="ChunkCodecInfo"/> are public surface other projects (<c>Dmg.Cli</c>)
/// already consume as <c>uint</c>, and a value the format does not define at all
/// still has to flow through this layer to be reported by <c>dmg info</c> - the
/// enum accepts that too (a C# enum backed by <c>uint</c> is not restricted to its
/// named members), but the codec layer's own arithmetic and dictionary keys have
/// no need for the enum's type identity.
/// </para>
/// </remarks>
public static class ChunkEntryTypeCodes
{
    /// <summary>Zero fill: emit zeros, read nothing from the data fork.</summary>
    public const uint ZeroFill = (uint)ChunkEntryType.ZeroFill;

    /// <summary>Raw: the data fork bytes are the payload, uncompressed.</summary>
    public const uint Raw = (uint)ChunkEntryType.Raw;

    /// <summary>Ignore / free: unallocated space. Emit zeros, read nothing.</summary>
    public const uint Ignore = (uint)ChunkEntryType.Ignore;

    /// <summary>Apple ADC, the LZ variant used by UDCO images.</summary>
    public const uint AppleAdc = (uint)ChunkEntryType.AppleAdc;

    /// <summary>zlib (RFC 1950), the UDZO default.</summary>
    public const uint Zlib = (uint)ChunkEntryType.Zlib;

    /// <summary>bzip2, as used by UDBZ. Recognised, not decoded.</summary>
    public const uint Bzip2 = (uint)ChunkEntryType.Bzip2;

    /// <summary>LZFSE, as used by ULFO. Recognised, not decoded.</summary>
    public const uint Lzfse = (uint)ChunkEntryType.Lzfse;

    /// <summary>LZMA, as used by ULMO. Recognised, not decoded.</summary>
    public const uint Lzma = (uint)ChunkEntryType.Lzma;

    /// <summary>A comment entry. Structural: skipped, never decoded.</summary>
    public const uint Comment = (uint)ChunkEntryType.Comment;

    /// <summary>The end-of-list marker. Structural: never decoded.</summary>
    public const uint Terminator = (uint)ChunkEntryType.Terminator;

    /// <summary>
    /// Every entry type the UDIF format defines, ascending - decodable or not.
    /// </summary>
    public static IReadOnlyList<uint> Known { get; } =
    [
        ZeroFill,
        Raw,
        Ignore,
        Comment,
        AppleAdc,
        Zlib,
        Bzip2,
        Lzfse,
        Lzma,
        Terminator,
    ];

    /// <summary>
    /// True when the entry is a structural marker rather than a chunk of data - a
    /// comment or the terminator. These have no payload to decode.
    /// </summary>
    public static bool IsStructural(uint entryType) =>
        entryType is Comment or Terminator;

    /// <summary>
    /// True when the value is one the format defines, whether or not this build can
    /// decode it.
    /// </summary>
    /// <remarks>
    /// This is the difference between "your image uses bzip2 and we do not implement
    /// bzip2" and "byte 0x2A appeared where an entry type should be". The first is a
    /// gap in this build that a user can work around by converting the image; the
    /// second is most likely a damaged chunk table. They read very differently to
    /// somebody trying to get their data back, so <c>dmg info</c> is given the means
    /// to tell them apart.
    /// </remarks>
    public static bool IsRecognised(uint entryType) => entryType switch
    {
        ZeroFill or Raw or Ignore or Comment => true,
        AppleAdc or Zlib or Bzip2 or Lzfse or Lzma => true,
        Terminator => true,
        _ => false,
    };

    /// <summary>
    /// A human name for any entry type, invented from the raw value when the type
    /// is one we have never seen. Never throws, never returns null: this feeds
    /// <c>dmg info</c>, which has to describe images it cannot open.
    /// </summary>
    public static string NameOf(uint entryType) => entryType switch
    {
        ZeroFill => "zero-fill",
        Raw => "raw",
        Ignore => "ignore",
        AppleAdc => "ADC",
        Zlib => "zlib",
        Bzip2 => "bzip2",
        Lzfse => "LZFSE",
        Lzma => "LZMA",
        Comment => "comment",
        Terminator => "terminator",
        _ => $"unknown (0x{entryType:X8})",
    };
}
