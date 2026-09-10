using Dmg.Core;

namespace Dmg.Windows.Elevation;

/// <summary>
/// Asks the process token what privileges it holds, behind a port that can be
/// faked.
/// </summary>
/// <remarks>
/// <para>
/// One method, for the same reason <see cref="Dmg.Windows.VirtualDisk.IVirtualDiskService"/>
/// has four: the interesting part of elevation is not the token query, it is what
/// the tool does with the answer - when it asks, what it says when the answer is
/// no, and what it refuses to do about it. Behind an interface those decisions run
/// on any machine. Behind a direct P/Invoke they can only be exercised on Windows,
/// twice, once elevated and once not.
/// </para>
/// <para>
/// <b>Asking about a privilege, not about "being an admin".</b> Windows has no
/// useful notion of "is this an administrator": a member of the Administrators
/// group running an unelevated shell has a filtered token that holds none of the
/// group's privileges, and the answer to "is the user an admin?" would be yes while
/// the mount still fails. The token is the only thing that decides, so the token is
/// what gets asked.
/// </para>
/// <para>
/// <b>No exceptions.</b> A failure to query is a <see cref="Result"/> carrying a
/// <see cref="DmgError"/> with an exit code already chosen, exactly as the virtual
/// disk port does. A raw Win32 error never escapes an implementation.
/// </para>
/// </remarks>
public interface IPrivilegeService
{
    /// <summary>
    /// Looks one privilege up in this process's token.
    /// </summary>
    /// <param name="privilegeName">
    /// The <c>winnt.h</c> name, such as <see cref="WindowsPrivilege.ManageVolume"/>.
    /// </param>
    /// <returns>
    /// The state - <see cref="PrivilegeState.Absent"/> for a privilege the token
    /// does not have, which is an answer and not a failure - or a failure if the
    /// token could not be read at all.
    /// </returns>
    Result<PrivilegeState> Query(string privilegeName);
}
