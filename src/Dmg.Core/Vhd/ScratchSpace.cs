namespace Dmg.Core.Vhd;

/// <summary>
/// One mount's scratch directory: created on the way in, deleted on the way out
/// unless the user asked to keep it.
/// </summary>
/// <remarks>
/// <para>
/// A conversion needs somewhere to put a VHD that may be tens of gigabytes, and
/// something has to be responsible for taking it away again. That responsibility
/// is this type, and it is an <see cref="IDisposable"/> so the responsibility
/// cannot be forgotten in an early return or an exception path:
/// </para>
/// <code>
/// using ScratchSpace scratch = ScratchSpace.Create(options).Value;
/// // write scratch.VhdPath, attach it, use it
/// // the directory goes away here, unless --keep-scratch
/// </code>
/// <para>
/// <b>The directory name is a generated <see cref="MountId"/>.</b> Nothing from
/// the image contributes to any path - not the volume name, not a file name, not
/// a label. See <see cref="MountId"/> for why. Before creating or deleting
/// anything, the composed path is checked to be genuinely inside the root.
/// </para>
/// <para>
/// <b>Cleanup does not throw.</b> By the time the directory is being removed the
/// real work has either succeeded or failed, and a locked file must not turn a
/// successful conversion into an exception. <see cref="Dispose"/> swallows the
/// failure and leaves it on <see cref="CleanupError"/>; a caller that wants to
/// report it calls <see cref="Cleanup"/> itself.
/// </para>
/// <para>
/// Portable: it is ordinary directory and file work, and its tests run on macOS.
/// </para>
/// </remarks>
public sealed class ScratchSpace : IDisposable
{
    private bool _disposed;

    private ScratchSpace(MountId id, string root, string directory, string vhdPath, bool keep)
    {
        Id = id;
        Root = root;
        Directory = directory;
        VhdPath = vhdPath;
        KeepScratch = keep;
    }

    /// <summary>The generated identifier this scratch space is named after.</summary>
    public MountId Id { get; }

    /// <summary>The root the mount directory was created under.</summary>
    public string Root { get; }

    /// <summary>The mount's own directory: <c>&lt;root&gt;\&lt;id&gt;</c>.</summary>
    public string Directory { get; }

    /// <summary>The path the VHD should be written to: <c>&lt;directory&gt;\&lt;id&gt;.vhd</c>.</summary>
    public string VhdPath { get; }

    /// <summary>True when the directory is to be left behind on dispose.</summary>
    public bool KeepScratch { get; }

    /// <summary>True once the directory has been removed.</summary>
    public bool IsCleanedUp { get; private set; }

    /// <summary>
    /// Why the last cleanup attempt failed, or null if it did not fail or has not
    /// been attempted.
    /// </summary>
    public DmgError? CleanupError { get; private set; }

    /// <summary>
    /// Creates a fresh scratch directory under the configured root.
    /// </summary>
    /// <param name="options">Where to create it and whether to keep it; null for the defaults.</param>
    /// <returns>
    /// The scratch space, or the failure: an unusable root comes back as an
    /// <see cref="DmgExitCode.InternalError"/> naming the directory and the reason.
    /// </returns>
    public static Result<ScratchSpace> Create(ScratchOptions? options = null)
    {
        ScratchOptions settings = options ?? ScratchOptions.Default;

        string root;

        try
        {
            root = Path.GetFullPath(
                string.IsNullOrWhiteSpace(settings.Root)
                    ? ScratchLayout.DefaultRoot()
                    : settings.Root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ScratchSpace>.Failure(DmgError.Internal(
                "The scratch directory is not a usable path.",
                exception.Message));
        }

        MountId id = MountId.New();
        string directory = ScratchLayout.DirectoryFor(root, id);

        // Cannot happen - the only variable part is 32 hex characters - which is
        // exactly why it is asserted before anything is created that will later be
        // deleted recursively.
        if (!ScratchLayout.IsWithin(root, directory))
        {
            return Result<ScratchSpace>.Failure(DmgError.Internal(
                "A scratch path escaped its root.",
                $"root '{root}', directory '{directory}'"));
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result<ScratchSpace>.Failure(DmgError.Internal(
                "The scratch directory could not be created.",
                $"'{directory}': {exception.Message}"));
        }

        return Result<ScratchSpace>.Success(new ScratchSpace(
            id,
            root,
            directory,
            Path.Combine(directory, ScratchLayout.FileNameFor(id)),
            settings.KeepScratch));
    }

    /// <summary>
    /// Removes the directory and everything in it, and reports whether it worked.
    /// </summary>
    /// <remarks>
    /// Honours <see cref="KeepScratch"/>: with the opt-out set this succeeds having
    /// deliberately deleted nothing. Calling it twice is harmless - an absent
    /// directory is a successful cleanup, not an error.
    /// </remarks>
    public Result Cleanup()
    {
        if (KeepScratch || IsCleanedUp)
        {
            return Result.Success();
        }

        if (!ScratchLayout.IsWithin(Root, Directory))
        {
            CleanupError = DmgError.Internal(
                "Refusing to delete a scratch directory that is not inside its root.",
                $"root '{Root}', directory '{Directory}'");

            return Result.Failure(CleanupError);
        }

        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            IsCleanedUp = true;
            CleanupError = null;

            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The usual cause on Windows is the VHD still being attached. Naming the
            // directory matters: the user can delete it themselves.
            CleanupError = DmgError.Internal(
                "The scratch directory could not be removed.",
                $"'{Directory}': {exception.Message}");

            return Result.Failure(CleanupError);
        }
    }

    /// <summary>
    /// Cleans up, unless <see cref="KeepScratch"/> says otherwise. Never throws:
    /// a failure is left on <see cref="CleanupError"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _ = Cleanup();
    }

    /// <summary>A one-line summary for verbose output.</summary>
    public override string ToString() =>
        KeepScratch ? $"{Directory} (kept)" : Directory;
}
