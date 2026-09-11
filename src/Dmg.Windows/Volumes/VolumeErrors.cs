using Dmg.Core;

namespace Dmg.Windows.Volumes;

/// <summary>
/// Turns the <c>DWORD</c> a volume-management call left in <c>GetLastError</c>
/// into a <see cref="DmgError"/>: an exit code a script can branch on and a
/// sentence a person can act on.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <see cref="Dmg.Windows.VirtualDisk.VirtualDiskErrors"/>, and
/// separate from it on purpose: the same numbers mean different things here.
/// <c>ERROR_ACCESS_DENIED</c> from <c>AttachVirtualDisk</c> means "you are not
/// elevated"; from <c>CreateFile</c> on a volume it usually means something else
/// has the volume open. Folding the two together would send users to run as
/// administrator over a problem elevation cannot fix.
/// </para>
/// <para>
/// Pure arithmetic over integers - no P/Invoke, no Win32 types - so the whole
/// mapping is testable on any machine, which is the only way it gets tested at
/// all: provoking <c>ERROR_UNRECOGNIZED_VOLUME</c> on demand from a real disk is
/// not something a test suite can do.
/// </para>
/// </remarks>
public static class VolumeErrors
{
    /// <summary><c>ERROR_SUCCESS</c>.</summary>
    public const uint Success = 0;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>.</summary>
    public const uint FileNotFound = 2;

    /// <summary><c>ERROR_PATH_NOT_FOUND</c>.</summary>
    public const uint PathNotFound = 3;

    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    public const uint AccessDenied = 5;

    /// <summary><c>ERROR_INVALID_HANDLE</c>.</summary>
    public const uint InvalidHandle = 6;

    /// <summary><c>ERROR_INVALID_DRIVE</c>.</summary>
    public const uint InvalidDrive = 15;

    /// <summary><c>ERROR_NO_MORE_FILES</c>. How volume enumeration says it is finished.</summary>
    public const uint NoMoreFiles = 18;

    /// <summary><c>ERROR_NOT_READY</c>. A drive with no media in it.</summary>
    public const uint NotReady = 21;

    /// <summary><c>ERROR_SHARING_VIOLATION</c>.</summary>
    public const uint SharingViolation = 32;

    /// <summary><c>ERROR_INVALID_PARAMETER</c>. We built the argument, so this is our bug.</summary>
    public const uint InvalidParameter = 87;

    /// <summary><c>ERROR_INVALID_NAME</c>.</summary>
    public const uint InvalidName = 123;

    /// <summary><c>ERROR_DIR_NOT_EMPTY</c>. What a taken drive letter often looks like.</summary>
    public const uint DirectoryNotEmpty = 145;

    /// <summary><c>ERROR_MORE_DATA</c>. Handled by growing the buffer, never surfaced.</summary>
    public const uint MoreData = 234;

    /// <summary><c>ERROR_UNRECOGNIZED_VOLUME</c>. Windows has no driver for this filesystem.</summary>
    public const uint UnrecognizedVolume = 1005;

    /// <summary><c>ERROR_FILE_INVALID</c>. The volume went away underneath us.</summary>
    public const uint FileInvalid = 1006;

    /// <summary><c>ERROR_NOT_A_REPARSE_POINT</c>.</summary>
    public const uint NotAReparsePoint = 4390;

    /// <summary>True when a call reported success.</summary>
    public static bool Succeeded(uint nativeError) => nativeError == Success;

    /// <summary>True when enumeration has simply run out of volumes.</summary>
    public static bool IsEndOfEnumeration(uint nativeError) => nativeError == NoMoreFiles;

    /// <summary>
    /// True when a failure is about one volume rather than about the machine.
    /// </summary>
    /// <remarks>
    /// Volume enumeration walks everything attached, including a card reader with
    /// no card and a BitLocker volume nobody has unlocked. Those refuse to be
    /// opened, and they are not our disk. Discovery skips them rather than failing,
    /// because the alternative is <c>dmg mount</c> refusing to work on a machine
    /// with an empty SD slot in it.
    /// </remarks>
    public static bool IsAboutOneVolume(uint nativeError) => nativeError
        is AccessDenied
        or NotReady
        or SharingViolation
        or FileNotFound
        or PathNotFound
        or InvalidHandle
        or InvalidDrive
        or FileInvalid
        or UnrecognizedVolume
        or NotAReparsePoint;

