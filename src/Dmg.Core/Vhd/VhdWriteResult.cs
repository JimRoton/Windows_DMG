namespace Dmg.Core.Vhd;

/// <summary>
/// What a completed VHD write produced.
/// </summary>
/// <param name="Footer">
/// The footer that was appended. Carries the disk size, the geometry and the
/// identifier Windows will report, so the caller need not read the file back to
/// describe what it wrote.
/// </param>
/// <param name="PayloadBytes">
/// Bytes of disk image written, footer excluded. Equal to
/// <c>Footer.DiskSize</c> for a fixed VHD, and smaller for a dynamic one, whose
/// unallocated blocks occupy no space.
/// </param>
/// <param name="TotalBytes">The size of the finished file, footer and metadata included.</param>
/// <param name="SourceBytes">
/// Bytes read from the source stream. Smaller than <paramref name="PayloadBytes"/>
/// only when the source did not end on a sector boundary and was padded out.
/// </param>
public sealed record VhdWriteResult(
    VhdFooter Footer,
    long PayloadBytes,
    long TotalBytes,
    long SourceBytes)
{
    /// <summary>The disk's capacity, as the footer declares it.</summary>
    public long DiskSize => Footer.DiskSize;

    /// <summary>
    /// True when the source stream did not end on a 512-byte boundary and the last
    /// partial sector was padded out with zeros.
    /// </summary>
    public bool WasPadded => PayloadBytes != SourceBytes;

    /// <summary>A one-line summary for verbose output.</summary>
    public override string ToString() =>
        $"{Footer.DiskType} VHD, {DiskSize} bytes of disk in a {TotalBytes}-byte file";
}
