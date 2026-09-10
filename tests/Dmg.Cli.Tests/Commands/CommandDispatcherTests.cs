using Dmg.Cli.Commands;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The routing contract: the right verb runs, the wrong one is a usage error, and
/// a bug in a verb becomes an exit code rather than a stack trace.
/// </summary>
public sealed class CommandDispatcherTests
{
    [Fact]
    public void RunsTheNamedVerbAndReturnsItsExitCode()
    {
        FakeCommand info = new("info", DmgExitCode.Success);
        FakeCommand mount = new("mount", DmgExitCode.MountFailed);
        CommandDispatcher dispatcher = new(new CommandRegistry([info, mount]));
        RecordingOutput recorder = new();

        DmgExitCode code = dispatcher.Execute(["mount", "image.dmg"], recorder.Output);

        Assert.Equal(DmgExitCode.MountFailed, code);
        Assert.Equal(1, mount.Calls);
        Assert.Equal(0, info.Calls);
    }

    [Fact]
    public void HandsTheVerbEverythingAfterItsOwnName()
    {
        FakeCommand info = new("info");
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));

        dispatcher.Execute(["info", "--json", "image.dmg"], new RecordingOutput().Output);

        Assert.Equal(["--json", "image.dmg"], info.LastArguments);
    }

    [Fact]
    public void AVerbWithNoArgumentsGetsAnEmptyList()
    {
        FakeCommand info = new("info");
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));

        dispatcher.Execute(["info"], new RecordingOutput().Output);

        Assert.Empty(info.LastArguments);
    }

    [Fact]
    public void NoArgumentsAtAllIsAUsageErrorThatNamesTheVerbs()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([new FakeCommand("info")]));
        RecordingOutput recorder = new();

        DmgExitCode code = dispatcher.Execute([], recorder.Output);

        Assert.Equal(DmgExitCode.UsageError, code);
        Assert.Contains("No command given", recorder.Stderr, StringComparison.Ordinal);
        Assert.Contains("info", recorder.Stderr, StringComparison.Ordinal);
        Assert.Empty(recorder.Stdout);
    }

    [Fact]
    public void AnUnknownVerbIsAUsageErrorThatNamesIt()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([new FakeCommand("info")]));
        RecordingOutput recorder = new();

        DmgExitCode code = dispatcher.Execute(["inof", "image.dmg"], recorder.Output);

        Assert.Equal(DmgExitCode.UsageError, code);
        Assert.Contains("'inof' is not a dmg command", recorder.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void VerbsAreMatchedCaseSensitively()
    {
        FakeCommand info = new("info");
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));

        DmgExitCode code = dispatcher.Execute(["INFO"], new RecordingOutput().Output);

        Assert.Equal(DmgExitCode.UsageError, code);
        Assert.Equal(0, info.Calls);
    }

    [Fact]
    public void AnExceptionOutOfAVerbBecomesExitOneAndAMessage()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry(
        [
            new FakeCommand("info", _ => throw new InvalidOperationException("boom")),
        ]));

        RecordingOutput recorder = new();

        DmgExitCode code = dispatcher.Execute(["info"], recorder.Output);

        Assert.Equal(DmgExitCode.InternalError, code);
        Assert.Contains("internal error", recorder.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", recorder.Stderr, StringComparison.Ordinal);
        Assert.Empty(recorder.Stdout);
    }

    [Fact]
    public void TheExceptionDetailShowsOnlyAtVerbose()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry(
        [
            new FakeCommand("info", _ => throw new InvalidOperationException("boom")),
        ]));

        RecordingOutput recorder = new(Verbosity.Verbose);

        Assert.Equal(DmgExitCode.InternalError, dispatcher.Execute(["info"], recorder.Output));
        Assert.Contains("InvalidOperationException: boom", recorder.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInternalErrorIsStillReportedAtQuiet()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry(
        [
            new FakeCommand("info", _ => throw new InvalidOperationException("boom")),
        ]));

        RecordingOutput recorder = new(Verbosity.Quiet);

        Assert.Equal(DmgExitCode.InternalError, dispatcher.Execute(["info"], recorder.Output));
        Assert.Contains("internal error", recorder.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArgumentsAndNullOutputAreRejected()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([]));

        Assert.Throws<ArgumentNullException>(() => dispatcher.Execute(null!, new RecordingOutput().Output));
        Assert.Throws<ArgumentNullException>(() => dispatcher.Execute([], null!));
    }

    [Fact]
    public void AnEmptyRegistryDoesNotOfferAnEmptyListOfVerbs()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.UsageError, dispatcher.Execute([], recorder.Output));
        Assert.DoesNotContain("Try one of", recorder.Stderr, StringComparison.Ordinal);
    }
}
