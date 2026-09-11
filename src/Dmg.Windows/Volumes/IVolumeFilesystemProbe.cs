using Dmg.Core;

namespace Dmg.Windows.Volumes;

/// <summary>
/// Asks what filesystem is mounted at a drive letter, behind a port that can be
/// faked.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="Dmg.Core.Vhd.IFreeSpaceProbe"/>: <c>dmg list</c>
/// needs a filesystem name for each mount, and there is no way to make a real drive
/// letter carry exFAT on demand in a test. <see cref="DriveVolumeFilesystemProbe"/>
/// is the production implementation, built over <see cref="DriveInfo"/> - portable
/// enough to compile and run on the Mac this is developed on, even though the
/// answer is only meaningful once a real drive letter exists on Windows.
/// </remarks>
public interface IVolumeFilesystemProbe
{
    /// <summary>
    /// The filesystem mounted at a drive letter - <c>NTFS</c>, <c>FAT32</c>,
    /// <c>exFAT</c> - or a failure when the drive is not ready or cannot be read.
    /// </summary>
    /// <param name="driveLetter">
    /// A canonical drive letter, as <see cref="DriveLetter.Normalise"/> produces -
    /// a single upper-case letter, no colon.
    /// </param>
    Result<string> Filesystem(string driveLetter);
}
