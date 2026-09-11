using Dmg.Core;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IVolumeService"/> that behaves the way Windows' volume
/// APIs behave, written by hand.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written for the same reason <c>FakeVirtualDiskService</c> is: a mocking
/// framework would be a third-party package, and it could only replay what a test
/// told it to - it could not enforce that a volume must exist before its path names
/// can be read, or that a mount point already claimed by one volume cannot be
/// claimed by another. Those are exactly the rules <c>DriveLetterDiscovery</c> has
/// to get right.
/// </para>
/// <para>
/// Everything the real service can fail to do, this one can be told to fail too, by
/// assigning a <see cref="DmgError"/> to one of the failure properties, or per
/// volume via <see cref="FakeVolume.PathNamesFailure"/>.
/// </para>
/// </remarks>
public sealed class FakeVolumeService : IVolumeService
{
    private readonly Dictionary<string, StorageDeviceNumber> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DmgError> _deviceFailures = new(StringComparer.Ordinal);
    private readonly List<FakeVolume> _volumes = [];
    private readonly List<string> _calls = [];

    /// <summary>Every call made through this service, in order, for tests that care about sequence.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Every volume this fake knows about.</summary>
    public IReadOnlyList<FakeVolume> Volumes => _volumes;

    /// <summary>When set, every <see cref="EnumerateVolumes"/> fails with this error.</summary>
    public DmgError? EnumerateVolumesFailure { get; set; }

    /// <summary>When set, every <see cref="AssignDriveLetter"/> fails with this error.</summary>
    public DmgError? AssignDriveLetterFailure { get; set; }

    /// <summary>
    /// Tells the fake what device a path - physical or volume - reports for
    /// <see cref="GetDeviceNumber"/>.
    /// </summary>
    public void SetDevice(string devicePath, StorageDeviceNumber device) => _devices[devicePath] = device;

    /// <summary>
    /// Makes <see cref="GetDeviceNumber"/> fail for one path, the way a locked
    /// device or an empty card reader would - a failure about that one device, not
    /// about the machine.
    /// </summary>
    public void FailDeviceNumber(string devicePath, DmgError error) => _deviceFailures[devicePath] = error;

    /// <summary>
    /// Adds a volume Windows knows about: which device it lives on, and everywhere
    /// it is currently reachable from. A volume added with no path names is what
    /// <c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c> looks like, or what every
    /// volume looks like for a moment after it appears.
    /// </summary>
    public FakeVolume AddVolume(string volumeName, StorageDeviceNumber device, params string[] pathNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        FakeVolume volume = new(volumeName, pathNames);
        _devices[volumeName] = device;
        _volumes.Add(volume);

        return volume;
    }

    /// <inheritdoc />
    public Result<StorageDeviceNumber> GetDeviceNumber(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Record($"GetDeviceNumber({devicePath})");

        if (_deviceFailures.TryGetValue(devicePath, out DmgError? failure))
        {
            return Result<StorageDeviceNumber>.Failure(failure);
        }

        if (_devices.TryGetValue(devicePath, out StorageDeviceNumber? device))
        {
            return Result<StorageDeviceNumber>.Success(device);
        }

        return Result<StorageDeviceNumber>.Failure(
            DmgExitCode.MountFailed,
            $"'{devicePath}' has no device behind it.",
            "fake: unknown device path");
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<string>> EnumerateVolumes()
    {
        Record("EnumerateVolumes()");

        if (EnumerateVolumesFailure is not null)
        {
            return Result<IReadOnlyList<string>>.Failure(EnumerateVolumesFailure);
        }

        return Result<IReadOnlyList<string>>.Success(_volumes.Select(volume => volume.VolumeName).ToList());
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<string>> GetVolumePathNames(string volumeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);
        Record($"GetVolumePathNames({volumeName})");

        FakeVolume? volume = Find(volumeName);

        if (volume is null)
        {
            return Result<IReadOnlyList<string>>.Failure(
                DmgExitCode.MountFailed,
                $"'{volumeName}' is not a volume this fake knows about.",
                "fake: unknown volume");
        }

        if (volume.PathNamesFailure is not null)
        {
            return Result<IReadOnlyList<string>>.Failure(volume.PathNamesFailure);
        }

        return Result<IReadOnlyList<string>>.Success(volume.PathNames);
    }

    /// <inheritdoc />
    public Result AssignDriveLetter(string volumeName, string driveLetter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);
        Record($"AssignDriveLetter({volumeName}, {driveLetter})");

        if (AssignDriveLetterFailure is not null)
        {
            return Result.Failure(AssignDriveLetterFailure);
        }

        FakeVolume? volume = Find(volumeName);

        if (volume is null)
        {
            return Result.Failure(
                DmgExitCode.MountFailed,
                $"'{volumeName}' is not a volume this fake knows about.",
                "fake: unknown volume");
        }

        string root = DriveLetter.Root(driveLetter);

        if (_volumes.Any(other => other != volume && other.PathNames.Contains(root)))
        {
            return Result.Failure(VolumeErrors.FromNative(
                VolumeErrors.DirectoryNotEmpty, VolumeOperation.SetMountPoint, root));
        }

        volume.AddPathName(root);

        return Result.Success();
    }

    private FakeVolume? Find(string volumeName) =>
        _volumes.FirstOrDefault(volume => volume.VolumeName == volumeName);

    private void Record(string call) => _calls.Add(call);
}

/// <summary>The state the fake keeps for one volume.</summary>
public sealed class FakeVolume
{
    private readonly List<string> _pathNames;

    internal FakeVolume(string volumeName, IEnumerable<string> pathNames)
    {
        VolumeName = volumeName;
        _pathNames = [.. pathNames];
    }

    /// <summary>The <c>\\?\Volume{GUID}\</c> name this volume was added under.</summary>
    public string VolumeName { get; }

    /// <summary>
    /// Everywhere the volume is currently reachable from. Grows when
    /// <see cref="FakeVolumeService.AssignDriveLetter"/> succeeds against this
    /// volume.
    /// </summary>
    public IReadOnlyList<string> PathNames => _pathNames;

    /// <summary>When set, reading this volume's path names fails with this error.</summary>
    public DmgError? PathNamesFailure { get; set; }

    internal void AddPathName(string pathName) => _pathNames.Add(pathName);
}
