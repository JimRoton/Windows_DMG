using Dmg.Cli.Parsing;

namespace Dmg.Cli.Tests.Parsing;

/// <summary>
/// The spec is what both the parser and the help read, so its lookups and the usage
/// line it renders are worth pinning on their own.
/// </summary>
public sealed class CommandLineSpecTests
{
    [Fact]
    public void RendersTheUsageLine()
    {
        CommandLineSpec spec = new(
            "info",
            [new OptionSpec("json", "JSON to stdout.")],
            ["IMAGE"]);

        Assert.Equal("dmg info [OPTIONS] IMAGE", spec.UsageLine);
    }

    [Fact]
    public void OmitsTheOptionsAndArgumentsItDoesNotHave()
    {
        Assert.Equal("dmg version", new CommandLineSpec("version", []).UsageLine);
    }

    [Fact]
    public void FindsOptionsByBothNames()
    {
        CommandLineSpec spec = new("info", [new OptionSpec("quiet", "Errors only.", 'q')]);

        Assert.True(spec.TryGetLong("quiet", out OptionSpec? byLong));
        Assert.True(spec.TryGetShort('q', out OptionSpec? byShort));
        Assert.Same(byLong, byShort);

        Assert.False(spec.TryGetLong("loud", out _));
        Assert.False(spec.TryGetShort('x', out _));
    }

    [Fact]
    public void RefusesADuplicateLongOrShortName()
    {
        Assert.Throws<ArgumentException>(() => new CommandLineSpec(
            "info",
            [new OptionSpec("json", "a"), new OptionSpec("json", "b")]));

        Assert.Throws<ArgumentException>(() => new CommandLineSpec(
            "info",
            [new OptionSpec("json", "a", 'j'), new OptionSpec("jump", "b", 'j')]));
    }

    [Fact]
    public void RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new CommandLineSpec("info", null!));
        Assert.Throws<ArgumentNullException>(() => new CommandLineSpec("info", [null!]));
        Assert.Throws<ArgumentException>(() => new CommandLineSpec("  ", []));
    }

    [Theory]
    [InlineData("json", null, null, "--json")]
    [InlineData("quiet", 'q', null, "-q, --quiet")]
    [InlineData("partition", 'p', "N", "-p, --partition N")]
    [InlineData("password-env", null, "VAR", "--password-env VAR")]
    public void AnOptionRendersItsSyntaxForHelp(string name, char? shortName, string? valueName, string expected)
    {
        OptionSpec option = new(name, "description", shortName, valueName);

        Assert.Equal(expected, option.Syntax);
        Assert.Equal(valueName is not null, option.TakesValue);
    }

    [Fact]
    public void AnOptionNeedsAName()
    {
        Assert.Throws<ArgumentException>(() => new OptionSpec("  ", "description"));
    }

    [Fact]
    public void AVerbMayNotClaimHelp()
    {
        // The dispatcher answers --help and -h before a verb's parse runs, so a
        // verb that declared either would find it unreachable. Better a build
        // failure than an option that silently does nothing.
        Assert.Throws<ArgumentException>(() =>
            new CommandLineSpec("info", [new OptionSpec("help", "Mine now.")]));

        Assert.Throws<ArgumentException>(() =>
            new CommandLineSpec("info", [new OptionSpec("hidden", "Mine now.", 'h')]));
    }

    [Fact]
    public void NotesAreCarriedThroughForTheHelpToPrint()
    {
        CommandLineSpec spec = new("info", [], ["IMAGE"], ["Exits 0 even when it cannot mount."]);

        Assert.Equal(["Exits 0 even when it cannot mount."], spec.Notes);
    }

    [Fact]
    public void ASpecWithoutNotesHasNone()
    {
        Assert.Empty(new CommandLineSpec("info", []).Notes);
    }
}
