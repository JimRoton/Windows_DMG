using Dmg.Core;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.VirtualDisk;

/// <summary>
/// The read-only default, asserted at the level of the actual bits.
/// </summary>
/// <remarks>
/// The difference between a mount that can change a user's disk image and one that
/// cannot is a single flag on a call nobody can observe from outside. So the flag
/// composition is a pure function, and this is where the promise "read-only unless
/// you asked for otherwise" is actually checked.
/// </remarks>
public sealed class VirtualDiskFlagsTests
{
    private const string VhdPath = @"C:\scratch\dmg\payload.vhd";

    [Fact]
    public void ReadOnlyIsWhatYouGetWhenYouAskForNothing()
    {
        // default(VirtualDiskAccessMode) must be the safe one: a forgotten
        // assignment anywhere in the codebase then fails safe rather than open.
        Assert.Equal(VirtualDiskAccessMode.ReadOnly, default);
        Assert.Equal(VirtualDiskAccessMode.ReadOnly, VirtualDiskFlags.ModeFor(readWriteRequested: false));
    }

    [Fact]
    public void OnlyAnExplicitRequestProducesReadWrite() =>
        Assert.Equal(VirtualDiskAccessMode.ReadWrite, VirtualDiskFlags.ModeFor(readWriteRequested: true));

    [Fact]
    public void AReadOnlyAttachSetsAttachVirtualDiskFlagReadOnly()
    {
        uint flags = VirtualDiskFlags.ComposeAttachFlags(
            VirtualDiskAccessMode.ReadOnly,
            VirtualDiskAttachOptions.Mount);

        Assert.Equal(VirtualDiskFlags.AttachReadOnly, flags & VirtualDiskFlags.AttachReadOnly);
        Assert.True(VirtualDiskFlags.IsReadOnly(flags));
    }

    [Fact]
    public void AReadWriteAttachClearsIt()
    {
        uint flags = VirtualDiskFlags.ComposeAttachFlags(
            VirtualDiskAccessMode.ReadWrite,
            VirtualDiskAttachOptions.Mount);

        Assert.Equal(0u, flags & VirtualDiskFlags.AttachReadOnly);
        Assert.False(VirtualDiskFlags.IsReadOnly(flags));
    }

    [Fact]
    public void TheMountDefaultIsReadOnlyAndPermanentAndNothingElse()
    {
        uint flags = VirtualDiskFlags.ComposeAttachFlags(
            VirtualDiskFlags.ModeFor(readWriteRequested: false),
            VirtualDiskAttachOptions.Mount);

        Assert.Equal(
            VirtualDiskFlags.AttachReadOnly | VirtualDiskFlags.AttachPermanentLifetime,
            flags);
    }

    [Fact]
    public void TheOtherFlagsAreIndependentOfTheMode()
    {
        VirtualDiskAttachOptions options = new(PermanentLifetime: false, NoDriveLetter: true);

        Assert.Equal(
            VirtualDiskFlags.AttachReadOnly | VirtualDiskFlags.AttachNoDriveLetter,
            VirtualDiskFlags.ComposeAttachFlags(VirtualDiskAccessMode.ReadOnly, options));

        Assert.Equal(
            VirtualDiskFlags.AttachNoDriveLetter,
            VirtualDiskFlags.ComposeAttachFlags(VirtualDiskAccessMode.ReadWrite, options));
    }

    [Fact]
    public void AnUnrecognisedModeStillAttachesReadOnly()
    {
        // Defence against a future member being added to the enum without this
        // switch being revisited: anything that is not explicitly ReadWrite is
        // treated as read-only, rather than falling through to a writable mount.
        uint flags = VirtualDiskFlags.ComposeAttachFlags(
            (VirtualDiskAccessMode)99,
            VirtualDiskAttachOptions.Transient);

        Assert.True(VirtualDiskFlags.IsReadOnly(flags));
    }

