using Dmg.Core;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IVirtualDiskService"/> that behaves the way
/// <c>virtdisk.dll</c> behaves, written by hand.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written on purpose. A mocking framework would be a third-party package,
/// and shipping code in this repository has none; more to the point, a mock that
/// only replays whatever the test told it to replay cannot enforce the awkward
/// parts of the real contract - that a handle can be attached once, that a
/// non-permanent attach evaporates when the handle closes, that asking for the
/// physical path of a detached disk fails. Those are exactly the rules the mount
/// sequence has to get right, so the fake enforces them.
/// </para>
/// <para>
/// Everything the real service can refuse to do, this one can be told to refuse
/// too, by assigning a <see cref="DmgError"/> to one of the failure properties.
/// </para>
/// </remarks>
public sealed class FakeVirtualDiskService : IVirtualDiskService
{
    private readonly Dictionary<string, FakeVirtualDisk> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = [];

    private int _nextPhysicalDrive;

    /// <summary>Every call made through this service, in order, for tests that care about sequence.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Every disk this fake knows about, attached or not.</summary>
    public IReadOnlyCollection<FakeVirtualDisk> Disks => _disks.Values;

    /// <summary>The disks currently attached.</summary>
    public IEnumerable<FakeVirtualDisk> AttachedDisks => _disks.Values.Where(disk => disk.IsAttached);

    /// <summary>When set, every <see cref="Open"/> fails with this error.</summary>
    public DmgError? OpenFailure { get; set; }

    /// <summary>When set, every <see cref="Attach"/> fails with this error.</summary>
    public DmgError? AttachFailure { get; set; }

    /// <summary>When set, every <see cref="Detach"/> fails with this error.</summary>
    public DmgError? DetachFailure { get; set; }

    /// <summary>When set, every <see cref="GetPhysicalPath"/> fails with this error.</summary>
    public DmgError? PhysicalPathFailure { get; set; }

    /// <summary>
    /// Tells the fake that a <c>.vhd</c> exists at <paramref name="vhdPath"/>. A path
    /// that was never added behaves like a file that is not there.
    /// </summary>
    /// <param name="vhdPath">The path the code under test will pass to <see cref="Open"/>.</param>
    /// <param name="physicalPath">
    /// The device the disk becomes when attached. Defaults to the next
    /// <c>\\.\PhysicalDriveN</c>, starting at 1 - drive 0 is the boot disk and a test
    /// that produces it is almost certainly testing the wrong thing.
    /// </param>
    public FakeVirtualDisk AddDisk(string vhdPath, string? physicalPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);

        FakeVirtualDisk disk = new(vhdPath, physicalPath ?? NextPhysicalPath());
        _disks[vhdPath] = disk;

