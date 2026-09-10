namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// An open handle to a virtual disk.
/// </summary>
/// <remarks>
/// <para>
/// Disposing the handle closes it. If the disk was attached without
/// <see cref="VirtualDiskAttachOptions.PermanentLifetime"/>, closing the handle
/// also detaches the disk - a Win32 behaviour that is easy to forget and produces
/// a mount that disappears at process exit, so it is modelled here and in the
/// fake rather than left as folklore.
/// </para>
/// <para>
/// The interface deliberately exposes no native handle. Callers work in terms of
/// the four operations on <see cref="IVirtualDiskService"/>; nothing above this
/// port should ever hold an <c>IntPtr</c>.
/// </para>
/// </remarks>
public interface IVirtualDiskHandle : IDisposable
{
    /// <summary>The full path of the <c>.vhd</c> this handle refers to.</summary>
    string VhdPath { get; }

    /// <summary>
    /// The access the handle was opened with. An attach through this handle uses
    /// this mode; there is no way to ask for a different one.
    /// </summary>
    VirtualDiskAccessMode Mode { get; }

    /// <summary>False once the handle has been disposed.</summary>
    bool IsOpen { get; }
}
