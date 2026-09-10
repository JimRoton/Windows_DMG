using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Parsing;

/// <summary>
/// The switches that decide how the tool talks, lifted off the line before the verb
/// is even found.
/// </summary>
public sealed class GlobalOptionsTests
{
    [Fact]
    public void NothingGivenIsNormalAndNotJson()
    {
        GlobalOptions globals = Ok(["info", "image.dmg"]);

        Assert.Equal(Verbosity.Normal, globals.Verbosity);
        Assert.False(globals.IsJson);
        Assert.Equal(["info", "image.dmg"], globals.Remaining);
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("-q")]
    public void QuietIsRecognisedInBothSpellings(string switchText)
    {
        Assert.Equal(Verbosity.Quiet, Ok(["info", switchText]).Verbosity);
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("-v")]
    public void VerboseIsRecognisedInBothSpellings(string switchText)
    {
        Assert.Equal(Verbosity.Verbose, Ok(["info", switchText]).Verbosity);
    }

    [Fact]
    public void JsonIsRecognised()
    {
        Assert.True(Ok(["info", "--json", "image.dmg"]).IsJson);
    }

    [Fact]
    public void TheSwitchesAreRemovedWhereverTheyAppear()
    {
        GlobalOptions globals = Ok(["--json", "info", "-v", "image.dmg"]);

        Assert.Equal(["info", "image.dmg"], globals.Remaining);
        Assert.True(globals.IsJson);
        Assert.Equal(Verbosity.Verbose, globals.Verbosity);
    }

    [Fact]
    public void QuietAndVerboseTogetherIsAUsageError()
    {
        Result<GlobalOptions> extracted = GlobalOptions.Extract(["info", "--quiet", "--verbose"]);

        Assert.False(extracted.Ok);
        Assert.Equal(DmgExitCode.UsageError, extracted.Error.Code);
        Assert.Contains("contradict each other", extracted.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingASwitchIsHarmless()
    {
        Assert.Equal(Verbosity.Quiet, Ok(["info", "-q", "--quiet"]).Verbosity);
    }

    [Fact]
    public void TheTerminatorProtectsAValueSpelledLikeASwitch()
    {
        GlobalOptions globals = Ok(["info", "--", "--json"]);

        Assert.False(globals.IsJson);
        Assert.Equal(["info", "--", "--json"], globals.Remaining);
    }

    [Fact]
    public void TheTerminatorItselfIsLeftOnTheLineForTheVerbToSee()
    {
        Assert.Contains("--", Ok(["info", "--", "x"]).Remaining);
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => GlobalOptions.Extract(null!));
    }

    private static GlobalOptions Ok(string[] arguments)
    {
        Result<GlobalOptions> extracted = GlobalOptions.Extract(arguments);

        Assert.True(extracted.Ok);

        return extracted.Value!;
    }
}
