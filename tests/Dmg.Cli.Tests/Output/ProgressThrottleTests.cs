using Dmg.Cli.Output;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Output;

/// <summary>
/// S9.11: the throttle that <c>verify</c>, <c>extract</c> and <c>mount</c> all
/// report byte progress through - one line per whole percentage point, to
/// stderr, silenced entirely at <see cref="Verbosity.Quiet"/>.
/// </summary>
/// <remarks>
/// Rendering itself - one line per update, never stdout, nothing under
/// <c>--quiet</c> - is <see cref="IOutput.Progress"/>'s contract, exercised here
/// through the real <see cref="Dmg.Core.Diagnostics.ConsoleOutput"/> that
/// <see cref="RecordingOutput"/> wraps rather than a hand-rolled stand-in, so
/// what these tests check is what a real run - stderr redirected to a file, as
/// every non-interactive invocation's stderr is - actually produces.
/// </remarks>
public sealed class ProgressThrottleTests
{
    [Fact]
    public void EachWholePercentagePointIsItsOwnLine()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        // Ten reads of one tenth each: ten distinct percentage points, so ten
        // lines - the "one line per update" half of the contract, in the
        // redirected/non-TTY shape every automated run and every CI log has.
        for (int tenth = 1; tenth <= 10; tenth++)
        {
            progress.Report(tenth * 10, 100);
        }

        Assert.Equal(10, output.StderrLines.Count);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void ManyCallsCollapseToAtMostOneLinePerPercentagePoint()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        for (int reported = 0; reported < 1000; reported++)
        {
            // A one-byte-at-a-time reporter over a 1000-byte transfer: every
            // call lands in a different whole percent exactly once, so this is
            // still ten calls per percentage point squeezed into a hundred
            // points - the throttle's entire job is not turning that into a
            // thousand lines.
            progress.Report(reported, 1000);
        }

        Assert.True(output.StderrLines.Count <= 100, $"expected at most 100 lines, got {output.StderrLines.Count}");
        Assert.True(output.StderrLines.Count > 1, "expected more than one line across a 1000-step transfer");
    }

    [Fact]
    public void NothingIsReportedTwiceForTheSamePercentInARow()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        progress.Report(50, 100);
        progress.Report(50, 100);
        progress.Report(50, 100);

        Assert.Single(output.StderrLines);
    }

    [Fact]
    public void ProgressNeverAppearsOnStdout()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        progress.Report(1, 2);
        progress.Report(2, 2);

        Assert.NotEmpty(output.Stderr);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void QuietSuppressesEveryLine()
    {
        RecordingOutput output = new(Verbosity.Quiet);
        ProgressThrottle progress = new(output.Output);

        for (int tenth = 1; tenth <= 10; tenth++)
        {
            progress.Report(tenth * 10, 100);
        }

        Assert.Empty(output.Stderr);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void QuietSuppressesEvenTheFinalHundredPercentLine()
    {
        RecordingOutput output = new(Verbosity.Quiet);
        ProgressThrottle progress = new(output.Output);

        progress.Report(100, 100);

        Assert.Empty(output.Stderr);
    }

    [Fact]
    public void JsonModeStillReportsProgressOnStderr()
    {
        // --json reserves stdout for the one JSON document; it says nothing
        // about stderr, which still carries progress exactly as it would in
        // human mode (S9.11's "always stderr").
        RecordingOutput output = new(isJson: true);
        ProgressThrottle progress = new(output.Output);

        progress.Report(1, 2);
        progress.Report(2, 2);

        Assert.Equal(2, output.StderrLines.Count);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void AZeroTotalIsReportedAsWhollyDoneOnce()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        progress.Report(0, 0);
        progress.Report(0, 0);

        Assert.Single(output.StderrLines);
        Assert.Contains("100%", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineNamesBothCountsAndThePercentage()
    {
        RecordingOutput output = new();
        ProgressThrottle progress = new(output.Output);

        progress.Report(512, 1024);

        Assert.Contains("512 bytes", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("1.00 KiB", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("50%", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NullOutputIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProgressThrottle(null!));
}
