namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// The attach-time choices that are not the access mode.
/// </summary>
/// <param name="PermanentLifetime">
/// Keep the disk attached after the handle that attached it is closed. True by
/// default, because <c>dmg mount</c> attaches a disk and then exits - without this
/// the disk would vanish the moment the process did.
/// </param>
/// <param name="NoDriveLetter">
/// Suppress the automatic drive-letter assignment Windows would otherwise make, so
/// that this tool can pick and report the letter itself.
/// </param>
/// <remarks>
/// The access mode is deliberately absent: it belongs to the handle, fixed at open
/// time. See <see cref="VirtualDiskAccessMode"/>.
/// </remarks>
public sealed record VirtualDiskAttachOptions(
    bool PermanentLifetime = true,
    bool NoDriveLetter = false)
{
    /// <summary>
    /// What <c>dmg mount</c> asks for: the disk stays attached after the process
    /// exits, and Windows assigns the drive letter.
    /// </summary>
    public static VirtualDiskAttachOptions Mount { get; } = new();

    /// <summary>
    /// A disk that lives only as long as the handle. Useful for verification passes
    /// that attach, read and detach inside one command.
    /// </summary>
    public static VirtualDiskAttachOptions Transient { get; } = new(PermanentLifetime: false);
}