    [Theory]
    [InlineData(VirtualDiskAccessMode.ReadOnly, VirtualDiskFlags.AccessAttachReadOnly)]
    [InlineData(VirtualDiskAccessMode.ReadWrite, VirtualDiskFlags.AccessAttachReadWrite)]
    public void TheAccessMaskAsksForTheRightAttachRight(VirtualDiskAccessMode mode, uint expectedAttachRight)
    {
        uint mask = VirtualDiskFlags.ComposeAccessMask(mode);

        Assert.Equal(expectedAttachRight, mask & expectedAttachRight);

        // Both modes always keep the right to look the device up and to undo the
        // mount; being unable to detach what you just attached is not a state worth
        // being in.
        Assert.Equal(VirtualDiskFlags.AccessGetInfo, mask & VirtualDiskFlags.AccessGetInfo);
        Assert.Equal(VirtualDiskFlags.AccessDetach, mask & VirtualDiskFlags.AccessDetach);
    }

    [Fact]
    public void AReadOnlyMaskDoesNotSmuggleInTheWriteRight()
    {
        uint mask = VirtualDiskFlags.ComposeAccessMask(VirtualDiskAccessMode.ReadOnly);

        Assert.Equal(0u, mask & VirtualDiskFlags.AccessAttachReadWrite);
    }

    [Fact]
    public void ComposeAttachFlagsRejectsMissingOptions() =>
        Assert.Throws<ArgumentNullException>(
            () => VirtualDiskFlags.ComposeAttachFlags(VirtualDiskAccessMode.ReadOnly, null!));

    [Fact]
    public void TheModeSurvivesAllTheWayToWhatTheUserIsTold()
    {
        // S8.3's other half: the mode has to come back out so it can be printed and
        // recorded, not just applied and forgotten.
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        Result<IVirtualDiskHandle> opened = service.Open(
            VhdPath,
            VirtualDiskFlags.ModeFor(readWriteRequested: true));

        Assert.True(opened.TryGetValue(out IVirtualDiskHandle? handle));

        using (handle)
        {
            Result<VirtualDiskAttachment> attached = service.Attach(handle, VirtualDiskAttachOptions.Mount);
            Assert.True(attached.TryGetValue(out VirtualDiskAttachment? attachment));

            Assert.Equal(VirtualDiskAccessMode.ReadWrite, attachment.Mode);
            Assert.False(attachment.IsReadOnly);
            Assert.Equal("read-write", attachment.ModeDescription);
            Assert.Equal(VirtualDiskAccessMode.ReadWrite, service.Disk(VhdPath).AttachedMode);
        }
    }

    [Fact]
    public void WithoutRwTheAttachmentReportsReadOnly()
    {
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        Result<IVirtualDiskHandle> opened = service.Open(
            VhdPath,
            VirtualDiskFlags.ModeFor(readWriteRequested: false));

        Assert.True(opened.TryGetValue(out IVirtualDiskHandle? handle));

        using (handle)
        {
            Assert.Equal(VirtualDiskAccessMode.ReadOnly, handle.Mode);

            Result<VirtualDiskAttachment> attached = service.Attach(handle, VirtualDiskAttachOptions.Mount);
            Assert.True(attached.TryGetValue(out VirtualDiskAttachment? attachment));

            Assert.True(attachment.IsReadOnly);
            Assert.Equal("read-only", attachment.ModeDescription);
        }
    }

    [Fact]
    public void AHandleCannotBeAttachedInAModeItWasNotOpenedFor()
    {
        // There is deliberately no way to pass a mode to Attach. The mode is fixed
        // at open, because OpenVirtualDisk's access mask is what actually decides
        // it - an attach-time override would be a lie the API would then refuse.
        FakeVirtualDiskService service = new();
        service.AddDisk(VhdPath);

        Result<IVirtualDiskHandle> opened = service.Open(VhdPath, VirtualDiskAccessMode.ReadOnly);
        Assert.True(opened.TryGetValue(out IVirtualDiskHandle? handle));

        using (handle)
        {
            Result<VirtualDiskAttachment> attached = service.Attach(handle, VirtualDiskAttachOptions.Mount);
            Assert.True(attached.TryGetValue(out VirtualDiskAttachment? attachment));

            Assert.Equal(handle.Mode, attachment.Mode);
        }
    }
}
