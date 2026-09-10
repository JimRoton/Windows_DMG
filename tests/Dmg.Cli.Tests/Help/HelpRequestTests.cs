using Dmg.Cli.Help;

namespace Dmg.Cli.Tests.Help;

/// <summary>
/// One predicate, two callers, so it is worth pinning on its own: the sink is built
/// from it and the routing is decided by it, and they must never disagree.
/// </summary>
public sealed class HelpRequestTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void EitherSpellingCounts(string spelling)
    {
        Assert.True(HelpRequest.IsRequestedIn([spelling]));
        Assert.True(HelpRequest.IsRequestedIn(["info", "image.dmg", spelling]));
        Assert.True(HelpRequest.IsRequestedIn([spelling, "info"]));
    }

    [Fact]
    public void NothingCountsWhenNothingAsked()
    {
        Assert.False(HelpRequest.IsRequestedIn([]));
        Assert.False(HelpRequest.IsRequestedIn(["info", "image.dmg", "--json"]));
    }

    [Fact]
    public void TheTerminatorEndsTheScan()
    {
        // `dmg info -- --help` opens a file called --help. Improbable, but it is the
        // whole point of the terminator.
        Assert.False(HelpRequest.IsRequestedIn(["info", "--", "--help"]));
    }

    [Fact]
    public void AClusteredHCountsForNothing()
    {
        // Reading -qh would mean knowing which letters take values, which means
        // knowing the verb, which is what this runs before.
        Assert.False(HelpRequest.IsRequestedIn(["-qh"]));
    }

    [Fact]
    public void NullsAreRejectedAndNullElementsIgnored()
    {
        Assert.Throws<ArgumentNullException>(() => HelpRequest.IsRequestedIn(null!));
        Assert.True(HelpRequest.IsRequestedIn([null!, "--help"]));
    }
}
