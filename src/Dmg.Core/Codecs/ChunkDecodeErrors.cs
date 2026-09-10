namespace Dmg.Core.Codecs;

/// <summary>
/// The failures a chunk decode can produce, written once so that every codec
/// reports the same way. Apart from the codec's name, a user should not be able to
/// tell which decoder produced a given message.
/// </summary>
internal static class ChunkDecodeErrors
{
    /// <summary>The input ran out mid-token: a length or offset byte that is not there.</summary>
    internal static DmgError Truncated(string codec, string detail) =>
        DmgError.Corrupt($"The {codec} chunk is truncated.", detail);

    /// <summary>
    /// The chunk decoded to more than its declared length. Always a failure, never a
    /// truncation: a chunk that inflates past its declared size is either corrupt or
    /// an attack, and silently keeping the first N bytes would hand the caller a
    /// plausible-looking image assembled from neither.
    /// </summary>
    internal static DmgError Overflow(string codec, long declaredLength, string? detail = null) =>
        DmgError.Corrupt(
            $"The {codec} chunk expands past its declared length of {declaredLength} bytes.",
            detail);

    /// <summary>
    /// The chunk decoded to fewer bytes than it declared. The missing tail would be
    /// silent zeros in the mounted image, so this is corruption too.
    /// </summary>
    internal static DmgError ShortOutput(string codec, long declaredLength, long written) =>
        DmgError.Corrupt(
            $"The {codec} chunk decoded to {written} bytes but declared {declaredLength}.",
            "A chunk must fill exactly the sectors it claims.");

    /// <summary>The bytes are not what the codec's framing says they should be.</summary>
    internal static DmgError Malformed(string codec, string detail) =>
        DmgError.Corrupt($"The {codec} chunk is malformed.", detail);
}
