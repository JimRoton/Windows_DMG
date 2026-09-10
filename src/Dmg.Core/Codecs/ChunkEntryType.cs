namespace Dmg.Core.Codecs;

/// <summary>
/// The <c>EntryType</c> values that can appear in a UDIF blkx chunk descriptor,
/// and the names we print for them.
/// </summary>
/// <remarks>
/// <para>
/// These are wire constants: they come off disk, so they are <see cref="uint"/>
/// and they never change. Two of them - <see cref="Comment"/> and
/// <see cref="Terminator"/> - are structural markers rather than codecs; they
/// carry no payload and are never decoded.
/// </para>
/// <para>
/// A value not listed here is not automatically a corrupt image. It is a chunk
/// this build cannot decode, which only matters if a read actually touches it.
/// <c>dmg info</c> must be able to describe such an image without failing, which
/// is why <see cref="NameOf"/> answers for every possible value.
/// </para>
/// </remarks>
public static class ChunkEntryType
{
    /// <summary>Zero fill: emit zeros, read nothing from the data fork.</summary>
    public const uint ZeroFill = 0x0000_0000;

    /// <summary>Raw: the data fork bytes are the payload, uncompressed.</summary>
    public const uint Raw = 0x0000_0001;

    /// <summary>Ignore / free: unallocated space. Emit zeros, read nothing.</summary>
    public const uint Ignore = 0x0000_0002;

    /// <summary>Apple ADC, the LZ variant used by UDCO images.</summary>
    public const uint AppleAdc = 0x8000_0004;

    /// <summary>zlib (RFC 1950), the UDZO default.</summary>
    public const uint Zlib = 0x8000_0005;

    /// <summary>bzip2, as used by UDBZ. Recognised, not decoded.</summary>
    public const uint Bzip2 = 0x8000_0006;

    /// <summary>LZFSE, as used by ULFO. Recognised, not decoded.</summary>
    public const uint Lzfse = 0x8000_0007;

    /// <summary>LZMA, as used by ULMO. Recognised, not decoded.</summary>
    public const uint Lzma = 0x8000_0008;

    /// <summary>A comment entry. Structural: skipped, never decoded.</summary>
    public const uint Comment = 0x7FFF_FFFE;

    /// <summary>The end-of-list marker. Structural: never decoded.</summary>
    public const uint Terminator = 0xFFFF_FFFF;

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
