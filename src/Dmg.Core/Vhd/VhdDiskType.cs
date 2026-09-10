namespace Dmg.Core.Vhd;

/// <summary>
/// The <c>Disk Type</c> field of a VHD footer, at offset 60.
/// </summary>
/// <remarks>
/// Only <see cref="Fixed"/> is produced by this tool. A fixed VHD is the decoded
/// payload laid down verbatim with a 512-byte footer appended - no block
/// allocation table, no sparse bitmap, nothing for the Windows VHD provider to
/// misinterpret. The other members exist so that a footer read back off disk can
/// be described accurately when we reject it.
/// </remarks>
public enum VhdDiskType : uint
{
    /// <summary>Not a disk type any implementation writes; treated as malformed.</summary>
    None = 0,

    /// <summary>Reserved and deprecated in the VHD specification.</summary>
    Reserved = 1,

    /// <summary>A fixed-size hard disk image. The only type this tool writes.</summary>
    Fixed = 2,

    /// <summary>A dynamically expanding hard disk image.</summary>
    Dynamic = 3,

    /// <summary>A differencing hard disk image, chained to a parent.</summary>
    Differencing = 4,
}
