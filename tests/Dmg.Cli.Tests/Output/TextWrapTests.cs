using Dmg.Cli.Output;

namespace Dmg.Cli.Tests.Output;

/// <summary>
/// Greedy word wrap, pinned on the two edge cases the doc comments call out: a
/// word longer than the width is never broken, and an empty paragraph still
/// comes back as one line rather than none.
/// </summary>
public sealed class TextWrapTests
{
    [Fact]
    public void ShortTextFitsOnOneLine() =>
        Assert.Equal(["one two three"], TextWrap.Wrap("one two three", 40));

    [Fact]
    public void WordsThatWouldOverflowStartANewLine()
    {
        IReadOnlyList<string> lines = TextWrap.Wrap("one two three four", 9);

        Assert.Equal(["one two", "three", "four"], lines);
        Assert.All(lines, line => Assert.True(line.Length <= 9, line));
    }

    [Fact]
    public void AWordLongerThanTheWidthIsLeftWholeRatherThanBroken()
    {
        IReadOnlyList<string> lines = TextWrap.Wrap("a supercalifragilisticexpialidocious word", 8);

        Assert.Contains("supercalifragilisticexpialidocious", lines);
    }

    [Fact]
    public void ExistingLineBreaksAreNotHonoured() =>
        Assert.Equal(["one two three"], TextWrap.Wrap("one\ntwo\r\nthree", 40));

    [Fact]
    public void RepeatedWhitespaceCollapses() =>
        Assert.Equal(["one two"], TextWrap.Wrap("one    two", 40));

    [Fact]
    public void AnEmptyParagraphComesBackAsOneEmptyLine() =>
        Assert.Equal([string.Empty], TextWrap.Wrap(string.Empty, 40));

    [Fact]
    public void AParagraphOfOnlyWhitespaceComesBackAsOneEmptyLine() =>
        Assert.Equal([string.Empty], TextWrap.Wrap("   \t  ", 40));

    [Fact]
    public void NullTextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => TextWrap.Wrap(null!, 40));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWidthBelowOneIsRejected(int width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWrap.Wrap("x", width));

    [Fact]
    public void IndentPutsThePrefixOnEveryLine()
    {
        IReadOnlyList<string> lines = TextWrap.Indent("one two three four five six", "    ");

        Assert.All(lines, line => Assert.StartsWith("    ", line, StringComparison.Ordinal));
    }

    [Fact]
    public void IndentNarrowsTheWrapWidthByThePrefixLength()
    {
        string prefix = new(' ', TextWrap.LineWidth - 10);
        string longWord = new('x', 20);

        IReadOnlyList<string> lines = TextWrap.Indent($"a {longWord} b", prefix);

        // The prefix leaves only 10 columns, so even a short word wraps onto its
        // own line rather than sharing one with "a".
        Assert.True(lines.Count > 1);
        Assert.Contains(lines, line => line == prefix + "a");
    }

    [Fact]
    public void IndentNeverGoesBelowOneColumnOfWrapWidth()
    {
        string hugePrefix = new(' ', TextWrap.LineWidth + 50);

        // A prefix wider than the line width would make Wrap's width go
        // negative; Indent clamps it to at least 1 rather than throwing.
        IReadOnlyList<string> lines = TextWrap.Indent("one two three", hugePrefix);

        Assert.All(lines, line => Assert.StartsWith(hugePrefix, line, StringComparison.Ordinal));
    }

    [Fact]
    public void NullPrefixIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => TextWrap.Indent("x", null!));
}
