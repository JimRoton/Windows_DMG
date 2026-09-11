using Dmg.Core;
using Dmg.Core.Vhd;
using Dmg.Windows.Elevation;
using Dmg.Windows.VirtualDisk;
using Dmg.Windows.Volumes;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// Small, hand-written substitutes for <see cref="MountCommandTests"/>.
/// </summary>
/// <remarks>
/// Not the rigorous fakes <c>Dmg.Windows.Tests</c> uses to pin down
/// <c>DriveLetterDiscovery</c> and the elevation check themselves - those live in a
/// different test project this one does not reference, and duplicating their full
/// contract enforcement here would test the fakes more than the command. These only
/// need to play back what <see cref="MountCommand"/> asks of them, in the order it
/// asks, so the CLI-level wiring - argument parsing, ordering, cleanup on failure -
/// can be exercised without a Windows box.
/// </remarks>
internal sealed class FakeMountVirtualDiskService : IVirtualDiskService
{
    private readonly List<string> _calls = [];

    public DmgError? OpenFailure { get; set; }

    public DmgError? AttachFailure { get; set; }

    public DmgError? DetachFailure { get; set; }

    public string PhysicalPath { get; set; } = @"\\.\PhysicalDrive9";

    public IReadOnlyList<string> Calls => _calls;

    public Result<IVirtualDiskHandle> Open(string vhdPath, VirtualDiskAccessMode mode)
    {
        _calls.Add($"Open({mode})");

        if (OpenFailure is not null)
        {
            return Result<IVirtualDiskHandle>.Failure(OpenFailure);
        }

        if (!File.Exists(vhdPath))
        {
            return Result<IVirtualDiskHandle>.Failure(
                DmgExitCode.MountFailed, $"fake: '{vhdPath}' does not exist");
        }

        return Result<IVirtualDiskHandle>.Success(new FakeHandle(vhdPath, mode));
    }

    public Result<VirtualDiskAttachment> Attach(IVirtualDiskHandle handle, VirtualDiskAttachOptions options)
    {
        _calls.Add($"Attach(permanent={options.PermanentLifetime}, noDriveLetter={options.NoDriveLetter})");

        if (AttachFailure is not null)
        {
            return Result<VirtualDiskAttachment>.Failure(AttachFailure);
        }

        return Result<VirtualDiskAttachment>.Success(
            new VirtualDiskAttachment(handle.VhdPath, PhysicalPath, handle.Mode, options.PermanentLifetime));
    }

    public Result Detach(IVirtualDiskHandle handle)
    {
        _calls.Add("Detach()");

        return DetachFailure is null ? Result.Success() : Result.Failure(DetachFailure);
    }

    public Result<string> GetPhysicalPath(IVirtualDiskHandle handle) => Result<string>.Success(PhysicalPath);

    private sealed class FakeHandle(string vhdPath, VirtualDiskAccessMode mode) : IVirtualDiskHandle
    {
        public string VhdPath { get; } = vhdPath;

        public VirtualDiskAccessMode Mode { get; } = mode;

        public bool IsOpen { get; private set; } = true;

        public void Dispose() => IsOpen = false;
    }
}

/// <summary>A process token with one privilege to give or withhold: Manage Volume.</summary>
internal sealed class FakeMountPrivilegeService : IPrivilegeService
{
    private PrivilegeState _manageVolume = PrivilegeState.Absent;

    public static FakeMountPrivilegeService Elevated() =>
        new() { _manageVolume = PrivilegeState.Enabled };

    public static FakeMountPrivilegeService Unelevated() => new();

    public Result<PrivilegeState> Query(string privilegeName) =>
        Result<PrivilegeState>.Success(
            privilegeName == WindowsPrivilege.ManageVolume ? _manageVolume : PrivilegeState.Absent);
}

/// <summary>
/// One volume on one device, already present from the first look - so
/// <c>DriveLetterDiscovery</c>'s retry loop never actually waits in a test.
/// </summary>
internal sealed class FakeMountVolumeService : IVolumeService
{
    private const string VolumeName = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private static readonly StorageDeviceNumber Device = new(StorageDeviceNumber.Disk, 9, 0);
    private static readonly StorageDeviceNumber DevicePartition = new(StorageDeviceNumber.Disk, 9, 1);

    private readonly List<string> _pathNames = [];

    public DmgError? AssignDriveLetterFailure { get; set; }

    /// <summary>Pre-assigns a drive letter, as if Windows had already surfaced it.</summary>
    public FakeMountVolumeService WithDriveLetter(string letter)
    {
        _pathNames.Add(DriveLetter.Root(letter));

        return this;
    }

    public Result<StorageDeviceNumber> GetDeviceNumber(string devicePath) =>
        devicePath switch
        {
            @"\\.\PhysicalDrive9" => Result<StorageDeviceNumber>.Success(Device),
            VolumeName => Result<StorageDeviceNumber>.Success(DevicePartition),
            _ => Result<StorageDeviceNumber>.Failure(
                DmgExitCode.MountFailed, $"fake: no device for '{devicePath}'"),
        };

    public Result<IReadOnlyList<string>> EnumerateVolumes() =>
        Result<IReadOnlyList<string>>.Success([VolumeName]);

    public Result<IReadOnlyList<string>> GetVolumePathNames(string volumeName) =>
        volumeName == VolumeName
            ? Result<IReadOnlyList<string>>.Success(_pathNames)
            : Result<IReadOnlyList<string>>.Failure(DmgExitCode.MountFailed, "fake: unknown volume");

    public Result AssignDriveLetter(string volumeName, string driveLetter)
    {
        if (AssignDriveLetterFailure is not null)
        {
            return Result.Failure(AssignDriveLetterFailure);
        }

        _pathNames.Add(DriveLetter.Root(driveLetter));

        return Result.Success();
    }
}

/// <summary>Reports a fixed number of bytes free, for the precheck's failure path.</summary>
internal sealed class FakeFreeSpaceProbe(long availableBytes) : IFreeSpaceProbe
{
    public Result<long> AvailableBytes(string path) => Result<long>.Success(availableBytes);
}
