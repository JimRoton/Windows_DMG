namespace Dmg.Cli.Output;

/// <summary>One mount in <c>dmg list --json</c>.</summary>
/// <param name="Id">The short id <c>dmg unmount</c> takes.</param>
/// <param name="DriveLetter">The letter it is attached at, without a colon, or null when it has none.</param>
/// <param name="Filesystem">
/// The filesystem <see cref="Windows.Volumes.IVolumeFilesystemProbe"/> found at the
/// drive letter, or <c>unknown</c> when there is no letter to ask, or asking failed.
/// </param>
/// <param name="SizeBytes">The scratch VHD's size on disk.</param>
/// <param name="Mode"><c>ro</c> or <c>rw</c>, as the registry stores it.</param>
/// <param name="SourcePath">The image that was mounted.</param>
/// <param name="VhdPath">The scratch VHD the volume was decoded to.</param>
/// <param name="MountedAtUtc">When the mount happened, in UTC.</param>
public sealed record MountListEntryPayload(
    string Id,
    string? DriveLetter,
    string Filesystem,
    long SizeBytes,
    string Mode,
    string SourcePath,
    string VhdPath,
    DateTimeOffset MountedAtUtc);

/// <summary>What <c>dmg list --json</c> puts on stdout.</summary>
/// <param name="Mounts">Every mount currently on record, after reconciliation.</param>
public sealed record MountListPayload(IReadOnlyList<MountListEntryPayload> Mounts);
