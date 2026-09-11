namespace Dmg.Windows.Volumes;

/// <summary>
/// What <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c> answers: which physical device this
/// handle sits on, and which partition of it.
/// </summary>
/// <remarks>
/// <para>
/// This is the identity that makes drive-letter discovery correct. Windows offers
/// no way to ask "what letter did the disk I just attached get" - the disk and the
/// volumes on it are separate objects, connected only by the device number they
/// report. Asking every volume on the machine for this triple and keeping the ones
/// that match our disk is the join.
/// </para>
/// <para>
/// <b>The device type is part of the identity, not decoration.</b> Device numbers
/// are only unique within a device type: a CD-ROM and a disk can both be device 1.
/// Matching on the number alone would occasionally pick a volume on somebody
/// else's hardware, and the symptom - a drive letter reported for the wrong disk -
/// would be nearly impossible to reproduce.
/// </para>
/// </remarks>
/// <param name="DeviceType">
/// The <c>FILE_DEVICE_*</c> class of the device. <see cref="Disk"/> for anything
/// this tool attaches.
/// </param>
/// <param name="DeviceNumber">
/// The number in <c>\\.\PhysicalDriveN</c>, unique among devices of the same type.
/// </param>
/// <param name="PartitionNumber">
/// Which partition of that device, counting from 1. <see cref="WholeDevice"/> when
/// the handle refers to the device itself rather than a partition on it.
/// </param>
public sealed record StorageDeviceNumber(uint DeviceType, uint DeviceNumber, int PartitionNumber)
{
    /// <summary><c>FILE_DEVICE_DISK</c>. What an attached VHD reports.</summary>
    public const uint Disk = 0x0000_0007;

    /// <summary><c>FILE_DEVICE_CD_ROM</c>. Present here only so tests can be about the wrong type.</summary>
    public const uint CdRom = 0x0000_0002;

    /// <summary>
    /// The partition number reported for a handle on the whole device. Windows
    /// documents this field as unsigned and then writes 0 or -1 into it, which is
    /// why it is read as a signed integer here.
    /// </summary>
    public const int WholeDevice = 0;

    /// <summary>True when this handle refers to a partition rather than the whole device.</summary>
    public bool IsPartition => PartitionNumber > WholeDevice;

    /// <summary>
    /// True when <paramref name="other"/> sits on the same physical device as this
    /// one - the same type and the same number, regardless of partition.
    /// </summary>
    /// <remarks>
    /// The whole point of the type. A volume "is on our disk" exactly when this is
    /// true of the disk's number and the volume's.
    /// </remarks>
    public bool IsSameDeviceAs(StorageDeviceNumber? other) =>
        other is not null && other.DeviceType == DeviceType && other.DeviceNumber == DeviceNumber;

    /// <summary>How the device reads in a diagnostic line.</summary>
    public override string ToString() =>
        IsPartition
            ? $@"\\.\PhysicalDrive{DeviceNumber} partition {PartitionNumber} (type {DeviceType})"
            : $@"\\.\PhysicalDrive{DeviceNumber} (type {DeviceType})";
}
