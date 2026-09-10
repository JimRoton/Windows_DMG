namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// Which <c>virtdisk.dll</c> call produced an error, so the message can say what
/// was being attempted rather than just what went wrong.
/// </summary>
public enum VirtualDiskOperation
{
    /// <summary><c>OpenVirtualDisk</c>.</summary>
    Open,

    /// <summary><c>AttachVirtualDisk</c>.</summary>
    Attach,

    /// <summary><c>DetachVirtualDisk</c>.</summary>
    Detach,

    /// <summary><c>GetVirtualDiskPhysicalPath</c>.</summary>
    GetPhysicalPath,
}
