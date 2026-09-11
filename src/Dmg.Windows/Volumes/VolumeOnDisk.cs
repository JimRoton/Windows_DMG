using DriveLetterFormat = Dmg.Windows.Volumes.DriveLetter;

namespace Dmg.Windows.Volumes;

/// <summary>
/// A volume that was found to live on the disk dmg attached: its name, where it
/// sits, and where Windows has made it reachable.
/// </summary>
/// <param name="VolumeName">
/// The <c>\\?\Volume{GUID}\</c> name, trailing backslash included - the form
/// <c>SetVolumeMountPointW</c> requires, so that assigning a letter later needs no
/// re-derivation.
/// </param>
/// <param name="Device">Which physical device and partition this volume is.</param>
/// <param name="PathNames">
/// Everywhere Windows says the volume is reachable from: <c>E:\</c>, and any
/// folder mount points. Empty when Windows has mounted it nowhere.
/// </param>
public sealed record VolumeOnDisk(
    string VolumeName,
    StorageDeviceNumber Device,
    IReadOnlyList<string> PathNames)
{
    /// <summary>Where Windows says the volume is reachable from. Never null.</summary>
    public IReadOnlyList<string> PathNames { get; } = PathNames ?? [];

    /// <summary>
    /// The drive letter Windows gave this volume, canonically - <c>E</c> - or null
    /// when it has none.
    /// </summary>
    /// <remarks>
    /// A volume can be reachable from several paths at once: a letter and one or
    /// more folder mount points. Only the letter is of interest, so folder paths
    /// are filtered out rather than mistaken for one - <c>C:\mnt\image\</c> is not
    /// something <c>dmg unmount</c> can be given.
    /// </remarks>
    public string? DriveLetter { get; } = FirstDriveLetter(PathNames);

    /// <summary>True when Windows has already given this volume a drive letter.</summary>
    public bool HasDriveLetter => DriveLetter is not null;

    /// <summary>How the volume reads in a diagnostic line.</summary>
    public override string ToString() =>
        HasDriveLetter ? $"{VolumeName} at {DriveLetter}:" : $"{VolumeName} with no drive letter";

    private static string? FirstDriveLetter(IReadOnlyList<string>? pathNames)
    {
        if (pathNames is null)
        {
            return null;
        }

        foreach (string pathName in pathNames)
        {
            if (DriveLetterFormat.Normalise(pathName) is string letter)
            {
                return letter;
            }
        }

        return null;
    }
}