        return disk;
    }

    /// <summary>The state of a disk previously handed to <see cref="AddDisk"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such disk was added.</exception>
    public FakeVirtualDisk Disk(string vhdPath) => _disks[vhdPath];

    /// <inheritdoc />
    public Result<IVirtualDiskHandle> Open(string vhdPath, VirtualDiskAccessMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);
        Record($"Open({vhdPath}, {mode})");

        if (OpenFailure is not null)
        {
            return Result<IVirtualDiskHandle>.Failure(OpenFailure);
        }

        if (!_disks.TryGetValue(vhdPath, out FakeVirtualDisk? disk))
        {
            return Result<IVirtualDiskHandle>.Failure(
                DmgExitCode.MountFailed,
                $"The virtual disk '{vhdPath}' could not be opened because it does not exist.",
                "fake: no such disk");
        }

        if (mode == VirtualDiskAccessMode.ReadWrite && disk.IsWriteProtected)
        {
            return Result<IVirtualDiskHandle>.Failure(
                DmgExitCode.MountFailed,
                $"The virtual disk '{vhdPath}' cannot be opened for writing because it is write-protected.",
                "fake: write-protected");
        }

        disk.OpenCount++;

        return Result<IVirtualDiskHandle>.Success(new FakeVirtualDiskHandle(this, disk, mode));
    }

    /// <inheritdoc />
    public Result<VirtualDiskAttachment> Attach(IVirtualDiskHandle handle, VirtualDiskAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(options);

        Record($"Attach({handle.VhdPath}, {handle.Mode}, permanent={options.PermanentLifetime}, " +
               $"noDriveLetter={options.NoDriveLetter})");

        Result<FakeVirtualDisk> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out FakeVirtualDisk? disk))
        {
            return resolved.CastFailure<VirtualDiskAttachment>();
        }

        if (AttachFailure is not null)
        {
            return Result<VirtualDiskAttachment>.Failure(AttachFailure);
        }

        if (disk.AttachFailure is not null)
        {
            return Result<VirtualDiskAttachment>.Failure(disk.AttachFailure);
        }

        if (disk.IsAttached)
        {
            return Result<VirtualDiskAttachment>.Failure(
                DmgExitCode.MountFailed,
                $"The virtual disk '{disk.VhdPath}' is already attached.",
                "fake: already attached");
        }

        disk.IsAttached = true;
        disk.AttachedMode = handle.Mode;
        disk.PermanentLifetime = options.PermanentLifetime;
        disk.NoDriveLetter = options.NoDriveLetter;
        disk.AttachCount++;

        return Result<VirtualDiskAttachment>.Success(new VirtualDiskAttachment(
            disk.VhdPath,
            disk.PhysicalPath,
            handle.Mode,
            options.PermanentLifetime));
    }

    /// <inheritdoc />
    public Result Detach(IVirtualDiskHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Record($"Detach({handle.VhdPath})");

        Result<FakeVirtualDisk> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out FakeVirtualDisk? disk))
        {
            return resolved.Discard();
        }

        if (DetachFailure is not null)
        {
            return Result.Failure(DetachFailure);
        }

        if (!disk.IsAttached)
        {
            return Result.Failure(
                DmgExitCode.MountFailed,
                $"The virtual disk '{disk.VhdPath}' is not attached.",
                "fake: not attached");
        }

        DetachWithoutChecks(disk);

        return Result.Success();
    }

    /// <inheritdoc />
    public Result<string> GetPhysicalPath(IVirtualDiskHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Record($"GetPhysicalPath({handle.VhdPath})");

        Result<FakeVirtualDisk> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out FakeVirtualDisk? disk))
        {
            return resolved.CastFailure<string>();
        }

        if (PhysicalPathFailure is not null)
        {
            return Result<string>.Failure(PhysicalPathFailure);
        }

        if (!disk.IsAttached)
        {
            return Result<string>.Failure(
                DmgExitCode.MountFailed,
                $"The virtual disk '{disk.VhdPath}' has no physical device because it is not attached.",
                "fake: not attached");
        }

        return Result<string>.Success(disk.PhysicalPath);
    }

    /// <summary>Clears the call log, so a test can assert about one phase at a time.</summary>
    public void ClearCalls() => _calls.Clear();

    internal void OnHandleDisposed(FakeVirtualDisk disk)
    {
        disk.OpenCount--;

        // The real service behaves this way: without ATTACH_VIRTUAL_DISK_FLAG_
        // PERMANENT_LIFETIME the disk goes away with the handle that attached it.
        if (disk.IsAttached && !disk.PermanentLifetime)
        {
            DetachWithoutChecks(disk);
        }
    }

    private static void DetachWithoutChecks(FakeVirtualDisk disk)
    {
        disk.IsAttached = false;
        disk.AttachedMode = null;
        disk.PermanentLifetime = false;
        disk.NoDriveLetter = false;
        disk.DetachCount++;
    }

    private Result<FakeVirtualDisk> Resolve(IVirtualDiskHandle handle)
    {
        if (handle is not FakeVirtualDiskHandle fake || !ReferenceEquals(fake.Service, this))
        {
            return Result<FakeVirtualDisk>.Failure(
                DmgError.Internal(
                    "That handle did not come from this virtual disk service.",
                    $"handle for '{handle.VhdPath}'"));
        }

        if (!handle.IsOpen)
        {
            return Result<FakeVirtualDisk>.Failure(
                DmgError.Internal(
                    "That virtual disk handle has already been closed.",
                    $"handle for '{handle.VhdPath}'"));
        }

        return Result<FakeVirtualDisk>.Success(fake.Disk);
    }

    private string NextPhysicalPath() => $@"\\.\PhysicalDrive{++_nextPhysicalDrive}";

    private void Record(string call) => _calls.Add(call);
}

/// <summary>The state the fake keeps for one virtual disk.</summary>
public sealed class FakeVirtualDisk
{
    internal FakeVirtualDisk(string vhdPath, string physicalPath)
    {
        VhdPath = vhdPath;
        PhysicalPath = physicalPath;
    }

    /// <summary>The path the disk was registered under.</summary>
    public string VhdPath { get; }

    /// <summary>The device this disk becomes when attached.</summary>
    public string PhysicalPath { get; set; }

    /// <summary>Whether the disk is attached right now.</summary>
    public bool IsAttached { get; internal set; }

    /// <summary>The mode it was attached in, or null when it is not attached.</summary>
    public VirtualDiskAccessMode? AttachedMode { get; internal set; }

    /// <summary>Whether the current attachment outlives its handle.</summary>
    public bool PermanentLifetime { get; internal set; }

    /// <summary>Whether the current attachment suppressed automatic drive-letter assignment.</summary>
    public bool NoDriveLetter { get; internal set; }

    /// <summary>Refuse a read-write open, the way a VHD on read-only media would.</summary>
    public bool IsWriteProtected { get; set; }

    /// <summary>When set, attaching this particular disk fails with this error.</summary>
    public DmgError? AttachFailure { get; set; }

    /// <summary>How many handles to this disk are open.</summary>
    public int OpenCount { get; internal set; }

    /// <summary>How many times this disk has been attached.</summary>
    public int AttachCount { get; internal set; }

    /// <summary>How many times this disk has been detached, including implicit detaches.</summary>
    public int DetachCount { get; internal set; }
}

/// <summary>The fake's handle. Closing it mirrors what closing a real one does.</summary>
internal sealed class FakeVirtualDiskHandle : IVirtualDiskHandle
{
    internal FakeVirtualDiskHandle(FakeVirtualDiskService service, FakeVirtualDisk disk, VirtualDiskAccessMode mode)
    {
        Service = service;
        Disk = disk;
        Mode = mode;
        IsOpen = true;
    }

    internal FakeVirtualDiskService Service { get; }

    internal FakeVirtualDisk Disk { get; }

    public string VhdPath => Disk.VhdPath;

    public VirtualDiskAccessMode Mode { get; }

    public bool IsOpen { get; private set; }

    public void Dispose()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Service.OnHandleDisposed(Disk);
        GC.SuppressFinalize(this);
    }
}
