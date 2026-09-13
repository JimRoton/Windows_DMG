using Dmg.Core;

namespace Dmg.Windows.Projection;

/// <summary>Which ProjFS call a failure came from, for the detail line.</summary>
public enum ProjectionOperation
{
    /// <summary><c>PrjMarkDirectoryAsPlaceholder</c>.</summary>
    MarkDirectory,

    /// <summary><c>PrjStartVirtualizing</c>.</summary>
    StartVirtualizing,

    /// <summary><c>PrjStopVirtualizing</c>.</summary>
    StopVirtualizing,

    /// <summary><c>PrjWritePlaceholderInfo</c>.</summary>
    WritePlaceholderInfo,

    /// <summary><c>PrjWriteFileData</c>.</summary>
    WriteFileData,

    /// <summary><c>PrjFillDirEntryBuffer</c>.</summary>
    FillDirEntryBuffer,
}

/// <summary>
/// Turns the <c>HRESULT</c> a <c>ProjectedFSLib.dll</c> call returned into a
/// <see cref="DmgError"/>: an exit code a script can branch on, and a sentence a
/// person can act on.
/// </summary>
/// <remarks>
/// <para>
/// The same argument as <see cref="VirtualDisk.VirtualDiskErrors"/>, for the same
/// reason: <c>0x80070002</c> tells a user nothing, and "the Windows Projected File
/// System is switched off, here is how to turn it on" tells them everything. The
/// translation happens once, here, so every call site says the same thing.
/// </para>
/// <para>
/// Deliberately pure - a switch over integers, no P/Invoke, no Win32 types - so
/// the whole mapping is tested on any machine. That matters more here than it did
/// for the VHD route: the ProjFS calls themselves cannot be exercised anywhere but
/// Windows, so the part that <em>can</em> be tested elsewhere is worth keeping
/// strictly separable.
/// </para>
/// <para>
/// <b>The feature being off is the case to get right.</b> ProjFS is an optional
/// Windows component and is not enabled by default on client editions, so the
/// most likely first experience of this verb is a machine that cannot run it. That
/// must arrive as an instruction, not as a missing-DLL stack trace.
/// </para>
/// </remarks>
public static class ProjectionErrors
{
    /// <summary><c>S_OK</c>.</summary>
    public const int Success = 0;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)</c>.</summary>
    public const int FileNotFound = unchecked((int)0x80070002);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)</c>.</summary>
    public const int PathNotFound = unchecked((int)0x80070003);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)</c>.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)</c>. The volume cannot host a projection.</summary>
    public const int NotSupported = unchecked((int)0x80070032);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_INVALID_PARAMETER)</c>. We built the block, so this is our bug.</summary>
    public const int InvalidParameter = unchecked((int)0x80070057);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY)</c>.</summary>
    public const int DirectoryNotEmpty = unchecked((int)0x80070091);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)</c>.</summary>
    public const int AlreadyExists = unchecked((int)0x800700B7);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_REPARSE_POINT_ENCOUNTERED)</c>: already a projection root.</summary>
    public const int ReparsePointEncountered = unchecked((int)0x80071126);

    /// <summary><c>E_OUTOFMEMORY</c>.</summary>
    public const int OutOfMemory = unchecked((int)0x8007000E);

    /// <summary>
    /// The remedy sentence for a machine whose ProjFS feature is not enabled. One
    /// wording, one place, so that what the user is told never drifts between the
    /// availability check and a call that failed for the same reason.
    /// </summary>
    public const string FeatureRemedy =
        "Projecting an image needs the Windows Projected File System, which is an optional "
        + "Windows feature and is switched off by default. Turn it on from an elevated "
        + "PowerShell with 'Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS "
        + "-NoRestart', then run the same command again. Nothing else needs installing.";

    /// <summary>True when a ProjFS call reported success.</summary>
    public static bool Succeeded(int hresult) => hresult >= 0;

    /// <summary>
    /// The failure to report when ProjFS is not present on this machine at all.
    /// </summary>
    /// <param name="detail">What made that apparent - a missing DLL, a missing export.</param>
    /// <remarks>
    /// <see cref="DmgExitCode.UnsupportedFormat"/> rather than a mount failure: the
    /// image is fine and dmg is fine, this machine simply cannot do it yet. That is
    /// the same code HFS+ gets for the same reason.
    /// </remarks>
    public static DmgError FeatureUnavailable(string detail) => new(
        DmgExitCode.UnsupportedFormat,
        FeatureRemedy,
        detail);

    /// <summary>
    /// Maps a native <c>HRESULT</c> to the failure this tool reports.
    /// </summary>
    /// <param name="hresult">The <c>HRESULT</c> the call returned.</param>
    /// <param name="operation">Which call it was.</param>
    /// <param name="rootPath">The projection root the call was about, for the message.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hresult"/> is a success code.</exception>
    public static DmgError FromNative(int hresult, ProjectionOperation operation, string rootPath)
    {
        if (Succeeded(hresult))
        {
            throw new ArgumentOutOfRangeException(
                nameof(hresult),
                hresult,
                "A success HRESULT does not describe a failure.");
        }

        string detail = $"{NativeCallName(operation)} returned 0x{hresult:X8}";

        return hresult switch
        {
            AccessDenied => new DmgError(
                DmgExitCode.MountFailed,
                $"Windows refused access to '{rootPath}'. A projection root must be a folder you "
                + "can write to; try one under your own profile.",
                detail),

            PathNotFound or FileNotFound => new DmgError(
                DmgExitCode.MountFailed,
                $"The folder '{rootPath}' does not exist and could not be created.",
                detail),

            DirectoryNotEmpty or AlreadyExists => new DmgError(
                DmgExitCode.UsageError,
                $"'{rootPath}' already has files in it. A projection root must be empty, so that "
                + "everything appearing there comes from the image.",
                detail),

            ReparsePointEncountered => new DmgError(
                DmgExitCode.UsageError,
                $"'{rootPath}' is already a projection root. Stop the projection using it, or "
                + "choose another folder.",
                detail),

            NotSupported => new DmgError(
                DmgExitCode.UnsupportedFormat,
                $"The volume holding '{rootPath}' cannot host a projection. ProjFS needs a local "
                + "NTFS volume; a network drive, a substituted drive or a non-NTFS filesystem "
                + "will not do.",
                detail),

            OutOfMemory => new DmgError(
                DmgExitCode.InternalError,
                "Windows ran out of memory setting up the projection.",
                detail),

            InvalidParameter => new DmgError(
                DmgExitCode.InternalError,
                "Windows rejected the projection parameters, which is a bug in dmg. Please report "
                + "it with the detail below.",
                detail),

            _ => new DmgError(
                DmgExitCode.MountFailed,
                $"The Windows Projected File System refused '{rootPath}'.",
                detail),
        };
    }

    /// <summary>The C name of the call, for the detail line.</summary>
    private static string NativeCallName(ProjectionOperation operation) => operation switch
    {
        ProjectionOperation.MarkDirectory => "PrjMarkDirectoryAsPlaceholder",
        ProjectionOperation.StartVirtualizing => "PrjStartVirtualizing",
        ProjectionOperation.StopVirtualizing => "PrjStopVirtualizing",
        ProjectionOperation.WritePlaceholderInfo => "PrjWritePlaceholderInfo",
        ProjectionOperation.WriteFileData => "PrjWriteFileData",
        ProjectionOperation.FillDirEntryBuffer => "PrjFillDirEntryBuffer",
        _ => "ProjectedFSLib",
    };
}
