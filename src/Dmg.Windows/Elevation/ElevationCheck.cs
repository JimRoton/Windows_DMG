using Dmg.Core;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Elevation;

/// <summary>
/// The fail-fast check: does this process hold what it needs, asked before it does
/// anything that costs the user time or disk space.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why first.</b> Mounting a DMG means decoding the whole image to a scratch
/// VHD and only then asking Windows to attach it. Decoding a two-gigabyte image and
/// then discovering that the shell was never elevated wastes minutes and leaves a
/// two-gigabyte file to clean up, and the user learns nothing they could not have
/// been told at the start. The token query costs one system call and no I/O, so
/// there is no reason for it to happen anywhere but the top of the command.
/// </para>
/// <para>
/// <b>No self-elevation, and that is deliberate.</b> dmg never re-launches itself
/// with a UAC prompt. A command-line tool that pops a consent dialog out of a
/// scripted run is a tool that hangs unattended builds; worse, a tool that quietly
/// relaunches itself elevated is one whose arguments, working directory and output
/// redirection all change underneath the caller, and whose exit code the original
/// shell never sees. The contract here is the plain one: dmg tells the user exactly
/// what to do, exits 7, and lets them decide. Anyone reading this later looking for
/// the missing <c>ShellExecute</c> with <c>runas</c>: it is not missing.
/// </para>
/// <para>
/// The remedy sentence itself is
/// <see cref="VirtualDiskErrors.ElevationRemedy"/> - the same words the mount path
/// uses when Windows refuses a call for the same reason. One wording, one place;
/// a user who hits both must not be told two different things.
/// </para>
/// </remarks>
public static class ElevationCheck
{
    /// <summary>
    /// Checks that the process can attach a virtual disk. The call to make before
    /// any decoding starts.
    /// </summary>
    /// <param name="privileges">The port onto the process token.</param>
    /// <returns>
    /// Success when the privilege is held, enabled or not;
    /// <see cref="DmgExitCode.ElevationRequired"/> when it is absent, with the
    /// remedy in the message.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="privileges"/> is null.</exception>
    public static Result RequireManageVolume(IPrivilegeService privileges) =>
        Require(privileges, WindowsPrivilege.ManageVolume);

    /// <summary>
    /// Checks one named privilege.
    /// </summary>
    /// <param name="privileges">The port onto the process token.</param>
    /// <param name="privilegeName">The <c>winnt.h</c> privilege name.</param>
    /// <returns>
    /// Success when the privilege is held, the query's own failure when the token
    /// could not be read, or <see cref="DmgExitCode.ElevationRequired"/>.
    /// </returns>
    public static Result Require(IPrivilegeService privileges, string privilegeName)
    {
        Result<PrivilegeState> queried = Query(privileges, privilegeName);

        if (!queried.TryGetValue(out PrivilegeState state))
        {
            return queried.Discard();
        }

        return IsHeld(state)
            ? Result.Success()
            : Result.Failure(NotHeld(privilegeName, state));
    }

    /// <summary>
    /// Asks for a privilege's state without deciding anything about it - what
    /// <c>dmg info</c> and diagnostics want.
    /// </summary>
    /// <param name="privileges">The port onto the process token.</param>
    /// <param name="privilegeName">The <c>winnt.h</c> privilege name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="privileges"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="privilegeName"/> is blank.</exception>
    public static Result<PrivilegeState> Query(IPrivilegeService privileges, string privilegeName)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        return privileges.Query(privilegeName);
    }

    /// <summary>
    /// True when a state means the process may go ahead.
    /// <see cref="PrivilegeState.Disabled"/> counts: see the remarks on
    /// <see cref="PrivilegeState"/>.
    /// </summary>
    public static bool IsHeld(PrivilegeState state) =>
        state is PrivilegeState.Enabled or PrivilegeState.Disabled;

    /// <summary>
    /// The refusal, with the exact remedy in it.
    /// </summary>
    /// <param name="privilegeName">The privilege that is missing.</param>
    /// <param name="state">The state the token reported, for the detail line.</param>
    public static DmgError NotHeld(string privilegeName, PrivilegeState state = PrivilegeState.Absent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        string display = WindowsPrivilege.DisplayNameOf(privilegeName);

        return new DmgError(
            DmgExitCode.ElevationRequired,
            $"This shell does not have the '{display}' right, so dmg stopped before doing any "
            + "work - nothing has been decoded and no files have been written. "
            + VirtualDiskErrors.ElevationRemedy,
            $"{privilegeName} is {state} in the process token.");
    }
}
