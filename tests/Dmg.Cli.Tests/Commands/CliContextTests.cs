using Dmg.Cli.Commands;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The two required fields are load-bearing enough that a null in either should
/// fail at construction rather than inside a verb; the registry is optional and has
/// to survive being left out.
/// </summary>
public sealed class CliContextTests
{
    [Fact]
    public void CarriesTheArgumentsAndTheOutput()
    {
        RecordingOutput recorder = new();
        CliContext context = new(["--json", "image.dmg"], recorder.Output);

        Assert.Equal(["--json", "image.dmg"], context.Arguments);
        Assert.Same(recorder.Output, context.Output);
    }

    [Fact]
    public void TheRegistryIsOptionalAndNeverNull()
    {
        // Most tests exercise one verb and have no catalogue to give. A verb that
        // reaches for context.Registry must still get something it can enumerate.
        Assert.Empty(new CliContext([], new RecordingOutput().Output).Registry.Commands);
    }

    [Fact]
    public void TheRegistryIsCarriedThroughWhenGiven()
    {
        CommandRegistry registry = new([new FakeCommand("info")]);

        Assert.Same(registry, new CliContext([], new RecordingOutput().Output, registry).Registry);
    }

    [Fact]
    public void RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new CliContext(null!, new RecordingOutput().Output));
        Assert.Throws<ArgumentNullException>(() => new CliContext([], null!));
    }
}
