using Dmg.Core;

namespace Dmg.Windows.Volumes;

/// <summary>
/// The volume-management calls this tool needs, behind a port that can be faked.
/// </summary>
/// <remarks>
/// <para>
/// Drive-letter discovery is a join, not a lookup: Windows never says "the disk
/// you just attached is E:". It says which device a handle sits on, and it will
/// list every volume on the machine, and it will say where a given volume is
/// mounted. Turning that into a drive letter means matching one against the
/// others, and the interesting failures - a volume that will not open, a disk that
/// carries no volume Windows understands, a volume that exists but has no letter
/// yet - are all decisions about the outcome of these three calls.
/// </para>
/// <para>
/// Behind this interface those decisions run on any machine. Behind three direct
/// P/Invokes they could only be exercised on Windows, by hand, with a real disk
/// attached, and the awkward cases could not be exercised at all.
/// </para>
/// <para>
/// <b>No exceptions and no raw Win32 codes.</b> Implementations return failures as
/// <see cref="Result"/> values carrying a <see cref="DmgError"/> that already has
/// an exit code and a sentence a user can act on. Mapping <c>GetLastError</c> is
/// the implementation's job - see <see cref="VolumeErrors"/>.
/// </para>
/// </remarks>
public interface IVolumeService
{
    /// <summary>
    /// Asks which physical device and partition a device path refers to, via
    /// <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>.
    /// </summary>
    /// <param name="devicePath">
    /// Either a physical device - <c>\\.\PhysicalDrive3</c>, as
    /// <c>GetVirtualDiskPhysicalPath</c> returns it - or a volume name as
    /// <see cref="EnumerateVolumes"/> returns it. Implementations accept the volume
    /// name with its trailing backslash and deal with the fact that
    /// <c>CreateFile</c> will not.
    /// </param>
    /// <returns>
    /// The device identity, or a failure. A failure here for a volume that is not
    /// ours is ordinary - see <see cref="VolumeErrors.IsAboutOneVolume"/>.
    /// </returns>
    Result<StorageDeviceNumber> GetDeviceNumber(string devicePath);

    /// <summary>
    /// Every volume on the machine, as <c>FindFirstVolumeW</c> and
    /// <c>FindNextVolumeW</c> report them.
    /// </summary>
    /// <returns>
    /// Names in the form <c>\\?\Volume{GUID}\</c>, trailing backslash included,
    /// because that is the form <c>GetVolumePathNamesForVolumeNameW</c> and
    /// <c>SetVolumeMountPointW</c> both require.
    /// </returns>
    Result<IReadOnlyList<string>> EnumerateVolumes();

    /// <summary>
    /// Where a volume is reachable from, via
    /// <c>GetVolumePathNamesForVolumeNameW</c>.
    /// </summary>
    /// <param name="volumeName">A name from <see cref="EnumerateVolumes"/>.</param>
    /// <returns>
    /// Paths such as <c>E:\</c> and <c>C:\mnt\image\</c>, or an empty list for a
    /// volume Windows has mounted nowhere. Empty is a normal answer, not a failure:
    /// it is what a disk attached with
    /// <c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c> looks like, and it is what
    /// every volume looks like for a moment after it appears.
    /// </returns>
    Result<IReadOnlyList<string>> GetVolumePathNames(string volumeName);

    /// <summary>
    /// Gives a volume a drive letter, via <c>SetVolumeMountPointW</c>.
    /// </summary>
    /// <remarks>
    /// What <c>--letter</c> needs (S8.5). The disk behind <paramref name="volumeName"/>
    /// was attached with <c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c> precisely so
    /// that no automatic assignment could race this call.
    /// </remarks>
    /// <param name="volumeName">A name from <see cref="EnumerateVolumes"/> - the volume to give the letter to.</param>
    /// <param name="driveLetter">
    /// The canonical letter - <c>E</c>, not <c>E:</c> or <c>E:\</c> - as
    /// <see cref="DriveLetter.Parse"/> produces it.
    /// </param>
    /// <returns>
    /// Success once the letter is assigned, or a failure - most commonly that the
    /// letter is already taken by another volume, which is reported clearly rather
    /// than as a bare Win32 code.
    /// </returns>
    Result AssignDriveLetter(string volumeName, string driveLetter);
}
