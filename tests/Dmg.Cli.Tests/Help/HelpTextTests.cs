using Dmg.Cli.Commands;
using Dmg.Cli.Help;
using Dmg.Cli.Parsing;
using Dmg.Cli.Tests.Commands;

namespace Dmg.Cli.Tests.Help;

/// <summary>
/// The two screens. What matters is not the exact wording but that every line comes
/// out of a spec - so these check that what the spec says appears, and that what it
/// does not say does not.
/// </summary>
public sealed class HelpTextTests
{
    private static readonly CommandRegistry Registry = new(
    [
        new FakeCommand("info", new CommandLineSpec(
            "info",
            [
                new OptionSpec("partition", "Which partition to describe.", 'p', "N"),
                new OptionSpec("password-env", "Read the passphrase from a variable.", ValueName: "VAR"),
            ],
            ["IMAGE"],
            ["Exits 0 even for an image it cannot mount."])),
        new FakeCommand("version", new CommandLineSpec("version", [])),
    ]);

    [Fact]
    public void TheToolHelpListsEveryVerbWithItsSummary()
    {
        string text = HelpText.ForTool(Registry);

        Assert.Contains(HelpText.Tagline, text, StringComparison.Ordinal);
        Assert.Contains("Usage: dmg [OPTIONS] <command> [ARGUMENTS]", text, StringComparison.Ordinal);

        foreach (ICliCommand command in Registry.Commands)
        {
            Assert.Contains(command.Verb, text, StringComparison.Ordinal);
            Assert.Contains(command.Summary, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheToolHelpListsTheGlobalSwitchesAndHelpItself()
    {
        string text = HelpText.ForTool(Registry);

        foreach (OptionSpec option in GlobalOptions.Specs)
        {
            Assert.Contains(option.LongForm, text, StringComparison.Ordinal);
            Assert.Contains(option.Description, text, StringComparison.Ordinal);
        }

        // --help is answered by the dispatcher and is in no verb's spec, so it can
        // only reach the listing by being added deliberately. An option a user
        // cannot discover may as well not exist.
        Assert.Contains("--help", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolHelpSurvivesAnEmptyRegistry()
    {
        string text = HelpText.ForTool(CommandRegistry.Empty);

        // No "Commands:" heading over an empty list.
        Assert.DoesNotContain("Commands:", text, StringComparison.Ordinal);
        Assert.Contains(HelpText.Tagline, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerbsHelpComesEntirelyFromItsSpec()
    {
        ICliCommand info = Registry.Commands[0];
        string text = HelpText.ForCommand(info);

        Assert.Contains("dmg info - " + info.Summary, text, StringComparison.Ordinal);
        Assert.Contains("Usage: dmg info [OPTIONS] IMAGE", text, StringComparison.Ordinal);

        foreach (OptionSpec option in info.Spec.Options)
        {
            Assert.Contains(option.Syntax, text, StringComparison.Ordinal);
            Assert.Contains(option.Description, text, StringComparison.Ordinal);
        }

        Assert.Contains("Exits 0 even for an image it cannot mount.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerbWithNoOptionsGetsNoOptionsHeading()
    {
        string text = HelpText.ForCommand(Registry.Commands[1]);

        Assert.Contains("Usage: dmg version", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Options:\n", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Global options:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionColumnIsAligned()
    {
        IReadOnlyList<string> rows =
        [
            .. HelpText.ForTool(Registry)
                .ReplaceLineEndings("\n")
                .Split('\n')
                .SkipWhile(line => !line.StartsWith("Commands:", StringComparison.Ordinal))
                .Skip(1)
                .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)),
        ];

        Assert.Equal(Registry.Commands.Count, rows.Count);

        // Every description has to start in the same column. Aligned to the widest
        // entry rather than to a fixed number, so a long verb name reflows the
        // block instead of pushing one line out of line with the rest.
        Assert.Single(rows.Select(DescriptionColumn).Distinct());
    }

    [Fact]
    public void WritingHelpPutsItOnStdoutOneLineAtATime()
    {
        RecordingOutput output = new();

        HelpText.WriteTo(output.Output, HelpText.ForTool(Registry));

        Assert.Empty(output.Stderr);
        Assert.Equal(HelpText.Tagline, output.StdoutLines[0]);
        Assert.DoesNotContain(output.StdoutLines, line => line.Contains('\n', StringComparison.Ordinal));
    }

    /// <summary>Where the second column starts on a two-column help row.</summary>
    private static int DescriptionColumn(string row)
    {
        int gap = row.IndexOf("   ", StringComparison.Ordinal);

        return row.Length - row[gap..].TrimStart().Length;
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => HelpText.ForTool(null!));
        Assert.Throws<ArgumentNullException>(() => HelpText.ForCommand(null!));
        Assert.Throws<ArgumentNullException>(() => HelpText.WriteTo(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => HelpText.WriteTo(new RecordingOutput().Output, null!));
    }
}
