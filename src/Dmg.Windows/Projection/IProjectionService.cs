using Dmg.Core;
using Dmg.Core.Projection;

namespace Dmg.Windows.Projection;

/// <summary>
/// Where a projection is rooted and how it presents itself.
/// </summary>
/// <param name="RootPath">
/// The directory the contents appear under. It is created if it is not there, and
/// it must be empty: projecting over a directory that already holds files would
/// leave the user unable to tell which were theirs.
/// </param>
/// <param name="VolumeLabel">
/// The label of the volume being projected, for messages. Never used to build a
/// path - see <c>MountId</c> for why nothing out of an image reaches the
/// filesystem.
/// </param>
public sealed record ProjectionOptions(string RootPath, string? VolumeLabel = null);

/// <summary>
/// A running projection. Disposing it stops the projection and leaves the root
/// directory empty.
/// </summary>
public interface IProjectionSession : IDisposable
{
    /// <summary>The directory the contents are appearing under.</summary>
    string RootPath { get; }

    /// <summary>True until the projection is stopped.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Stops the projection, leaving the root directory in place but empty.
    /// </summary>
    /// <remarks>
    /// Stopping twice is not an error; the second call reports success having done
    /// nothing, the same way an absent scratch directory is a successful cleanup.
    /// </remarks>
    Result Stop();
}

/// <summary>
/// The Windows Projected File System, behind a port that can be faked.
/// </summary>
/// <remarks>
/// <para>
/// The projection head from
/// <a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>: the
/// route for an image too large to materialise. Where
/// <see cref="VirtualDisk.IVirtualDiskService"/> hands Windows a whole decoded
/// volume and lets its exFAT driver read it, this hands Windows a set of callbacks
/// and decrypts only what is actually opened. Mount time stops depending on how
/// big the image is.
/// </para>
/// <para>
/// <b>This interface exists so the sequence is testable.</b> Everything worth
/// getting right - refusing a root that is not empty, reporting ProjFS being
/// switched off as something the user can act on, stopping cleanly when the
/// process is asked to stop - is a decision about the outcome of these calls, and
/// behind an interface those decisions can be exercised on a Mac. The same
/// argument, and the same shape, as the VHD port.
/// </para>
/// <para>
/// <b>No exceptions, no HRESULTs.</b> Implementations return
/// <see cref="Result"/> values carrying a <see cref="DmgError"/> with an exit code
/// already chosen. In particular, ProjFS is an optional Windows feature: a machine
/// with it switched off must be told which feature to enable, not handed
/// <c>0x80070002</c>.
/// </para>
/// </remarks>
public interface IProjectionService
{
    /// <summary>
    /// Whether this machine can project at all.
    /// </summary>
    /// <returns>
    /// Success when ProjFS is present and enabled;
    /// <see cref="DmgExitCode.UnsupportedFormat"/> naming the Windows feature to
    /// turn on when it is not.
    /// </returns>
    /// <remarks>
    /// Checked before anything is decoded, so a machine that cannot project finds
    /// out in a millisecond rather than after the passphrase prompt.
    /// </remarks>
    Result EnsureAvailable();

    /// <summary>
    /// Starts projecting <paramref name="content"/> under
    /// <paramref name="options"/>'s root.
    /// </summary>
    /// <param name="options">Where to root the projection.</param>
    /// <param name="content">
    /// The image's contents. Every callback Windows makes is answered from here, on
    /// whatever thread ProjFS chooses, so an implementation of
    /// <see cref="IProjectedContent"/> handed to this method must tolerate being
    /// called concurrently.
    /// </param>
    /// <returns>
    /// The running session, or a failure: <see cref="DmgExitCode.UsageError"/> when
    /// the root is unusable, <see cref="DmgExitCode.MountFailed"/> when ProjFS
    /// refuses to start.
    /// </returns>
    Result<IProjectionSession> Start(ProjectionOptions options, IProjectedContent content);
}
