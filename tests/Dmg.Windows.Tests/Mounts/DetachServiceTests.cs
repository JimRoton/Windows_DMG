using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.Mounts;

/// <summary>
/// <see cref="DetachService"/> is what <c>dmg unmount</c> calls, by id, by drive
/// letter, or for everything at once (S8.9). The rule under every test here is the
/// same one the story states: detach, then delete the scratch file unless told to
/// keep it, then update the registry - and if Windows will not let go of the disk,
/// say so plainly and touch nothing else.
/// </summary>
public sealed class DetachServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DetachByIdDetachesDeletesTheScratchFileAndUpdatesTheRegistry()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        string vhdPath = directory.File("a.vhd");
        File.WriteAllText(vhdPath, "not a real vhd, just needs to exist");

        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");
        virtualDiskService.AddDisk(vhdPath).MarkAttached();

        DetachService detachService = new(registry, virtualDiskService);
        Result<DetachOutcome> result = detachService.DetachById(stored.Id);

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());
        Assert.True(result.Value!.WasMounted);
        Assert.True(result.Value.ScratchDeleted);
        Assert.Equal(stored.Id, result.Value.Record!.Id);

        Assert.False(virtualDiskService.Disk(vhdPath).IsAttached);
        Assert.False(File.Exists(vhdPath));
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void KeepScratchLeavesTheVhdFileInPlace()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        string vhdPath = directory.File("a.vhd");
        File.WriteAllText(vhdPath, "scratch");

        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");
        virtualDiskService.AddDisk(vhdPath).MarkAttached();

        DetachService detachService = new(registry, virtualDiskService);
        Result<DetachOutcome> result = detachService.DetachById(stored.Id, keepScratch: true);

        Assert.True(result.Ok);
        Assert.False(result.Value!.ScratchDeleted);
        Assert.True(File.Exists(vhdPath));
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void DetachingAnIdThatIsNotMountedIsNotAnError()
    {
        // Matches MountRegistry.Remove: asking to detach something already gone
        // has got the user what they wanted.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();
        DetachService detachService = new(registry, virtualDiskService);

        Result<DetachOutcome> result = detachService.DetachById("deadbeef");

        Assert.True(result.Ok);
        Assert.False(result.Value!.WasMounted);
    }

    [Fact]
    public void AnOpenHandleIsReportedClearlyAndNothingIsForced()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        string vhdPath = directory.File("a.vhd");
        File.WriteAllText(vhdPath, "scratch");

        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");
        FakeVirtualDisk disk = virtualDiskService.AddDisk(vhdPath).MarkAttached();
        disk.DetachFailure = DmgError.Internal(
            "The virtual disk could not be detached because a process still has a handle open on E:.",
            "fake: busy");

        DetachService detachService = new(registry, virtualDiskService);
        Result<DetachOutcome> result = detachService.DetachById(stored.Id);

        Assert.False(result.Ok);
        Assert.Contains("handle open", result.Error.Message, StringComparison.Ordinal);

        // Nothing forced, nothing changed: the disk is still attached, the scratch
        // file is still there, and the registry still has the entry to retry.
        Assert.True(disk.IsAttached);
        Assert.True(File.Exists(vhdPath));
        Assert.Equal("E", Assert.Single(registry.Read()).DriveLetter);
    }

    [Theory]
    [InlineData("E")]
    [InlineData("e")]
    [InlineData("E:")]
    public void DetachByDriveLetterAcceptsAnyOfTheFormsALetterComesIn(string typed)
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        string vhdPath = directory.File("a.vhd");
        File.WriteAllText(vhdPath, "scratch");

        Store(registry, @"C:\images\a.dmg", vhdPath, "E");
        virtualDiskService.AddDisk(vhdPath).MarkAttached();

        DetachService detachService = new(registry, virtualDiskService);
        Result<DetachOutcome> result = detachService.DetachByDriveLetter(typed);

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());
        Assert.True(result.Value!.WasMounted);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void DetachByDriveLetterThatIsNotALetterIsAUsageFailure()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();
        DetachService detachService = new(registry, virtualDiskService);

        Result<DetachOutcome> result = detachService.DetachByDriveLetter("ZZ");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void DetachByDriveLetterThatIsNotMountedIsNotAnError()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();
        DetachService detachService = new(registry, virtualDiskService);

        Result<DetachOutcome> result = detachService.DetachByDriveLetter("Z");

        Assert.True(result.Ok);
        Assert.False(result.Value!.WasMounted);
    }

    [Fact]
    public void DetachAllDetachesEverythingAndReportsFailuresSeparately()
    {
        // Best-effort across the whole registry: a busy handle on one mount must
        // not stop the other one from being detached.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        string goodVhd = directory.File("good.vhd");
        string busyVhd = directory.File("busy.vhd");
        File.WriteAllText(goodVhd, "scratch");
        File.WriteAllText(busyVhd, "scratch");

        MountRecord good = Store(registry, @"C:\images\good.dmg", goodVhd, "E");
        MountRecord busy = Store(registry, @"C:\images\busy.dmg", busyVhd, "F");

        virtualDiskService.AddDisk(goodVhd).MarkAttached();
        FakeVirtualDisk busyDisk = virtualDiskService.AddDisk(busyVhd).MarkAttached();
        busyDisk.DetachFailure = DmgError.Internal("A handle is still open on F:.", "fake: busy");

        DetachService detachService = new(registry, virtualDiskService);
        DetachAllOutcome outcome = detachService.DetachAll();

        DetachOutcome detached = Assert.Single(outcome.Detached);
        Assert.Equal(good.Id, detached.Record!.Id);

        DetachFailure failure = Assert.Single(outcome.Failed);
        Assert.Equal(busy.Id, failure.Record.Id);
        Assert.Contains("still open", failure.Error.Message, StringComparison.Ordinal);

        Assert.Equal("F", Assert.Single(registry.Read()).DriveLetter);
    }

    [Fact]
    public void DetachAllOnAnEmptyRegistryDetachesNothingAndFailsNothing()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();
        DetachService detachService = new(registry, virtualDiskService);

        DetachAllOutcome outcome = detachService.DetachAll();

        Assert.Empty(outcome.Detached);
        Assert.Empty(outcome.Failed);
    }

    [Fact]
    public void AVhdThatIsGoneEntirelyIsNotMountedAsFarAsDetachByIdCanTell()
    {
        // Reconciliation (S8.8) runs first, inside the lookup: a VHD that cannot
        // even be opened reads as "not attached" there, so it is already gone from
        // the list DetachById searches, and this is the same NothingToDo outcome
        // as an id nobody ever mounted.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        MountRecord stored = Store(registry, @"C:\images\a.dmg", @"C:\scratch\vanished.vhd", "E");

        DetachService detachService = new(registry, virtualDiskService);
        Result<DetachOutcome> result = detachService.DetachById(stored.Id);

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());
        Assert.False(result.Value!.WasMounted);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void ADiskThatVanishesBetweenReconciliationAndTheDetachItselfIsTreatedAsAlreadyGone()
    {
        // The narrow race the defensive branch inside DetachService.Detach exists
        // for: reconciliation just confirmed the disk was attached, but opening it
        // again to actually detach it fails anyway. Cleanup, not an error - there
        // is nothing left to fail a detach on.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService fake = new();

        string vhdPath = directory.File("a.vhd");
        File.WriteAllText(vhdPath, "scratch");

        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");
        fake.AddDisk(vhdPath).MarkAttached();

        DetachService detachService = new(registry, new OpenOnceThenVanishesVirtualDiskService(fake));
        Result<DetachOutcome> result = detachService.DetachById(stored.Id);

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());
        Assert.True(result.Value!.WasMounted);
        Assert.False(result.Value.ScratchDeleted);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void ConstructorRejectsNullDependencies()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        Assert.Throws<ArgumentNullException>(() => new DetachService(null!, virtualDiskService));
        Assert.Throws<ArgumentNullException>(() => new DetachService(registry, null!));
    }

    private static MountRecord Store(
        MountRegistry registry,
        string sourcePath,
        string vhdPath,
        string? driveLetter,
        VirtualDiskAccessMode mode = VirtualDiskAccessMode.ReadOnly)
    {
        Result<MountRecord> created = MountRecord.Create(sourcePath, vhdPath, driveLetter, mode, Noon);
        Assert.True(created.Ok, created.Ok ? string.Empty : created.Error.ToString());

        Result<MountRecord> added = registry.Add(created.Value!);
        Assert.True(added.Ok, added.Ok ? string.Empty : added.Error.ToString());

        return added.Value!;
    }

    /// <summary>
    /// Wraps a real <see cref="IVirtualDiskService"/> and fails the second
    /// <see cref="Open"/> onward, modelling a VHD that vanishes between
    /// reconciliation's probe and <see cref="DetachService"/>'s own open of the
    /// same disk moments later.
    /// </summary>
    private sealed class OpenOnceThenVanishesVirtualDiskService(IVirtualDiskService inner) : IVirtualDiskService
    {
        private int _opens;

        public Result<IVirtualDiskHandle> Open(string vhdPath, VirtualDiskAccessMode mode) =>
            ++_opens > 1
                ? Result<IVirtualDiskHandle>.Failure(
                    DmgExitCode.MountFailed,
                    $"The virtual disk '{vhdPath}' could not be opened because it does not exist.",
                    "fake: vanished between reads")
                : inner.Open(vhdPath, mode);

        public Result<VirtualDiskAttachment> Attach(IVirtualDiskHandle handle, VirtualDiskAttachOptions options) =>
            inner.Attach(handle, options);

        public Result Detach(IVirtualDiskHandle handle) => inner.Detach(handle);

        public Result<string> GetPhysicalPath(IVirtualDiskHandle handle) => inner.GetPhysicalPath(handle);
    }
}
