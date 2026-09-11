using Dmg.Core;

namespace Dmg.Windows.Volumes;

/// <summary>
/// The real filesystem probe: <see cref="DriveInfo.DriveFormat"/> for the drive
/// letter a mount was given.
/// </summary>
public sealed class DriveVolumeFilesystemProbe : IVolumeFilesystemProbe
{
    /// <summary>The shared instance. The type holds no state.</summary>
    public static DriveVolumeFilesystemProbe Instance { get; } = new();

    /// <inheritdoc />
    public Result<string> Filesystem(string driveLetter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveLetter);

        try
        {
            DriveInfo drive = new(DriveLetter.Root(driveLetter));

            if (!drive.IsReady)
            {
                return Result<string>.Failure(DmgError.Internal(
                    $"Drive {driveLetter}: is not ready.",
                    $"{nameof(DriveInfo)}.{nameof(DriveInfo.IsReady)} was false."));
            }

            return Result<string>.Success(drive.DriveFormat);
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(DmgError.Internal(
                $"The filesystem at {driveLetter}: could not be read.",
                exception.Message));
        }
    }
}
