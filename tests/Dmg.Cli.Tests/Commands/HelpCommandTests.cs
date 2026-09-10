using Dmg.Cli.Commands;
using Dmg.Cli.Help;
using Dmg.Cli.Parsing;
using Dmg.Core;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// <c>dmg help</c> and <c>dmg help &lt;command&gt;</c>: the right screen on stdout at
/// exit 0, and a usage error on stderr at exit 2 for a name that does not exist.
/// </summary>
public sealed class HelpCommandTests
{
    private static readonly HelpCommand Command = new();

    private static CommandRegistry Registry() => new(
    [
        Command,
        new FakeCommand("info", new CommandLineSpec("info", [new OptionSpec("json", "JSON out.")], ["IMAGE"])),
        new FakeCommand("version", new CommandLineSpec("version", [])),
    ]);

    [Fact]
    public void WithNoArgumentItPrintsTheToolHelpToStdout()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([], output.Output, Registry())));

        Assert.Empty(output.Stderr);
        Assert.Equal(HelpText.Tagline, output.StdoutLines[0]);
        Assert.Contains("info", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAVerbItPrintsThatVerbsHelp()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext(["info"], output.Output, Registry())));

        Assert.Empty(output.Stderr);
        Assert.Contains("Usage: dmg info [OPTIONS] IMAGE", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ItCanDescribeItself()
    {
        RecordingOutput output = new();

        Command.Execute(new CliContext(["help"], output.Output, Registry()));

        Assert.Contains("Usage: dmg help [COMMAND]", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbIsAUsageErrorWithASuggestion()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["infoo"], output.Output, Registry())));

        Assert.Empty(output.Stdout);
        Assert.Contains("There is no 'infoo' command", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'info'?", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbWithNothingCloseJustSaysSo()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["zzzzzzzz"], output.Output, Registry())));

        Assert.DoesNotContain("Did you mean", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("dmg help", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoVerbsIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["info", "version"], output.Output, Registry())));

        Assert.Contains("takes one argument", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARegistryItStillPrintsSomething()
    {
        RecordingOutput output = new();

        // A CliContext built without a catalogue - which is what most tests do -
        // must not make help throw.
        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([], output.Output)));
        Assert.Contains(HelpText.Tagline, output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Command.Execute(null!));
    }
}
