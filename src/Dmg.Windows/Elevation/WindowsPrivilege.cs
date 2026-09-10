namespace Dmg.Windows.Elevation;

/// <summary>
/// The Windows privileges this tool cares about, by their real names.
/// </summary>
/// <remarks>
/// One privilege today, and probably one forever. The names are the constants from
/// <c>winnt.h</c>, spelled exactly as the API expects them: a privilege name is
/// looked up as a string and a typo produces "no such privilege" rather than a
/// compile error, which is a good reason for them to live in one place.
/// </remarks>
public static class WindowsPrivilege
{
    /// <summary>
    /// <c>SE_MANAGE_VOLUME_NAME</c> - the privilege that lets a process attach a
    /// virtual disk and do volume maintenance.
    /// </summary>
    /// <remarks>
    /// Granted to Administrators, and to nobody else by default. It is the single
    /// thing standing between <c>dmg mount</c> and success, which is why the check
    /// for it happens before any decoding rather than after.
    /// </remarks>
    public const string ManageVolume = "SeManageVolumePrivilege";

    /// <summary>
    /// What the Local Security Policy editor calls <see cref="ManageVolume"/>:
    /// "Perform volume maintenance tasks".
    /// </summary>
    /// <remarks>
    /// Worth carrying separately. A user who goes looking for
    /// <c>SeManageVolumePrivilege</c> in secpol.msc will not find it - the console
    /// only ever shows the display name.
    /// </remarks>
    public const string ManageVolumeDisplayName = "Perform volume maintenance tasks";

    /// <summary>The display name for a privilege, or the raw name if it is not one we know.</summary>
    public static string DisplayNameOf(string privilegeName) => privilegeName switch
    {
        ManageVolume => ManageVolumeDisplayName,
        _ => privilegeName,
    };
}
