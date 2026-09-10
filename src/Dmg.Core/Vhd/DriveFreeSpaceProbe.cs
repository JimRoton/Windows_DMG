namespace Dmg.Core.Vhd;

/// <summary>
/// The real free-space probe: <see cref="DriveInfo.AvailableFreeSpace"/> for the
/// volume that holds the path.
/// </summary>
/// <remarks>
/// <para>
/// The volume is found by matching the path against the mounted volumes' root
/// directories and taking the longest match, rather than by taking the path's
/// drive letter. On Windows that makes a mounted volume folder -
/// <c>C:\mnt\data</c> being a different disk - answer for the disk it really is;
/// on macOS, where there is no drive letter to take, it is the only thing that
/// works at all.
/// </para>
/// <para>
/// <b>Available, not free.</b> <c>AvailableFreeSpace</c> is what this user may
/// use, which on a volume with quotas is the smaller and the honest number.
/// </para>
/// <para>
/// The path need not exist: only its text is matched against mount points, so the
/// scratch directory can be measured before it is created.
/// </para>
/// </remarks>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    /// <summary>The shared instance. The type holds no state.</summary>
    public static DriveFreeSpaceProbe Instance { get; } = new();

    /// <inheritdoc />
    public Result<long> AvailableBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<long>.Failure(DmgError.Internal(
                "The scratch path could not be resolved to a volume.",
                $"'{path}': {exception.Message}"));
        }

        try
        {
            DriveInfo? volume = FindVolume(full);

            if (volume is null)
            {
                return Result<long>.Failure(DmgError.Internal(
                    "No mounted volume holds the scratch path.",
                    $"'{full}'"));
            }

            return Result<long>.Success(volume.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<long>.Failure(DmgError.Internal(
                "The volume holding the scratch path could not be interrogated.",
                $"'{full}': {exception.Message}"));
        }
    }

    /// <summary>The mounted volume whose root is the longest prefix of the path.</summary>
    private static DriveInfo? FindVolume(string fullPath)
    {
        DriveInfo? best = null;
        int bestLength = -1;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = drive.RootDirectory.FullName;

            if (root.Length <= bestLength || !Covers(root, fullPath))
            {
                continue;
            }

            // A volume that is not ready - an empty optical drive, a disconnected
            // network mapping - throws on every size property, so it is no use as
            // an answer even when its root matches.
            if (!drive.IsReady)
            {
                continue;
            }

            best = drive;
            bestLength = root.Length;
        }

        return best;
    }

    /// <summary>Whether <paramref name="root"/> is <paramref name="fullPath"/> or contains it.</summary>
    /// <remarks>
    /// Both sides get a trailing separator before they are compared, which is what
    /// makes the comparison a path comparison rather than a text one: a volume
    /// mounted at <c>/Volumes/data</c> does not hold <c>/Volumes/data-old</c>, and
    /// the root of the disk - <c>/</c>, or <c>C:\</c> - holds everything below it
    /// even though it has no path segment of its own.
    /// </remarks>
    private static bool Covers(string root, string fullPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return WithTrailingSeparator(fullPath).StartsWith(WithTrailingSeparator(root), comparison);
    }

    private static string WithTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
