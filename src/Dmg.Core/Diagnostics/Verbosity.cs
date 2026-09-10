namespace Dmg.Core.Diagnostics;

/// <summary>
/// How much the tool says while it works.
/// </summary>
/// <remarks>
/// Errors are never suppressed. <see cref="Quiet"/> silences everything else, so a
/// script can rely on "no output means it worked".
/// </remarks>
public enum Verbosity
{
    /// <summary>Errors only. Nothing on success.</summary>
    Quiet = 0,

    /// <summary>The default: results, warnings, progress and errors.</summary>
    Normal = 1,

    /// <summary>Everything, including the per-step trace and error detail.</summary>
    Verbose = 2,
}
