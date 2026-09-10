namespace Dmg.Core.Containers;

/// <summary>
/// Walks a parsed property list to <c>resource-fork</c> → <c>blkx</c> and decodes
/// each entry's <c>Data</c> payload.
/// </summary>
/// <remarks>
/// The property list also carries a <c>plst</c> array and, depending on how the
/// image was made, several other keys. Only <c>blkx</c> is of interest; everything
/// beside it is left alone rather than being treated as an error, because a UDIF
/// image is free to carry resources this tool has no opinion about.
/// </remarks>
public static class BlkxReader
{
    /// <summary>
    /// The largest single mish payload this build will decode. A mish block is a
    /// 204-byte header plus 40 bytes per chunk, so 16 MiB is over 400,000 chunks
    /// in one region - far past anything a real image contains.
    /// </summary>
    public const long DefaultMaxEntryBytes = 16L * 1024 * 1024;

    /// <summary>The most <c>blkx</c> entries an image may declare.</summary>
    public const int MaxEntries = 65536;

    /// <summary>The key holding the resource dictionary.</summary>
    public const string ResourceForkKey = "resource-fork";

    /// <summary>The key holding the block map array.</summary>
    public const string BlkxKey = "blkx";

    /// <summary>
    /// Extracts and base64-decodes every <c>blkx</c> entry.
    /// </summary>
    /// <param name="root">The parsed property list.</param>
    /// <param name="maxEntryBytes">The per-entry refuse-to-allocate ceiling.</param>
    public static Result<IReadOnlyList<BlkxEntry>> Extract(
        PlistValue root,
        long maxEntryBytes = DefaultMaxEntryBytes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntryBytes);

        if (root.Kind != PlistKind.Dictionary)
        {
            return Failure(
                "The image's property list is not a dictionary.",
                $"Its root is a {root.Kind}.");
        }

        if (!root.TryGetEntry(ResourceForkKey, PlistKind.Dictionary, out PlistValue? resourceFork))
        {
            return Failure(
                "The image's property list has no resource-fork dictionary.",
                root.TryGetEntry(ResourceForkKey, out PlistValue? wrong)
                    ? $"'{ResourceForkKey}' is a {wrong.Kind}, not a dict."
                    : $"Keys present: {string.Join(", ", root.Entries.Keys)}.");
        }

        if (!resourceFork.TryGetEntry(BlkxKey, PlistKind.Array, out PlistValue? blkx))
        {
            return Failure(
                "The image's property list has no blkx block map.",
                resourceFork.TryGetEntry(BlkxKey, out PlistValue? wrong)
                    ? $"'{BlkxKey}' is a {wrong.Kind}, not an array."
                    : $"Keys present: {string.Join(", ", resourceFork.Entries.Keys)}.");
        }

        if (blkx.Items.Count == 0)
        {
            return Failure(
                "The image declares no regions at all.",
                "resource-fork/blkx is an empty array.");
        }

        if (blkx.Items.Count > MaxEntries)
        {
            return Failure(
                "The image declares an implausible number of regions.",
                $"{blkx.Items.Count} blkx entries; the ceiling is {MaxEntries}.");
        }

        var entries = new List<BlkxEntry>(blkx.Items.Count);

        for (int index = 0; index < blkx.Items.Count; index++)
        {
            Result<BlkxEntry> entry = ReadEntry(blkx.Items[index], index, maxEntryBytes);

            if (!entry.TryGetValue(out BlkxEntry? read))
            {
                return entry.CastFailure<IReadOnlyList<BlkxEntry>>();
            }

            entries.Add(read);
        }

        return Result<IReadOnlyList<BlkxEntry>>.Success(entries);
    }

    private static Result<BlkxEntry> ReadEntry(PlistValue item, int index, long maxEntryBytes)
    {
        if (item.Kind != PlistKind.Dictionary)
        {
            return Result<BlkxEntry>.Failure(
                DmgExitCode.CorruptImage,
                $"Region {index} of the image's block map is not a dictionary.",
                $"It is a {item.Kind}.");
        }

        if (!item.TryGetEntry("Data", PlistKind.Data, out PlistValue? data))
        {
            return Result<BlkxEntry>.Failure(
                DmgExitCode.CorruptImage,
                $"Region {index} of the image's block map carries no Data payload.",
                $"Keys present: {string.Join(", ", item.Entries.Keys)}.");
        }

        Result<byte[]> decoded = Base64Payload.Decode(
            data.Text,
            maxEntryBytes,
            $"block map payload for region {index}");

        if (!decoded.TryGetValue(out byte[]? bytes))
        {
            return decoded.CastFailure<BlkxEntry>();
        }

        return Result<BlkxEntry>.Success(new BlkxEntry(
            index,
            Text(item, "Name"),
            Text(item, "CFName"),
            Text(item, "Attributes"),
            Text(item, "ID"),
            bytes));
    }

    private static string Text(PlistValue entry, string key) =>
        entry.TryGetString(key, out string? text) ? text : string.Empty;

    private static Result<IReadOnlyList<BlkxEntry>> Failure(string message, string? detail) =>
        Result<IReadOnlyList<BlkxEntry>>.Failure(DmgExitCode.CorruptImage, message, detail);
}
