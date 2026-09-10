namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// The <c>virtdisk.dll</c> access masks and attach flags, and the two functions
/// that decide which of them this tool asks for.
/// </summary>
/// <remarks>
/// <para>
/// The constants are plain integers and the two <c>Compose</c> methods are plain
/// arithmetic, so this class carries no Windows dependency and every flag decision
/// can be asserted on any machine. That matters more than it sounds: the
/// difference between a read-only mount and a read-write one is one bit, the
/// consequence of getting it wrong is a modified disk image, and there is no way
/// to see that bit from outside once the call has been made.
/// </para>
/// <para>
/// The policy the composition encodes: <b>read-only unless someone asked for
/// otherwise</b>. A DMG is something you were given. Mounting one to look inside
/// should not be able to change it, and <c>--rw</c> is the deliberate act of
/// saying you meant to.
/// </para>
/// </remarks>
public static class VirtualDiskFlags
{
    /// <summary><c>VIRTUAL_DISK_ACCESS_ATTACH_RO</c>.</summary>
    public const uint AccessAttachReadOnly = 0x0001_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_ATTACH_RW</c>.</summary>
    public const uint AccessAttachReadWrite = 0x0002_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_DETACH</c>.</summary>
    public const uint AccessDetach = 0x0004_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_GET_INFO</c>.</summary>
    public const uint AccessGetInfo = 0x0008_0000;

    /// <summary><c>OPEN_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    public const uint OpenNone = 0;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    public const uint AttachNone = 0;

    /// <summary>
    /// <c>ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY</c>. Set unless the caller asked for
    /// <see cref="VirtualDiskAccessMode.ReadWrite"/>.
    /// </summary>
    public const uint AttachReadOnly = 0x0000_0001;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c>.</summary>
    public const uint AttachNoDriveLetter = 0x0000_0002;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME</c>.</summary>
    public const uint AttachPermanentLifetime = 0x0000_0004;

    /// <summary><c>DETACH_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    public const uint DetachNone = 0;

    /// <summary>
    /// Turns the presence or absence of <c>--rw</c> into an access mode.
    /// </summary>
    /// <param name="readWriteRequested">True only when the user passed <c>--rw</c>.</param>
    /// <remarks>
    /// The one place in the codebase where that switch becomes a mode. Everything
    /// downstream - the access mask, the attach flag, what gets printed, what gets
    /// written to the mount registry - follows from the value this returns.
    /// </remarks>
    public static VirtualDiskAccessMode ModeFor(bool readWriteRequested) =>
        readWriteRequested ? VirtualDiskAccessMode.ReadWrite : VirtualDiskAccessMode.ReadOnly;

    /// <summary>
    /// The access mask for <c>OpenVirtualDisk</c>.
    /// </summary>
    /// <remarks>
    /// <c>GET_INFO</c> and <c>DETACH</c> are always included: the physical-path
    /// query needs the first, and being unable to undo a mount you just made is not
    /// a state worth being in. Only the attach right varies with the mode.
    /// </remarks>
    public static uint ComposeAccessMask(VirtualDiskAccessMode mode) =>
        AccessGetInfo | AccessDetach | (mode == VirtualDiskAccessMode.ReadWrite
            ? AccessAttachReadWrite
            : AccessAttachReadOnly);

    /// <summary>
    /// The flags for <c>AttachVirtualDisk</c>.
    /// </summary>
    /// <param name="mode">The mode the handle was opened with.</param>
    /// <param name="options">The lifetime and drive-letter choices.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static uint ComposeAttachFlags(VirtualDiskAccessMode mode, VirtualDiskAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        uint flags = AttachNone;

        // The default, and the reason this method exists: anything that is not an
        // explicit read-write request attaches read-only.
        if (mode != VirtualDiskAccessMode.ReadWrite)
        {
            flags |= AttachReadOnly;
        }

        if (options.PermanentLifetime)
        {
            flags |= AttachPermanentLifetime;
        }

        if (options.NoDriveLetter)
        {
            flags |= AttachNoDriveLetter;
        }

        return flags;
    }

    /// <summary>True when a composed attach-flag set will produce a read-only mount.</summary>
    public static bool IsReadOnly(uint attachFlags) => (attachFlags & AttachReadOnly) != 0;
}
