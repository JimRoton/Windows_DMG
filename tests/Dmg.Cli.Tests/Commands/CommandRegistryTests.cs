using Dmg.Cli.Commands;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The registry's job is lookup and the refusal of a wiring bug. Both are here.
/// </summary>
public sealed class CommandRegistryTests
{
    [Fact]
    public void FindsARegisteredVerb()
    {
        FakeCommand info = new("info");
        CommandRegistry registry = new([info, new FakeCommand("mount")]);

        Assert.True(registry.TryGet("info", out ICliCommand? found));
        Assert.Same(info, found);
    }

    [Fact]
    public void DoesNotFindAVerbThatIsNotThere()
    {
        CommandRegistry registry = new([new FakeCommand("info")]);

        Assert.False(registry.TryGet("mount", out ICliCommand? found));
        Assert.Null(found);
    }

    [Fact]
    public void KeepsRegistrationOrderForTheHelpListing()
    {
        CommandRegistry registry = new(
        [
            new FakeCommand("info"),
            new FakeCommand("mount"),
            new FakeCommand("unmount"),
        ]);

        Assert.Equal(["info", "mount", "unmount"], registry.Verbs);
    }

    [Fact]
    public void RefusesTwoCommandsClaimingTheSameVerb()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() =>
            new CommandRegistry([new FakeCommand("info"), new FakeCommand("info")]));

        Assert.Contains("both claim the verb 'info'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesABlankVerb()
    {
        Assert.Throws<ArgumentException>(() => new CommandRegistry([new FakeCommand("   ")]));
    }

    [Fact]
    public void RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new CommandRegistry(null!));
        Assert.Throws<ArgumentNullException>(() => new CommandRegistry([null!]));
    }

    [Fact]
    public void AnEmptyRegistryIsLegal()
    {
        CommandRegistry registry = new([]);

        Assert.Empty(registry.Verbs);
        Assert.False(registry.TryGet("info", out _));
    }
}
