using Dmg.Core;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.VirtualDisk;

/// <summary>
/// Pins the contract of <see cref="IVirtualDiskService"/> by exercising it through
/// the fake.
/// </summary>
/// <remarks>
/// These are tests of the port's rules, not of the fake's cleverness. Every
/// assertion here is something <c>virtdisk.dll</c> also does, and something the
/// mount sequence will depend on: a handle attaches once, a non-permanent attach
/// dies with its handle, a detached disk has no physical path.
/// </remarks>
public sealed class FakeVirtualDiskServiceTests
{
    private const string VhdPath = @"C:\scratch\dmg\payload.vhd";

    [Fact]
    public void OpensAttachesAndReportsThePhysicalDevice()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath, @"\\.\PhysicalDrive7");

        Result<IVirtualDiskHandle> opened = service.Open(VhdPath, VirtualDiskAccessMode.ReadOnly);
        Assert.True(opened.TryGetValue(out IVirtualDiskHandle? handle));

        using (handle)
        {
            Result<VirtualDiskAttachment> attached = service.Attach(handle, VirtualDiskAttachOptions.Mount);
            Assert.True(attached.TryGetValue(out VirtualDiskAttachment? attachment));

            Assert.Equal(VhdPath, attachment.VhdPath);
            Assert.Equal(@"\\.\PhysicalDrive7", attachment.PhysicalPath);
            Assert.Equal(VirtualDiskAccessMode.ReadOnly, attachment.Mode);

            Result<string> physicalPath = service.GetPhysicalPath(handle);
            Assert.True(physicalPath.TryGetValue(out string? path));
            Assert.Equal(@"\\.\PhysicalDrive7", path);
        }
    }

    [Fact]
    public void HandsOutADistinctPhysicalDeviceForEachDiskAndNeverDriveZero()
    {
        FakeVirtualDiskService service = new();

        FakeVirtualDisk first = service.AddDisk(@"C:\a.vhd");
        FakeVirtualDisk second = service.AddDisk(@"C:\b.vhd");

        Assert.NotEqual(first.PhysicalPath, second.PhysicalPath);
        Assert.DoesNotContain("PhysicalDrive0", first.PhysicalPath, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalDrive0", second.PhysicalPath, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningSomethingThatIsNotThereFailsWithMountFailed()
    {
        FakeVirtualDiskService service = new();

        Result<IVirtualDiskHandle> opened = service.Open(@"C:\missing.vhd", VirtualDiskAccessMode.ReadOnly);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.MountFailed, opened.Error.Code);
    }

    [Fact]
    public void AScriptedOpenFailureIsReturnedVerbatim()
    {
        DmgError refusal = new(DmgExitCode.ElevationRequired, "Run this from an elevated shell.");
        FakeVirtualDiskService service = new() { OpenFailure = refusal };
        service.AddDisk(VhdPath);

        Result<IVirtualDiskHandle> opened = service.Open(VhdPath, VirtualDiskAccessMode.ReadOnly);

        Assert.False(opened.Ok);
        Assert.Same(refusal, opened.Error);
    }

    [Fact]
    public void AttachingATwiceAttachedDiskFails()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        using IVirtualDiskHandle first = OpenOrThrow(service, VhdPath);
        using IVirtualDiskHandle second = OpenOrThrow(service, VhdPath);

        Assert.True(service.Attach(first, VirtualDiskAttachOptions.Mount).Ok);

        Result<VirtualDiskAttachment> again = service.Attach(second, VirtualDiskAttachOptions.Mount);

        Assert.False(again.Ok);
        Assert.Equal(DmgExitCode.MountFailed, again.Error.Code);
        Assert.Contains("already attached", again.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APermanentAttachSurvivesTheHandleThatMadeIt()
    {
        // This is why dmg mount can exit and leave the drive mounted.
        FakeVirtualDiskService service = new();
        FakeVirtualDisk disk = service.AddDisk(VhdPath);

        using (IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath))
        {
            Assert.True(service.Attach(handle, VirtualDiskAttachOptions.Mount).Ok);
        }

        Assert.True(disk.IsAttached);
        Assert.Equal(0, disk.OpenCount);
    }

    [Fact]
    public void ATransientAttachDiesWithItsHandle()
    {
        FakeVirtualDiskService service = new();
        FakeVirtualDisk disk = service.AddDisk(VhdPath);

        using (IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath))
        {
            Assert.True(service.Attach(handle, VirtualDiskAttachOptions.Transient).Ok);
            Assert.True(disk.IsAttached);
        }

        Assert.False(disk.IsAttached);
        Assert.Equal(1, disk.DetachCount);
    }

    [Fact]
    public void DetachReleasesTheDiskAndCanBeSeenAfterwards()
    {
        FakeVirtualDiskService service = new();
        FakeVirtualDisk disk = service.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        Assert.True(service.Attach(handle, VirtualDiskAttachOptions.Mount).Ok);

        Assert.True(service.Detach(handle).Ok);

        Assert.False(disk.IsAttached);
        Assert.Null(disk.AttachedMode);
        Assert.Empty(service.AttachedDisks);
    }

    [Fact]
    public void DetachingSomethingThatIsNotAttachedFails()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);

        Result detached = service.Detach(handle);

        Assert.False(detached.Ok);
        Assert.Equal(DmgExitCode.MountFailed, detached.Error.Code);
    }

    [Fact]
    public void TheMissingPhysicalPathIsHowCallersDiscoverADiskIsGone()
    {
        // Mount-registry reconciliation leans on exactly this: open the recorded VHD,
        // ask for its device, and treat the failure as "this entry is a ghost".
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);

        Result<string> path = service.GetPhysicalPath(handle);

        Assert.False(path.Ok);
        Assert.Equal(DmgExitCode.MountFailed, path.Error.Code);
    }

    [Fact]
    public void UsingAClosedHandleIsABugNotAMountFailure()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        handle.Dispose();

        Assert.False(handle.IsOpen);
        Assert.Equal(DmgExitCode.InternalError, service.Attach(handle, VirtualDiskAttachOptions.Mount).Error.Code);
        Assert.Equal(DmgExitCode.InternalError, service.Detach(handle).Error.Code);
        Assert.Equal(DmgExitCode.InternalError, service.GetPhysicalPath(handle).Error.Code);
    }

    [Fact]
    public void AHandleFromAnotherServiceIsRejected()
    {
        FakeVirtualDiskService one = new();
        FakeVirtualDiskService other = new();
        one.AddDisk(VhdPath);
        other.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(one, VhdPath);

        Assert.Equal(
            DmgExitCode.InternalError,
            other.Attach(handle, VirtualDiskAttachOptions.Mount).Error.Code);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        FakeVirtualDiskService service = new();
        FakeVirtualDisk disk = service.AddDisk(VhdPath);

        IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        handle.Dispose();
        handle.Dispose();

        Assert.Equal(0, disk.OpenCount);
    }

    [Fact]
    public void APerDiskAttachFailureLetsOneDiskFailWhileOthersSucceed()
    {
        FakeVirtualDiskService service = new();
        FakeVirtualDisk broken = service.AddDisk(@"C:\broken.vhd");
        service.AddDisk(@"C:\fine.vhd");

        broken.AttachFailure = new DmgError(DmgExitCode.MountFailed, "That file is not a virtual disk.");

        using IVirtualDiskHandle brokenHandle = OpenOrThrow(service, @"C:\broken.vhd");
        using IVirtualDiskHandle fineHandle = OpenOrThrow(service, @"C:\fine.vhd");

        Assert.False(service.Attach(brokenHandle, VirtualDiskAttachOptions.Mount).Ok);
        Assert.True(service.Attach(fineHandle, VirtualDiskAttachOptions.Mount).Ok);
    }

    [Fact]
    public void AWriteProtectedDiskRefusesAReadWriteOpenButAllowsAReadOnlyOne()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath).IsWriteProtected = true;

        Assert.False(service.Open(VhdPath, VirtualDiskAccessMode.ReadWrite).Ok);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        Assert.True(service.Attach(handle, VirtualDiskAttachOptions.Mount).Ok);
    }

    [Fact]
    public void RecordsEveryCallInOrderSoSequenceCanBeAsserted()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        service.Attach(handle, VirtualDiskAttachOptions.Mount);
        service.GetPhysicalPath(handle);
        service.Detach(handle);

        Assert.Collection(
            service.Calls,
            call => Assert.StartsWith("Open(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("Attach(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("GetPhysicalPath(", call, StringComparison.Ordinal),
            call => Assert.StartsWith("Detach(", call, StringComparison.Ordinal));

        service.ClearCalls();
        Assert.Empty(service.Calls);
    }

    [Fact]
    public void TheAttachOptionsAreRecordedSoLaterStoriesCanAssertTheFlags()
    {
        FakeVirtualDiskService service = new();
        FakeVirtualDisk disk = service.AddDisk(VhdPath);

        using IVirtualDiskHandle handle = OpenOrThrow(service, VhdPath);
        service.Attach(handle, new VirtualDiskAttachOptions(PermanentLifetime: true, NoDriveLetter: true));

        Assert.True(disk.PermanentLifetime);
        Assert.True(disk.NoDriveLetter);
        Assert.Contains("noDriveLetter=True", service.Calls[^1], StringComparison.Ordinal);
    }

    private static IVirtualDiskHandle OpenOrThrow(
        FakeVirtualDiskService service,
        string vhdPath,
        VirtualDiskAccessMode mode = VirtualDiskAccessMode.ReadOnly)
    {
        Result<IVirtualDiskHandle> opened = service.Open(vhdPath, mode);
        Assert.True(opened.TryGetValue(out IVirtualDiskHandle? handle), $"Opening '{vhdPath}' should succeed.");

        return handle;
    }
}
