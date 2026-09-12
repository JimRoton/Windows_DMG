namespace Dmg.Core.Projection;

/// <summary>
/// One item a projection can show: a file or a directory, with the metadata a
/// file browser asks for before it ever reads a byte.
/// </summary>
/// <param name="Name">The item's name within its parent directory.</param>
/// <param name="IsDirectory">True for a directory.</param>
/// <param name="Length">The file's length in bytes. Zero for a directory.</param>
/// <param name="Created">Creation time, or null when the source did not record one.</param>
/// <param name="Modified">Last-modified time, or null.</param>
/// <param name="IsReadOnly">The read-only attribute.</param>
/// <param name="IsHidden">The hidden attribute.</param>
public sealed record ProjectedItem(
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool IsReadOnly,
    bool IsHidden);

/// <summary>
/// The contents of an image, addressed by path, for a projection to serve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than the projection head calling a filesystem reader
/// directly.</b> The Windows side of a projection
/// (<a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>) is a
/// set of callbacks: list a directory, describe one item, read a range of a file.
/// Everything interesting about answering those - resolving a path through nested
/// directories, matching names the way the filesystem matches them, deciding what
/// is a file and what is not - is logic that has nothing to do with Windows, and
/// behind this interface it is exercised on any machine. Only the P/Invoke stays
/// Windows-bound, which is the same division
/// <see cref="T:Dmg.Windows.VirtualDisk.IVirtualDiskService"/> already draws for
/// the VHD route.
/// </para>
/// <para>
/// <b>Paths are relative and separator-agnostic.</b> The root is the empty string.
/// Below it, callers may use either separator: Windows hands back <c>\</c> and
/// every test in this repository would rather write <c>/</c>. An implementation
/// accepts both.
/// </para>
/// <para>
/// <b>Read-only, by construction.</b> There is no write, create or delete on this
/// interface. This tool has never written into a <c>.dmg</c> and a projection is
/// not where that would start.
/// </para>
/// </remarks>
public interface IProjectedContent
{
    /// <summary>
    /// Lists the directory at <paramref name="relativePath"/>.
    /// </summary>
    /// <param name="relativePath">
    /// The directory's path relative to the projection root; the empty string for
    /// the root itself.
    /// </param>
    /// <returns>
    /// The entries, or a failure: <see cref="DmgExitCode.UsageError"/> when the path
    /// names a file or is not there, <see cref="DmgExitCode.CorruptImage"/> when the
    /// image cannot be walked.
    /// </returns>
    Result<IReadOnlyList<ProjectedItem>> List(string relativePath);

    /// <summary>
    /// Describes the single item at <paramref name="relativePath"/>.
    /// </summary>
    /// <param name="relativePath">The item's path relative to the projection root.</param>
    /// <returns>
    /// The item, or null when nothing is there - which is an ordinary answer, not a
    /// failure, because a projection is asked about paths that do not exist all the
    /// time.
    /// </returns>
    Result<ProjectedItem?> Find(string relativePath);

    /// <summary>
    /// Reads <paramref name="count"/> bytes of the file at
    /// <paramref name="relativePath"/>, starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="relativePath">The file's path relative to the projection root.</param>
    /// <param name="offset">The offset within the file.</param>
    /// <param name="count">How many bytes to read; clipped to the file's length.</param>
    /// <returns>
    /// The bytes, shorter than asked for at the end of the file and empty past it.
    /// </returns>
    Result<byte[]> Read(string relativePath, long offset, int count);
}