    /// <summary>
    /// Maps a native error to the failure this tool reports.
    /// </summary>
    /// <param name="nativeError">The code from <c>GetLastError</c>.</param>
    /// <param name="operation">Which call it was.</param>
    /// <param name="subject">The device, volume or letter the call was about.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nativeError"/> is <see cref="Success"/>.</exception>
    public static DmgError FromNative(uint nativeError, VolumeOperation operation, string subject)
    {
        if (nativeError == Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeError),
                nativeError,
                "ERROR_SUCCESS does not describe a failure.");
        }

        string detail = $"{NativeCallName(operation)} on '{subject}' failed with "
            + $"{nativeError} ({NativeErrorName(nativeError)})";

        return nativeError switch
        {
            AccessDenied => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows refused access to '{subject}' while working out which drive letter the "
                + "mounted image was given.",
                detail),

            FileNotFound or PathNotFound or InvalidDrive => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows no longer has a device at '{subject}'. The disk was detached while dmg "
                + "was looking at it.",
                detail),

            NotReady => new DmgError(
                DmgExitCode.MountFailed,
                $"The device at '{subject}' is not ready.",
                detail),

            SharingViolation => new DmgError(
                DmgExitCode.MountFailed,
                $"'{subject}' is in use by another program.",
                detail),

            UnrecognizedVolume => new DmgError(
                DmgExitCode.FilesystemNotMountable,
                $"Windows does not recognise the filesystem on '{subject}'.",
                detail),

            DirectoryNotEmpty => new DmgError(
                DmgExitCode.MountFailed,
                $"'{subject}' is already in use. Pick a different --letter, or leave it out to let "
                + "Windows assign one.",
                detail),

            InvalidName => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows rejected '{subject}' as a name.",
                detail),

            InvalidParameter => DmgError.Internal(
                $"dmg passed something Windows rejected while working on '{subject}'. This is a bug in dmg.",
                detail),

            _ => new DmgError(
                DmgExitCode.MountFailed,
                $"{DescribeOperation(operation)} '{subject}' failed.",
                detail),
        };
    }

    /// <summary>The exported function behind an operation, for diagnostics.</summary>
    public static string NativeCallName(VolumeOperation operation) => operation switch
    {
        VolumeOperation.OpenDevice => "CreateFileW",
        VolumeOperation.GetDeviceNumber => "IOCTL_STORAGE_GET_DEVICE_NUMBER",
        VolumeOperation.EnumerateVolumes => "FindFirstVolumeW",
        VolumeOperation.GetVolumePathNames => "GetVolumePathNamesForVolumeNameW",
        VolumeOperation.SetMountPoint => "SetVolumeMountPointW",
        _ => "kernel32.dll",
    };

    /// <summary>The <c>winerror.h</c> name of a code this tool knows about.</summary>
    public static string NativeErrorName(uint nativeError) => nativeError switch
    {
        Success => "ERROR_SUCCESS",
        FileNotFound => "ERROR_FILE_NOT_FOUND",
        PathNotFound => "ERROR_PATH_NOT_FOUND",
        AccessDenied => "ERROR_ACCESS_DENIED",
        InvalidHandle => "ERROR_INVALID_HANDLE",
        InvalidDrive => "ERROR_INVALID_DRIVE",
        NoMoreFiles => "ERROR_NO_MORE_FILES",
        NotReady => "ERROR_NOT_READY",
        SharingViolation => "ERROR_SHARING_VIOLATION",
        InvalidParameter => "ERROR_INVALID_PARAMETER",
        InvalidName => "ERROR_INVALID_NAME",
        DirectoryNotEmpty => "ERROR_DIR_NOT_EMPTY",
        MoreData => "ERROR_MORE_DATA",
        UnrecognizedVolume => "ERROR_UNRECOGNIZED_VOLUME",
        FileInvalid => "ERROR_FILE_INVALID",
        NotAReparsePoint => "ERROR_NOT_A_REPARSE_POINT",
        _ => "unrecognised",
    };

    private static string DescribeOperation(VolumeOperation operation) => operation switch
    {
        VolumeOperation.OpenDevice => "Opening",
        VolumeOperation.GetDeviceNumber => "Asking Windows which device backs",
        VolumeOperation.EnumerateVolumes => "Listing the volumes on",
        VolumeOperation.GetVolumePathNames => "Asking Windows where Windows mounted",
        VolumeOperation.SetMountPoint => "Assigning a drive letter to",
        _ => "Working on",
    };
}
