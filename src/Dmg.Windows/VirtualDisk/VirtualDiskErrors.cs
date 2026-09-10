using Dmg.Core;

namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// Turns the <c>DWORD</c> a <c>virtdisk.dll</c> call returned into a
/// <see cref="DmgError"/>: an exit code a script can branch on, and a sentence a
/// person can act on.
/// </summary>
/// <remarks>
/// <para>
/// A raw HRESULT is not a user-facing error. <c>0x80070005</c> tells someone
/// nothing; "attaching a virtual disk needs an elevated shell" tells them what to
/// do next. This class is where that translation happens, once, so that every call
/// site gets the same wording and the same exit code.
/// </para>
/// <para>
/// It is deliberately pure - a switch over integers, no P/Invoke, no Win32 types -
/// so the whole mapping can be tested on any machine. The parts of the mount path
/// that need Windows are the four native calls; deciding what a failure
/// <em>means</em> is not one of them.
/// </para>
/// <para>
/// The native code is kept in <see cref="DmgError.Detail"/> rather than thrown
/// away: it is what a bug report needs, and it only surfaces at verbose output.
/// </para>
/// </remarks>
public static class VirtualDiskErrors
{
    /// <summary>The call succeeded.</summary>
    public const uint Success = 0;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>.</summary>
    public const uint FileNotFound = 2;

    /// <summary><c>ERROR_PATH_NOT_FOUND</c>.</summary>
    public const uint PathNotFound = 3;

    /// <summary><c>ERROR_ACCESS_DENIED</c>. The elevation case.</summary>
    public const uint AccessDenied = 5;

    /// <summary><c>ERROR_NOT_READY</c>.</summary>
    public const uint NotReady = 21;

    /// <summary><c>ERROR_SHARING_VIOLATION</c>. Something else has the file open.</summary>
    public const uint SharingViolation = 32;

    /// <summary><c>ERROR_NOT_SUPPORTED</c>.</summary>
    public const uint NotSupported = 50;

    /// <summary><c>ERROR_DEV_NOT_EXIST</c>. Returned when the disk is not attached.</summary>
    public const uint DeviceNotExist = 55;

    /// <summary><c>ERROR_INVALID_PARAMETER</c>. We built the parameter block, so this is our bug.</summary>
    public const uint InvalidParameter = 87;

    /// <summary><c>ERROR_DISK_FULL</c>.</summary>
    public const uint DiskFull = 112;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>. Handled internally, never surfaced.</summary>
    public const uint InsufficientBuffer = 122;

    /// <summary><c>ERROR_PRIVILEGE_NOT_HELD</c>. The other elevation case.</summary>
    public const uint PrivilegeNotHeld = 1314;

    /// <summary><c>ERROR_VHD_DRIVE_FOOTER_MISSING</c>.</summary>
    public const uint VhdDriveFooterMissing = 0xC03A_0001;

    /// <summary><c>ERROR_VHD_DRIVE_FOOTER_CHECKSUM_MISMATCH</c>.</summary>
    public const uint VhdDriveFooterChecksumMismatch = 0xC03A_0002;

    /// <summary><c>ERROR_VHD_DRIVE_FOOTER_CORRUPT</c>.</summary>
    public const uint VhdDriveFooterCorrupt = 0xC03A_0003;

    /// <summary><c>ERROR_VHD_FORMAT_UNKNOWN</c>.</summary>
    public const uint VhdFormatUnknown = 0xC03A_0004;

    /// <summary><c>ERROR_VHD_FORMAT_UNSUPPORTED_VERSION</c>.</summary>
    public const uint VhdFormatUnsupportedVersion = 0xC03A_0005;

    /// <summary><c>ERROR_VHD_INVALID_SIZE</c>.</summary>
    public const uint VhdInvalidSize = 0xC03A_0012;

    /// <summary><c>ERROR_VHD_INVALID_FILE_SIZE</c>.</summary>
    public const uint VhdInvalidFileSize = 0xC03A_0013;

