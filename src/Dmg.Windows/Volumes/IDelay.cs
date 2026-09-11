namespace Dmg.Windows.Volumes;

/// <summary>
/// Waiting, behind a port, so that a test for a retry loop does not have to spend
/// the time the loop would.
/// </summary>
/// <remarks>
/// Discovery polls for up to several seconds. A test that actually slept that long
/// would either be skipped by whoever is waiting for the suite or quietly turned
/// into one attempt with no waiting, and the retry behaviour - the part that is
/// easy to get wrong and impossible to see from the outside - would stop being
/// tested. This interface is a single method so the tests can assert how long the
/// loop would have waited without waiting.
/// </remarks>
public interface IDelay
{
    /// <summary>Waits for the given duration. Zero or negative returns at once.</summary>
    void Wait(TimeSpan duration);
}

/// <summary>The real one: <see cref="Thread.Sleep(TimeSpan)"/>.</summary>
/// <remarks>
/// Synchronous on purpose. dmg is a command-line program doing one thing; there is
/// no work to get on with while Windows finishes surfacing a volume, and an async
/// path here would only spread <c>await</c> through the mount sequence for nothing.
/// </remarks>
public sealed class ThreadDelay : IDelay
{
    /// <summary>The single instance. It has no state.</summary>
    public static ThreadDelay Instance { get; } = new();

    /// <inheritdoc />
    public void Wait(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            Thread.Sleep(duration);
        }
    }
}
