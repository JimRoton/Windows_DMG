namespace Dmg.Cli.Output;

/// <summary>
/// What <c>dmg unmount --json</c> puts on stdout for one target: an id, a drive
/// letter, or one entry of <c>--all</c>.
/// </summary>
/// <param name="WasMounted">
/// False when nothing matched the id or drive letter asked for - not an error, see
/// <see cref="Dmg.Windows.Mounts.DetachOutcome"/>. Every other field is meaningless
/// when this is false.
/// </param>
/// <param name="Id">The mount's short id.</param>
/// <param name="SourcePath">The image that was mounted.</param>
/// <param name="DriveLetter">The letter it was attached at, without a colon, or null.</param>
/// <param name="VhdPath">The scratch VHD that was decoded to.</param>
/// <param name="ScratchDeleted">True when the scratch VHD was deleted.</param>
/// <param name="ReclaimedBytes">
/// The scratch VHD's size in bytes, taken just before it was deleted. Zero when
/// <see cref="ScratchDeleted"/> is false - nothing was reclaimed.
/// </param>
public sealed record UnmountPayload(
    bool WasMounted,
    string? Id,
    string? SourcePath,
    string? DriveLetter,
    string? VhdPath,
    bool ScratchDeleted,
    long ReclaimedBytes);

/// <summary>One mount <c>dmg unmount --all</c> could not detach, and why.</summary>
/// <param name="Id">The mount's short id.</param>
/// <param name="DriveLetter">The letter it was attached at, without a colon, or null.</param>
/// <param name="SourcePath">The image that was mounted.</param>
/// <param name="Message">Why it could not be detached - most often a handle still open on it.</param>
public sealed record UnmountFailurePayload(string Id, string? DriveLetter, string SourcePath, string Message);

/// <summary>What <c>dmg unmount --all --json</c> puts on stdout.</summary>
/// <param name="Detached">Every mount that was detached, in registry order.</param>
/// <param name="Failed">Every mount that could not be, and why.</param>
public sealed record UnmountAllPayload(
    IReadOnlyList<UnmountPayload> Detached,
    IReadOnlyList<UnmountFailurePayload> Failed);
