using Dmg.Cli.Parsing;

namespace Dmg.Cli.Tests.Parsing;

/// <summary>
/// A suggestion is only worth printing when it is likely to be right. These pin
/// both halves of that: what gets suggested, and what deliberately does not.
/// </summary>
public sealed class NearestMatchTests
{
    private static readonly string[] Options = ["json", "quiet", "verbose", "partition", "password-env"];

    [Theory]
    [InlineData("jsn", "json")]
    [InlineData("jsonn", "json")]
    [InlineData("qiuet", "quiet")]
    [InlineData("verbse", "verbose")]
    [InlineData("partitions", "partition")]
    public void SuggestsTheObviousTypo(string typed, string expected)
    {
        Assert.Equal(expected, NearestMatch.Find(typed, Options));
    }

    [Fact]
    public void PrefersAPrefixOverAnEditDistanceMatch()
    {
        // "part" is 5 edits from "partition" but is plainly a truncation of it.
        Assert.Equal("partition", NearestMatch.Find("part", Options));
    }

    [Fact]
    public void PrefersTheShortestCompletionOfAPrefix()
    {
        Assert.Equal("pass", NearestMatch.Find("pas", ["passphrase", "pass", "password"]));
    }

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData("mount")]
    public void SuggestsNothingWhenNothingIsClose(string typed)
    {
        Assert.Null(NearestMatch.Find(typed, Options));
    }

    [Fact]
    public void AShortWordToleratesOnlyOneEdit()
    {
        Assert.Equal("json", NearestMatch.Find("jso", Options));
        Assert.Null(NearestMatch.Find("abc", Options));
    }

    [Fact]
    public void AnEmptyInputSuggestsNothing()
    {
        Assert.Null(NearestMatch.Find(string.Empty, Options));
    }

    [Fact]
    public void AnEmptyCandidateListSuggestsNothing()
    {
        Assert.Null(NearestMatch.Find("json", []));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]
    // A swap of two neighbours costs one edit, not the two that a delete plus an
    // insert would. This is the whole reason the distance is not plain
    // Levenshtein: "qiuet" for "quiet" is otherwise too far to suggest.
    [InlineData("json", "jsno", 1)]
    [InlineData("quiet", "qiuet", 1)]
    // Restricted, so a substring is never edited twice: unrestricted Damerau would
    // call this 2.
    [InlineData("ca", "abc", 3)]
    public void DistanceCountsAnAdjacentSwapAsOneEdit(string left, string right, int expected)
    {
        Assert.Equal(expected, NearestMatch.Distance(left, right));
        Assert.Equal(expected, NearestMatch.Distance(right, left));
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => NearestMatch.Find(null!, Options));
        Assert.Throws<ArgumentNullException>(() => NearestMatch.Find("json", null!));
        Assert.Throws<ArgumentNullException>(() => NearestMatch.Distance(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => NearestMatch.Distance("a", null!));
    }
}