    /// <summary><c>ERROR_VIRTDISK_PROVIDER_NOT_FOUND</c>.</summary>
    public const uint VirtDiskProviderNotFound = 0xC03A_0014;

    /// <summary><c>ERROR_VIRTDISK_NOT_VIRTUAL_DISK</c>. Windows does not recognise the file as a VHD.</summary>
    public const uint NotVirtualDisk = 0xC03A_0015;

    /// <summary>The high half of every error code in the virtual-disk facility.</summary>
    private const uint VirtDiskFacilityMask = 0xFFFF_0000;

    /// <summary>Facility 0x3A, <c>FACILITY_VIRTDISK</c>, with the failure and customer bits set.</summary>
    private const uint VirtDiskFacility = 0xC03A_0000;

    /// <summary>
    /// The remedy sentence for every elevation failure. One wording, one place, so
    /// that what the user is told to do never drifts between call sites.
    /// </summary>
    public const string ElevationRemedy =
        "Attaching a virtual disk needs the Manage Volume privilege. Close this window, " +
        "start Windows Terminal or PowerShell with 'Run as administrator', and run the same " +
        "command again. dmg will not raise a UAC prompt on your behalf.";

    /// <summary>True when a <c>virtdisk.dll</c> call reported success.</summary>
    public static bool Succeeded(uint nativeError) => nativeError == Success;

