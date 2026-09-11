using Dmg.Core;
using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>
/// The single canonical form for a drive letter, and the shapes it is read from and
/// rendered into.
/// </summary>
public sealed class DriveLetterTests
{
    [Theory]
    [InlineData("E", "E")]
    [InlineData("e", "E")]
    [InlineData("E:", "E")]
    [InlineData("e:", "E")]
    [InlineData(@"E:\", "E")]
    [InlineData("E:/", "E")]
    [InlineData("  E  ", "E")]
    [InlineData(" e: ", "E")]
    public void NormaliseReducesEveryShapeToTheSameCanonicalLetter(string input, string expected)
    {
        Assert.Equal(expected, DriveLetter.Normalise(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("EE")]
    [InlineData("E:E")]
    [InlineData(@"C:\mnt\image")]
    public void NormaliseRejectsAnythingThatIsNotADriveLetter(string? input)
    {
        Assert.Null(DriveLetter.Normalise(input));
        Assert.False(DriveLetter.IsDriveLetter(input));
    }

    [Fact]
    public void ParseSucceedsWithTheCanonicalForm()
    {
        Result<string> parsed = DriveLetter.Parse("x:");

        Assert.True(parsed.TryGetValue(out string? letter));
        Assert.Equal("X", letter);
    }

    [Fact]
    public void ParseFailsWithAUsageErrorNotAMountFailure()
    {
        // Nothing has been attempted yet - the command line itself is wrong - so a
        // two-gigabyte decode is never thrown away over a typo in --letter.
        Result<string> parsed = DriveLetter.Parse("nope");

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.UsageError, parsed.Error.Code);
        Assert.Contains("nope", parsed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReportsAMissingValueDistinctlyFromABadOne()
    {
        Result<string> parsed = DriveLetter.Parse(null);

        Assert.False(parsed.Ok);
        Assert.Equal("no value given", parsed.Error.Detail!);
    }

    [Fact]
    public void WithColonAndRootFormatTheCanonicalLetterForTheApisThatWantThem()
    {
        Assert.Equal("E:", DriveLetter.WithColon("E"));
        Assert.Equal(@"E:\", DriveLetter.Root("E"));
    }

    [Theory]
    [InlineData("e")]
    [InlineData("E:")]
    [InlineData(@"E:\")]
    public void WithColonAndRootRejectAnythingThatIsNotAlreadyCanonical(string nonCanonical)
    {
        Assert.Throws<ArgumentException>(() => DriveLetter.WithColon(nonCanonical));
        Assert.Throws<ArgumentException>(() => DriveLetter.Root(nonCanonical));
    }

    [Theory]
    [InlineData("A", 0)]
    [InlineData("C", 2)]
    [InlineData("Z", 25)]
    public void BitPositionMatchesGetLogicalDrivesLayout(string letter, int expected)
    {
        Assert.Equal(expected, DriveLetter.BitPosition(letter));
    }
}
