namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// What a successful attach produced: which VHD, which physical device Windows
/// gave it, and on what terms.
/// </summary>
/// <param name="VhdPath">The full path of the <c>.vhd</c> that was attached.</param>
/// <param name="PhysicalPath">
/// The device Windows created, as returned by <c>GetVirtualDiskPhysicalPath</c> -
/// typically <c>\\.\PhysicalDriveN</c>. This is the handle the rest of the mount
/// sequence works from when it goes looking for volumes and a drive letter.
/// </param>
/// <param name="Mode">
/// The mode the disk was actually attached in. Reported rather than assumed: the
/// user is told which one they got, and the mount registry records it, so that a
/// later <c>dmg list</c> can say whether a mount is writable without going back to
/// Windows to ask.
/// </param>
/// <param name="PermanentLifetime">
/// Whether the disk survives the closing of the handle that attached it.
/// </param>
public sealed record VirtualDiskAttachment(
    string VhdPath,
    string PhysicalPath,
    VirtualDiskAccessMode Mode,
    bool PermanentLifetime)
{
    /// <summary>True when the disk was attached read-only.</summary>
    public bool IsReadOnly => Mode == VirtualDiskAccessMode.ReadOnly;

    /// <summary>
    /// The mode in the words the CLI uses when it prints the mount summary.
    /// </summary>
    public string ModeDescription => IsReadOnly ? "read-only" : "read-write";
}