    /// <summary>
    /// Maps a native error to the failure this tool reports.
    /// </summary>
    /// <param name="nativeError">The <c>DWORD</c> the call returned.</param>
    /// <param name="operation">Which call it was.</param>
    /// <param name="vhdPath">The virtual disk the call was about, for the message.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nativeError"/> is <see cref="Success"/>.</exception>
    public static DmgError FromNative(uint nativeError, VirtualDiskOperation operation, string vhdPath)
    {
        if (nativeError == Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeError),
                nativeError,
                "ERROR_SUCCESS does not describe a failure.");
        }

        string detail = $"{NativeCallName(operation)} returned 0x{nativeError:X8} ({NativeErrorName(nativeError)})";

        return nativeError switch
        {
            AccessDenied or PrivilegeNotHeld => new DmgError(
                DmgExitCode.ElevationRequired,
                $"Windows refused access to '{vhdPath}'. {ElevationRemedy}",
                detail),

            FileNotFound => new DmgError(
                DmgExitCode.MountFailed,
                $"There is no file at '{vhdPath}'.",
                detail),

            PathNotFound => new DmgError(
                DmgExitCode.MountFailed,
                $"The folder that should hold '{vhdPath}' does not exist.",
                detail),

            SharingViolation => new DmgError(
                DmgExitCode.MountFailed,
                $"'{vhdPath}' is open in another program. Close whatever is using it and try again.",
                detail),

            NotVirtualDisk => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows does not recognise '{vhdPath}' as a virtual disk, so it cannot be attached.",
                detail),

            VirtDiskProviderNotFound => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows has no virtual disk provider for '{vhdPath}'.",
                detail),

            VhdDriveFooterMissing => new DmgError(
                DmgExitCode.CorruptImage,
                $"'{vhdPath}' has no VHD footer, so Windows will not attach it.",
                detail),

            VhdDriveFooterChecksumMismatch or VhdDriveFooterCorrupt => new DmgError(
                DmgExitCode.CorruptImage,
                $"The VHD footer of '{vhdPath}' is damaged: Windows rejected its checksum.",
                detail),

            VhdFormatUnknown or VhdFormatUnsupportedVersion => new DmgError(
                DmgExitCode.UnsupportedFormat,
                $"'{vhdPath}' is in a virtual disk format this build of Windows will not open.",
                detail),

            VhdInvalidSize or VhdInvalidFileSize => new DmgError(
                DmgExitCode.CorruptImage,
                $"The size recorded in '{vhdPath}' does not match the file on disk.",
                detail),

            DeviceNotExist => new DmgError(
                DmgExitCode.MountFailed,
                $"'{vhdPath}' is not attached, so it has no physical device.",
                detail),

            NotReady => new DmgError(
                DmgExitCode.MountFailed,
                $"The device behind '{vhdPath}' is not ready.",
                detail),

            DiskFull => new DmgError(
                DmgExitCode.InsufficientSpace,
                $"There is not enough room on the volume holding '{vhdPath}'.",
                detail),

            NotSupported => new DmgError(
                DmgExitCode.UnsupportedFormat,
                $"Windows does not support this operation on '{vhdPath}'.",
                detail),

            InvalidParameter => DmgError.Internal(
                $"dmg passed something Windows rejected while working on '{vhdPath}'. This is a bug in dmg.",
                detail),

            _ when (nativeError & VirtDiskFacilityMask) == VirtDiskFacility => new DmgError(
                DmgExitCode.MountFailed,
                $"The Windows virtual disk service refused '{vhdPath}'.",
                detail),

            _ => new DmgError(
                DmgExitCode.MountFailed,
                $"{DescribeOperation(operation)} '{vhdPath}' failed.",
                detail),
        };
    }

    /// <summary>The exported function behind an operation, for diagnostics.</summary>
    public static string NativeCallName(VirtualDiskOperation operation) => operation switch
    {
        VirtualDiskOperation.Open => "OpenVirtualDisk",
        VirtualDiskOperation.Attach => "AttachVirtualDisk",
        VirtualDiskOperation.Detach => "DetachVirtualDisk",
        VirtualDiskOperation.GetPhysicalPath => "GetVirtualDiskPhysicalPath",
        _ => "virtdisk.dll",
    };

    /// <summary>The <c>winerror.h</c> name of a code this tool knows about.</summary>
    public static string NativeErrorName(uint nativeError) => nativeError switch
    {
        Success => "ERROR_SUCCESS",
        FileNotFound => "ERROR_FILE_NOT_FOUND",
        PathNotFound => "ERROR_PATH_NOT_FOUND",
        AccessDenied => "ERROR_ACCESS_DENIED",
        NotReady => "ERROR_NOT_READY",
        SharingViolation => "ERROR_SHARING_VIOLATION",
        NotSupported => "ERROR_NOT_SUPPORTED",
        DeviceNotExist => "ERROR_DEV_NOT_EXIST",
        InvalidParameter => "ERROR_INVALID_PARAMETER",
        DiskFull => "ERROR_DISK_FULL",
        InsufficientBuffer => "ERROR_INSUFFICIENT_BUFFER",
        PrivilegeNotHeld => "ERROR_PRIVILEGE_NOT_HELD",
        VhdDriveFooterMissing => "ERROR_VHD_DRIVE_FOOTER_MISSING",
        VhdDriveFooterChecksumMismatch => "ERROR_VHD_DRIVE_FOOTER_CHECKSUM_MISMATCH",
        VhdDriveFooterCorrupt => "ERROR_VHD_DRIVE_FOOTER_CORRUPT",
        VhdFormatUnknown => "ERROR_VHD_FORMAT_UNKNOWN",
        VhdFormatUnsupportedVersion => "ERROR_VHD_FORMAT_UNSUPPORTED_VERSION",
        VhdInvalidSize => "ERROR_VHD_INVALID_SIZE",
        VhdInvalidFileSize => "ERROR_VHD_INVALID_FILE_SIZE",
        VirtDiskProviderNotFound => "ERROR_VIRTDISK_PROVIDER_NOT_FOUND",
        NotVirtualDisk => "ERROR_VIRTDISK_NOT_VIRTUAL_DISK",
        _ => "unrecognised",
    };

    private static string DescribeOperation(VirtualDiskOperation operation) => operation switch
    {
        VirtualDiskOperation.Open => "Opening",
        VirtualDiskOperation.Attach => "Attaching",
        VirtualDiskOperation.Detach => "Detaching",
        VirtualDiskOperation.GetPhysicalPath => "Locating the device behind",
        _ => "Working on",
    };
}
