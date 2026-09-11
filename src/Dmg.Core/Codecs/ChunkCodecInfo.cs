namespace Dmg.Core.Codecs;

/// <summary>
/// What the registry can say about one <c>EntryType</c> without decoding a single
/// byte: what it is called, and whether this build can decode it.
/// </summary>
/// <param name="EntryType">The raw blkx entry type.</param>
/// <param name="Name">A printable name - "zlib", "bzip2", "unknown (0x0000002A)".</param>
/// <param name="IsSupported">True when a decoder is registered for this type.</param>
/// <param name="IsStructural">
/// True for comment and terminator entries, which carry no payload. Structural
/// entries are never <see cref="IsSupported"/>, and their lack of a decoder is not a
/// limitation worth reporting to the user.
/// </param>
/// <remarks>
/// This record is why the registry is a lookup rather than a switch buried in the
/// reader: <c>dmg info</c> has to describe a UDBZ image it can never mount, and it
/// must do that without attempting - and failing - a decode.
/// </remarks>
public sealed record ChunkCodecInfo(
    uint EntryType,
    string Name,
    bool IsSupported,
    bool IsStructural)
{
    /// <summary>
    /// True when the image needs this codec to be readable and this build does not
    /// have it: the set worth printing to the user as "unsupported".
    /// </summary>
    public bool IsUnsupportedPayload => !IsSupported && !IsStructural;

    /// <summary>
    /// True when the format defines this entry type, whether or not we decode it.
    /// False means the value is not in the format at all, which usually means a
    /// damaged chunk table rather than a codec we are missing.
    /// </summary>
    public bool IsRecognised => ChunkEntryTypeCodes.IsRecognised(EntryType);

    /// <inheritdoc />
    public override string ToString() =>
        IsSupported || IsStructural ? Name : $"{Name} (unsupported)";
}
