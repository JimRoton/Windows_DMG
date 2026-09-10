using Dmg.Core;

namespace Dmg.Windows.Mounts;

/// <summary>
/// The lock that stops two <c>dmg</c> processes writing the registry at once: a
/// file next to it, held open exclusively for as long as the update takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lock file and not a mutex.</b> A named mutex would be tidier and would
/// only work while every writer is this program. The registry is a file in a folder
/// a user can open, and the failure that matters is two mounts started from two
/// terminals a second apart, each reading the same list and each writing back its
/// own copy - the first mount silently disappearing from the file. An exclusive
/// open of a file on disk is the mechanism the operating system already provides
/// for that, it needs no agreement between processes beyond the path, and it is
/// released by the kernel if a process is killed mid-update.
/// </para>
/// <para>
/// <b>Writers only.</b> Reading does not take this lock, and does not need to:
/// updates are written to a temporary file and moved into place, so a reader sees
/// either the whole previous file or the whole new one. Making readers wait would
/// mean <c>dmg list</c> could block behind somebody else's mount for no benefit.
/// </para>
/// <para>
/// <b>Waiting is bounded.</b> An update takes milliseconds, so a wait of any length
/// means something has gone wrong - a hung process, a stale handle - and the honest
/// answer then is to say so rather than to hang a command line forever.
/// </para>
/// <para>
/// The lock file is left behind on purpose. Deleting it on release opens a race
/// where one process deletes the file another has just opened, and an empty file in
/// an application data folder costs nothing.
/// </para>
/// </remarks>
internal sealed class RegistryLock : IDisposable
{
    /// <summary>How long to wait between attempts. Short: contention is rare and brief.</summary>
    private const int PollMilliseconds = 20;

    private readonly FileStream _stream;

    private RegistryLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/> for whoever has it.
    /// </summary>
    /// <param name="lockPath">The lock file. Its folder must already exist.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <returns>
    /// The held lock, which the caller disposes, or
    /// <see cref="DmgExitCode.MountFailed"/> when the wait ran out or the file could
    /// not be created at all.
    /// </returns>
    public static Result<RegistryLock> Acquire(string lockPath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);

        long deadline = Environment.TickCount64 + (long)Math.Max(timeout.TotalMilliseconds, 0);

        while (true)
        {
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                return Result<RegistryLock>.Success(new RegistryLock(stream));
            }
            catch (IOException exception)
            {
                // Somebody else has it. That is the expected case, so it is the one
                // that retries rather than the one that reports.
                if (Environment.TickCount64 >= deadline)
                {
                    return Result<RegistryLock>.Failure(
                        DmgExitCode.MountFailed,
                        "Another dmg process is updating the mount registry. Wait for it to "
                        + "finish and run this again.",
                        $"Could not lock '{lockPath}' within {(long)timeout.TotalMilliseconds}ms: "
                        + exception.Message);
                }

                Thread.Sleep(PollMilliseconds);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Not contention: the path is unusable. Retrying would only turn an
                // immediate error into a slow one.
                return Result<RegistryLock>.Failure(
                    DmgExitCode.MountFailed,
                    $"dmg could not use '{lockPath}' to coordinate access to the mount registry.",
                    exception.Message);
            }
        }
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose() => _stream.Dispose();
}
