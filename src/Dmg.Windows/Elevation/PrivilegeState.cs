namespace Dmg.Windows.Elevation;

/// <summary>
/// What a process token has to say about one privilege.
/// </summary>
/// <remarks>
/// <para>
/// Three states, not two, because Windows has three and collapsing them produces a
/// wrong answer in a common case. A privilege can be in a token and switched off:
/// that is the normal condition of nearly every privilege in an elevated token,
/// including this one. A process in that state is allowed to do the thing - it
/// simply has to enable the privilege first, which it can do for itself with no
/// prompt and no user involvement.
/// </para>
/// <para>
/// So <see cref="Disabled"/> means yes and <see cref="Absent"/> means no. A check
/// that tested for <see cref="Enabled"/> would tell an administrator running an
/// elevated shell that they are not elevated, which is both wrong and impossible
/// for them to act on.
/// </para>
/// <para>
/// <see cref="Absent"/> is zero so that an unset value denies rather than grants.
/// </para>
/// </remarks>
public enum PrivilegeState
{
    /// <summary>
    /// The privilege is not in the token at all. Nothing this process can do to
    /// itself will change that - it needs a different token, which means a different
    /// shell.
    /// </summary>
    Absent = 0,

    /// <summary>
    /// The privilege is in the token but switched off. Held, in the sense that
    /// matters: the process can enable it whenever it needs it.
    /// </summary>
    Disabled,

    /// <summary>The privilege is in the token and switched on.</summary>
    Enabled,
}
