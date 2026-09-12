using Dmg.Core.Filesystems;

namespace Dmg.Core.Projection;

/// <summary>
/// An exFAT volume's contents, addressed by path, for a projection to serve.
/// </summary>
/// <remarks>
/// <para>
/// The adapter between <see cref="ExFatReader"/>, which knows about clusters and
/// directory entry sets, and <see cref="IProjectedContent"/>, which knows about
/// paths. Everything here is path resolution: splitting a relative path into
/// segments and walking down from the root, one directory read per segment.
/// </para>
/// <para>
/// <b>Name matching is case-insensitive.</b> exFAT preserves the case a name was
/// created with and matches without regard to it, and so does Windows. Matching
/// case-sensitively here would make a file that Explorer can see impossible to
/// open.
/// </para>
/// <para>
/// <b>Nothing is cached yet.</b> Each call re-reads the directories on the way
/// down, so listing a deep tree re-reads its parents. That is honest and simple,
/// and it is bounded by how often a file browser asks; if it proves too slow under
/// a real ProjFS workload, a directory cache belongs here, behind this same
/// interface, rather than anywhere else.
/// </para>
/// </remarks>
public sealed class ExFatProjectedContent : IProjectedContent
{
    private readonly ExFatReader _reader;

    /// <summary>Wraps a reader over the volume to be projected.</summary>
    /// <param name="reader">The exFAT volume's reader.</param>
    public ExFatProjectedContent(ExFatReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<ProjectedItem>> List(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        Result<IReadOnlyList<ExFatEntry>> listed = ListEntries(relativePath);

        return listed.TryGetValue(out IReadOnlyList<ExFatEntry>? entries)
            ? Result<IReadOnlyList<ProjectedItem>>.Success([.. entries.Select(ToItem)])
            : listed.CastFailure<IReadOnlyList<ProjectedItem>>();
    }

    /// <inheritdoc />
    public Result<ProjectedItem?> Find(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        Result<ExFatEntry?> found = FindEntry(relativePath);

        return found.TryGetValue(out ExFatEntry? entry)
            ? Result<ProjectedItem?>.Success(entry is null ? null : ToItem(entry))
            : found.CastFailure<ProjectedItem?>();
    }

    /// <inheritdoc />
    public Result<byte[]> Read(string relativePath, long offset, int count)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Result<ExFatEntry?> found = FindEntry(relativePath);

        if (!found.TryGetValue(out ExFatEntry? entry))
        {
            return found.CastFailure<byte[]>();
        }

        if (entry is null)
        {
            return Result<byte[]>.Failure(DmgError.Usage(
                $"There is nothing at '{relativePath}' in this volume.",
                "The projection was asked to read a path that is not in the image."));
        }

        return entry.IsDirectory
            ? Result<byte[]>.Failure(DmgError.Usage(
                $"'{relativePath}' is a directory, not a file.",
                "Directories are listed, not read as data."))
            : _reader.ReadFile(entry, offset, count);
    }

    /// <summary>Splits a relative path into its segments, tolerating either separator.</summary>
    /// <remarks>
    /// Empty segments are dropped, so a trailing separator, a doubled one, and a
    /// leading one all mean what a reader would expect rather than producing a
    /// nameless segment that matches nothing.
    /// </remarks>
    internal static string[] Segments(string relativePath) =>
        relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static ProjectedItem ToItem(ExFatEntry entry) => new(
        entry.Name,
        entry.IsDirectory,
        entry.IsDirectory ? 0 : entry.Length,
        entry.Created,
        entry.Modified,
        entry.IsReadOnly,
        entry.IsHidden);

    /// <summary>Lists the directory a path names, or the root for an empty path.</summary>
    private Result<IReadOnlyList<ExFatEntry>> ListEntries(string relativePath)
    {
        string[] segments = Segments(relativePath);

        if (segments.Length == 0)
        {
            return _reader.ReadRootDirectory();
        }

        Result<ExFatEntry?> found = FindEntry(relativePath);

        if (!found.TryGetValue(out ExFatEntry? entry))
        {
            return found.CastFailure<IReadOnlyList<ExFatEntry>>();
        }

        if (entry is null)
        {
            return Result<IReadOnlyList<ExFatEntry>>.Failure(DmgError.Usage(
                $"There is no directory at '{relativePath}' in this volume.",
                "The projection was asked to list a path that is not in the image."));
        }

        return entry.IsDirectory
            ? _reader.ReadDirectory(entry.FirstCluster, entry.IsContiguous, entry.Length)
            : Result<IReadOnlyList<ExFatEntry>>.Failure(DmgError.Usage(
                $"'{relativePath}' is a file, not a directory.",
                "Files are read, not listed."));
    }

    /// <summary>Walks down from the root to the entry a path names.</summary>
    /// <returns>The entry, or null when any segment along the way is not there.</returns>
    private Result<ExFatEntry?> FindEntry(string relativePath)
    {
        string[] segments = Segments(relativePath);

        if (segments.Length == 0)
        {
            // The root itself. It is a directory, and it has no entry of its own.
            return Result<ExFatEntry?>.Success(new ExFatEntry(
                string.Empty,
                IsDirectory: true,
                Length: 0,
                FirstCluster: _reader.Geometry.RootDirectoryCluster,
                IsContiguous: false,
                Created: null,
                Modified: null,
                IsReadOnly: true,
                IsHidden: false));
        }

        Result<IReadOnlyList<ExFatEntry>> current = _reader.ReadRootDirectory();

        if (!current.TryGetValue(out IReadOnlyList<ExFatEntry>? entries))
        {
            return current.CastFailure<ExFatEntry?>();
        }

        for (int index = 0; index < segments.Length; index++)
        {
            ExFatEntry? match = entries.FirstOrDefault(
                entry => string.Equals(entry.Name, segments[index], StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return Result<ExFatEntry?>.Success(null);
            }

            if (index == segments.Length - 1)
            {
                return Result<ExFatEntry?>.Success(match);
            }

            if (!match.IsDirectory)
            {
                // A segment in the middle of the path is a file, so the rest of the
                // path cannot exist. Not a failure - just nothing there.
                return Result<ExFatEntry?>.Success(null);
            }

            Result<IReadOnlyList<ExFatEntry>> next =
                _reader.ReadDirectory(match.FirstCluster, match.IsContiguous, match.Length);

            if (!next.TryGetValue(out entries))
            {
                return next.CastFailure<ExFatEntry?>();
            }
        }

        return Result<ExFatEntry?>.Success(null);
    }
}
