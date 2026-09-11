using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// An <see cref="IDelay"/> that records how long <see cref="DriveLetterDiscovery"/>
/// would have waited, instead of waiting - so a retry-loop test finishes instantly
/// no matter how many attempts <see cref="VolumeWaitPolicy"/> allows.
/// </summary>
/// <remarks>
/// An optional callback runs on every recorded wait, which is what lets a test
/// simulate "the volume appears on the second attempt": the callback mutates a
/// <see cref="FakeVolumeService"/> between one look and the next, exactly where the
/// real wait would have given Windows time to do the same thing.
/// </remarks>
public sealed class FakeDelay(Action? onWait = null) : IDelay
{
    private readonly List<TimeSpan> _waits = [];

    /// <summary>Every duration the code under test asked to wait for, in order.</summary>
    public IReadOnlyList<TimeSpan> Waits => _waits;

    /// <inheritdoc />
    public void Wait(TimeSpan duration)
    {
        _waits.Add(duration);
        onWait?.Invoke();
    }
}
