namespace Dmg.Core.Vhd;

/// <summary>
/// How a <see cref="ScratchSpace"/> should behave: where it lives, and whether it
/// survives.
/// </summary>
/// <remarks>
/// These map onto command-line switches: <c>--scratch &lt;dir&gt;</c> and
/// <c>--keep-scratch</c>. Neither is required, and the defaults are what a user
/// who passes nothing should get - a per-user scratch root, cleaned up on the way
/// out.
/// </remarks>
public sealed record ScratchOptions
{
    /// <summary>The defaults: the per-user scratch root, cleaned up afterwards.</summary>
    public static ScratchOptions Default { get; } = new();

    /// <summary>
    /// The directory mount directories are created under, or null for
    /// <see cref="ScratchLayout.DefaultRoot"/>.
    /// </summary>
    /// <remarks>
    /// This is a user-supplied path - <c>--scratch D:\big</c> for the case where the
    /// system drive has no room for a 40 GB image. It is used as given, because the
    /// user is entitled to choose their own directory; it is the <i>image</i> that
    /// is never allowed to choose one.
    /// </remarks>
    public string? Root { get; init; }

    /// <summary>
    /// True to leave the scratch directory behind on dispose: the
    /// <c>--keep-scratch</c> opt-out.
    /// </summary>
    /// <remarks>
    /// The opt-out exists for the two cases where deleting is exactly wrong -
    /// diagnosing a conversion that produced an unmountable image, and keeping a
    /// converted VHD to attach again later without paying for the decode twice.
    /// </remarks>
    public bool KeepScratch { get; init; }
}
