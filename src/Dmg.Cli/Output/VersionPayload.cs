namespace Dmg.Cli.Output;

/// <summary>
/// What <c>dmg version --json</c> puts on stdout.
/// </summary>
/// <remarks>
/// A shape of its own rather than serializing <see cref="BuildInfo"/> directly,
/// because the JSON is a contract a script may branch on and the record is an
/// internal convenience. Renaming a property on one should not silently rewrite
/// the other.
/// </remarks>
/// <param name="Tool">Always <c>dmg</c>. Present so a caller can tell what it read.</param>
/// <param name="Version">The version on its own.</param>
/// <param name="Commit">The commit the build came from, or null when unstamped.</param>
/// <param name="Configuration"><c>Debug</c> or <c>Release</c>.</param>
/// <param name="Architecture">The architecture this binary was built for.</param>
/// <param name="OsArchitecture">The architecture of the machine it is running on.</param>
/// <param name="Emulated">True when those two differ.</param>
/// <param name="Runtime">The runtime's own description.</param>
/// <param name="Os">The operating system's own description.</param>
public sealed record VersionPayload(
    string Tool,
    string Version,
    string? Commit,
    string Configuration,
    string Architecture,
    string OsArchitecture,
    bool Emulated,
    string Runtime,
    string Os)
{
    /// <summary>Builds the payload from a <see cref="BuildInfo"/>.</summary>
    /// <param name="build">The build to describe.</param>
    public static VersionPayload From(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);

        return new VersionPayload(
            BuildInfo.ToolName,
            build.Version,
            build.Commit,
            build.Configuration,
            BuildInfo.Name(build.ProcessArchitecture),
            BuildInfo.Name(build.OsArchitecture),
            build.IsEmulated,
            build.Framework,
            build.OperatingSystem);
    }
}
