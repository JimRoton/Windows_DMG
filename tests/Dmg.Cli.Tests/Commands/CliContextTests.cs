using Dmg.Cli.Commands;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The context is a two-field record, and both fields are load-bearing enough that
/// a null in either should fail at construction rather than inside a verb.
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
    public void RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new CliContext(null!, new RecordingOutput().Output));
        Assert.Throws<ArgumentNullException>(() => new CliContext([], null!));
    }
}
