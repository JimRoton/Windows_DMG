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
/// The disk the VHD describes, in bytes, footer excluded: the source rounded up
/// to a whole sector. The same figure as <c>Footer.DiskSize</c>, and the same for
/// a fixed and a dynamic write of one image - it is the capacity Windows reports,
/// not the space the file takes.
/// </param>
/// <param name="TotalBytes">
/// The size of the finished file, footer and metadata included. For a fixed VHD
/// that is the payload plus 512 bytes; for a dynamic one it is where the saving
/// shows, because the blocks that were all zeros are not in it.
/// </param>
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
