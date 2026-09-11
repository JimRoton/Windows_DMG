using Dmg.Cli.Commands;
using Dmg.Cli.Help;
using Dmg.Cli.Parsing;
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
    public void ADoubleDashHelpPrintsTheToolHelpToStdoutAndExitsZero()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([new FakeCommand("info")]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.Success, dispatcher.Execute(["--help"], recorder.Output));
        Assert.Empty(recorder.Stderr);
        Assert.Equal(HelpText.Tagline, recorder.StdoutLines[0]);
    }

    [Fact]
    public void AShortDashHPrintsTheSameThing()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([new FakeCommand("info")]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.Success, dispatcher.Execute(["-h"], recorder.Output));
        Assert.Equal(HelpText.Tagline, recorder.StdoutLines[0]);
    }

    [Fact]
    public void HelpForAVerbIsAnsweredBeforeTheVerbRuns()
    {
        // The point of doing this here rather than in each verb: `dmg info --help`
        // has to print the help, not complain that IMAGE is missing.
        FakeCommand info = new("info", new CommandLineSpec("info", [], ["IMAGE"]));
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.Success, dispatcher.Execute(["info", "--help"], recorder.Output));

        Assert.Equal(0, info.Calls);
        Assert.Empty(recorder.Stderr);
        Assert.Contains("Usage: dmg info IMAGE", recorder.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpIsAnsweredWhereverOnTheVerbsLineItAppears()
    {
        FakeCommand info = new("info", new CommandLineSpec("info", [], ["IMAGE"]));
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.Success, dispatcher.Execute(["info", "image.dmg", "-h"], recorder.Output));
        Assert.Equal(0, info.Calls);
    }

    [Fact]
    public void HelpAfterTheTerminatorIsJustAnArgument()
    {
        FakeCommand info = new("info");
        CommandDispatcher dispatcher = new(new CommandRegistry([info]));

        dispatcher.Execute(["info", "--", "--help"], new RecordingOutput().Output);

        Assert.Equal(1, info.Calls);
        Assert.Equal(["--", "--help"], info.LastArguments);
    }

    [Fact]
    public void DoubleDashVersionIsTheVersionVerb()
    {
        FakeCommand version = new("version");
        CommandDispatcher dispatcher = new(new CommandRegistry([version]));

        Assert.Equal(DmgExitCode.Success, dispatcher.Execute(["--version"], new RecordingOutput().Output));
        Assert.Equal(1, version.Calls);
        Assert.Empty(version.LastArguments);
    }

    [Fact]
    public void DoubleDashVersionWithoutAVersionVerbIsStillAUsageError()
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([new FakeCommand("info")]));
        RecordingOutput recorder = new();

        Assert.Equal(DmgExitCode.UsageError, dispatcher.Execute(["--version"], recorder.Output));
        Assert.Contains("is not a dmg command", recorder.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerbCanSeeTheCatalogueButIsHandedItReadOnly()
    {
        CommandRegistry? seen = null;
        FakeCommand info = new("info", context =>
        {
            seen = context.Registry;
            return DmgExitCode.Success;
        });
        CommandRegistry registry = new([info]);

        new CommandDispatcher(registry).Execute(["info"], new RecordingOutput().Output);

        Assert.Same(registry, seen);
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
