using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Mounts;

/// <summary>
/// How a mount was attached, as it appears in the registry file: <c>ro</c> or
/// <c>rw</c>.
/// </summary>
/// <remarks>
/// <para>
/// A string rather than a serialized enum, on purpose. The registry file is
/// something a person may end up looking at while working out why a drive letter is
/// still taken, and <c>"mode": "ro"</c> reads as what it is. It is also what makes
/// the file survive an enum being renamed or reordered later: the on-disk values
/// are two literals defined here and nowhere else.
/// </para>
/// <para>
/// Anything the file says that is not one of those two literals is not guessed at.
/// The record is invalid and gets dropped, because inventing "probably read-only"
/// for a mount that might be writable is the wrong way to be wrong.
/// </para>
/// </remarks>
public static class MountMode
{
    /// <summary>The value written for a read-only attach, which is the default.</summary>
    public const string ReadOnly = "ro";

    /// <summary>The value written for a read-write attach.</summary>
    public const string ReadWrite = "rw";

    /// <summary>The registry value for an access mode.</summary>
    public static string From(VirtualDiskAccessMode mode) =>
        mode == VirtualDiskAccessMode.ReadWrite ? ReadWrite : ReadOnly;

    /// <summary>True when a string read from the file is one of the two legal values.</summary>
    public static bool IsValid(string? mode) => mode is ReadOnly or ReadWrite;

    /// <summary>
    /// The access mode a registry value means.
    /// </summary>
    /// <param name="mode">The value read from the file.</param>
    /// <returns>
    /// The mode, or <see cref="VirtualDiskAccessMode.ReadOnly"/> for anything
    /// unrecognised - the safe direction, and only reachable for a record that has
    /// already been rejected as invalid.
    /// </returns>
    public static VirtualDiskAccessMode ToAccessMode(string? mode) =>
        mode == ReadWrite ? VirtualDiskAccessMode.ReadWrite : VirtualDiskAccessMode.ReadOnly;
}
