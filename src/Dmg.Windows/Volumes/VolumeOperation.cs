namespace Dmg.Windows.Volumes;

/// <summary>
/// Which volume-management call produced an error, so a message can say what was
/// being attempted rather than only what went wrong.
/// </summary>
public enum VolumeOperation
{
    /// <summary><c>CreateFileW</c> on a device or volume path.</summary>
    OpenDevice,

    /// <summary><c>DeviceIoControl</c> with <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>.</summary>
    GetDeviceNumber,

    /// <summary><c>FindFirstVolumeW</c> / <c>FindNextVolumeW</c>.</summary>
    EnumerateVolumes,

    /// <summary><c>GetVolumePathNamesForVolumeNameW</c>.</summary>
    GetVolumePathNames,

    /// <summary><c>SetVolumeMountPointW</c>.</summary>
    SetMountPoint,
}
