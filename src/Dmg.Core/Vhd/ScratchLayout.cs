namespace Dmg.Core.Vhd;

/// <summary>
/// Where scratch VHDs live, and how their paths are built.
/// </summary>
/// <remarks>
/// <para>
/// The default root is <c>%LOCALAPPDATA%\dmg\scratch</c>. That is a per-user,
/// non-roaming, non-backed-up location - which is what a multi-gigabyte decoded
/// image should be, and what keeps two users on the same machine out of each
/// other's files.
/// </para>
/// <para>
/// <b>Portable on purpose.</b> <c>Dmg.Core</c> builds and tests on macOS, so the
/// root is resolved through <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// rather than by reading <c>%LOCALAPPDATA%</c> directly. On Windows that folder
/// <i>is</i> <c>%LOCALAPPDATA%</c>; on macOS and Linux it is the platform's
/// equivalent, so the tests exercise the real code path instead of a stub.
/// </para>
/// <para>
/// <b>Every path here is constructed.</b> The only variable part of a scratch path
/// is a <see cref="MountId"/> this tool generated. No volume name, no file name
/// and no string out of the image reaches the filesystem. See
/// <see cref="MountId"/> for why that matters.
/// </para>
/// </remarks>
public static class ScratchLayout
{
    /// <summary>The directory created under the local application data folder.</summary>
    public const string ApplicationDirectoryName = "dmg";

    /// <summary>The directory created under <see cref="ApplicationDirectoryName"/>.</summary>
    public const string ScratchDirectoryName = "scratch";

    /// <summary>The extension every scratch image is given.</summary>
    public const string VhdExtension = ".vhd";

    /// <summary>
    /// The default scratch root: <c>%LOCALAPPDATA%\dmg\scratch</c> on Windows, the
    /// platform equivalent elsewhere.
    /// </summary>
    /// <remarks>
    /// Falls back to the temporary directory when the platform reports no local
    /// application data folder - a stripped container, a service account with no
    /// profile - because a missing profile should not stop a conversion.
    /// </remarks>
    public static string DefaultRoot()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        string baseDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? Path.GetTempPath()
            : localAppData;

        return Path.Combine(baseDirectory, ApplicationDirectoryName, ScratchDirectoryName);
    }

    /// <summary>The directory one mount's files live in: <c>&lt;root&gt;\&lt;id&gt;</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a <c>default(MountId)</c>.</exception>
    public static string DirectoryFor(string root, MountId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!id.IsValid)
        {
            throw new ArgumentException("A scratch path needs a generated mount id.", nameof(id));
        }

        return Path.Combine(Path.GetFullPath(root), id.Value);
    }

    /// <summary>The image's file name: the mount id and nothing else.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a <c>default(MountId)</c>.</exception>
    public static string FileNameFor(MountId id)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("A scratch file name needs a generated mount id.", nameof(id));
        }

        return id.Value + VhdExtension;
    }

    /// <summary>The full path of one mount's VHD.</summary>
    public static string VhdPathFor(string root, MountId id) =>
        Path.Combine(DirectoryFor(root, id), FileNameFor(id));

    /// <summary>
    /// Whether <paramref name="candidate"/> really is inside <paramref name="root"/>
    /// once both are fully resolved.
    /// </summary>
    /// <remarks>
    /// Belt and braces. Paths here are built from generated identifiers and cannot
    /// escape, but this is checked anyway before anything is created or deleted:
    /// the cost is a string comparison and the failure mode it guards against is
    /// deleting somebody's home directory.
    /// </remarks>
    public static bool IsWithin(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (fullCandidate.Length <= fullRoot.Length)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullCandidate.StartsWith(fullRoot, comparison)
            && fullCandidate[fullRoot.Length] == Path.DirectorySeparatorChar;
    }
}
