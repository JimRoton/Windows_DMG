using Dmg.Core;

namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// The four <c>virtdisk.dll</c> operations this tool needs, behind a port that can
/// be faked.
/// </summary>
/// <remarks>
/// <para>
/// This interface exists so the mount sequence is testable. Everything interesting
/// about mounting a DMG on Windows - read-only versus read-write, what to do when
/// the disk is already attached, what to say when the file turns out not to be a
/// VHD at all, whether to roll back a half-finished mount - is a decision the code
/// makes about the outcome of these four calls. Behind an interface those
/// decisions can be exercised on any machine; behind a direct P/Invoke they can
/// only be exercised on Windows, by hand, with a real disk.
/// </para>
/// <para>
/// <b>No exceptions.</b> Every implementation returns failures as
/// <see cref="Result"/> values carrying a <see cref="DmgError"/> with an exit code
/// already chosen. A raw HRESULT must never escape an implementation of this port:
/// mapping Win32 error codes to something a user can act on is the implementation's
/// job, not the caller's.
/// </para>
/// <para>
/// <b>Ordering.</b> <see cref="Open"/> comes first and fixes the access mode.
/// <see cref="Attach"/>, <see cref="Detach"/> and <see cref="GetPhysicalPath"/> all
/// take the handle it produced. A handle may be attached at most once.
/// </para>
/// </remarks>
public interface IVirtualDiskService
{
    /// <summary>
    /// Opens an existing <c>.vhd</c>, fixing the access mode for everything done
    /// through the resulting handle.
    /// </summary>
    /// <param name="vhdPath">Full path to the <c>.vhd</c> file.</param>
    /// <param name="mode">
    /// <see cref="VirtualDiskAccessMode.ReadOnly"/> unless the user asked for
    /// <c>--rw</c>.
    /// </param>
    /// <returns>
    /// The handle, or a failure: <see cref="DmgExitCode.ElevationRequired"/> when
    /// the caller lacks the rights, <see cref="DmgExitCode.MountFailed"/> when the
    /// file is missing, locked, or not a virtual disk.
    /// </returns>
    Result<IVirtualDiskHandle> Open(string vhdPath, VirtualDiskAccessMode mode);

    /// <summary>
    /// Attaches the disk, making it appear to Windows as a physical drive.
    /// </summary>
    /// <param name="handle">A handle from <see cref="Open"/>. Its mode decides whether the attach is read-only.</param>
    /// <param name="options">Lifetime and drive-letter choices.</param>
    /// <returns>
    /// The attachment, including the physical device path and the mode actually
    /// used, or a failure explaining why Windows refused.
    /// </returns>
    Result<VirtualDiskAttachment> Attach(IVirtualDiskHandle handle, VirtualDiskAttachOptions options);

    /// <summary>
    /// Detaches the disk. Succeeds only if this process is allowed to; a disk
    /// attached by another user is not ours to take away.
    /// </summary>
    /// <param name="handle">A handle from <see cref="Open"/>, referring to an attached disk.</param>
    Result Detach(IVirtualDiskHandle handle);

    /// <summary>
    /// Asks Windows what physical device an attached disk became.
    /// </summary>
    /// <param name="handle">A handle from <see cref="Open"/>.</param>
    /// <returns>
    /// Something like <c>\\.\PhysicalDrive3</c>, or a failure if the disk is not
    /// attached. The failure is the supported way to ask "is this still attached?" -
    /// which is what mount-registry reconciliation needs.
    /// </returns>
    Result<string> GetPhysicalPath(IVirtualDiskHandle handle);
}
