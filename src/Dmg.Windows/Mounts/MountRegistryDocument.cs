using System.Text.Json.Serialization;

namespace Dmg.Windows.Mounts;

/// <summary>
/// The shape of <c>mounts.json</c> itself.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper object rather than a bare array, so that the file has somewhere to put
/// a version number. A bare array cannot grow a field, and the first time this
/// format needs one the only options would be to guess from the contents or to
/// break every existing file.
/// </para>
/// <para>
/// <see cref="Version"/> is checked on read. A file from the future is not parsed
/// on the assumption that it is close enough - it is reported and ignored, and the
/// user is told which version wrote it.
/// </para>
/// </remarks>
/// <param name="Version">The schema version. <see cref="MountRegistry.SchemaVersion"/> today.</param>
/// <param name="Mounts">
/// Every mount this tool believes is live. Nullable because a hand-edited or
/// half-written file can leave the property out altogether, and that has to read as
/// "no mounts" rather than as a null reference three lines later.
/// </param>
public sealed record MountRegistryDocument(
    int Version,
    IReadOnlyList<MountRecord>? Mounts)
{
    /// <summary>An empty registry at the current version.</summary>
    [JsonIgnore]
    public static MountRegistryDocument Empty { get; } = new(MountRegistry.SchemaVersion, []);
}
