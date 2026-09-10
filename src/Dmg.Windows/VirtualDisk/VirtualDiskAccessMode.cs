namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// Whether a virtual disk is opened and attached for reading only, or for writing
/// as well.
/// </summary>
/// <remarks>
/// <para>
/// The mode is fixed when the disk is <em>opened</em>, not when it is attached:
/// <c>OpenVirtualDisk</c> takes an access mask, and asking for a read-write attach
/// through a handle opened read-only simply fails. Carrying the mode on the handle
/// means the two can never disagree - see <see cref="IVirtualDiskService.Open"/>.
/// </para>
/// <para>
/// <see cref="ReadOnly"/> is the default everywhere in this tool. A DMG is a
/// distribution artefact; the overwhelmingly common reason to mount one is to read
/// what is inside it, and a read-only attach cannot corrupt the image or the VHD
/// materialised from it. Writing is opt-in.
/// </para>
/// </remarks>
public enum VirtualDiskAccessMode
{
    /// <summary>Attach read-only. The default.</summary>
    ReadOnly = 0,

    /// <summary>Attach read-write. Requested explicitly, never assumed.</summary>
    ReadWrite = 1,
}
