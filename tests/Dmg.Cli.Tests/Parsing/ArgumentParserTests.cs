using Dmg.Cli.Parsing;
using Dmg.Core;

namespace Dmg.Cli.Tests.Parsing;

/// <summary>
/// The whole grammar, one case at a time: flags, values in both spellings, short
/// forms and their clusters, the terminator, and every way the line can be wrong.
/// </summary>
public sealed class ArgumentParserTests
{
    private static readonly CommandLineSpec Spec = new(
        "info",
        [
            new OptionSpec("json", "JSON to stdout."),
            new OptionSpec("quiet", "Errors only.", 'q'),
            new OptionSpec("partition", "Which partition.", 'p', "N"),
            new OptionSpec("password-env", "Read the passphrase from a variable.", ValueName: "VAR"),
            new OptionSpec("tag", "A repeatable label.", 't', "TAG", AllowMultiple: true),
        ],
        ["IMAGE"]);

    [Fact]
    public void AnEmptyLineParsesToNothing()
    {
        ParsedArguments parsed = Ok([]);

        Assert.Empty(parsed.Positionals);
        Assert.False(parsed.Has("json"));
    }

    [Fact]
    public void ALongFlagIsRecordedWithoutAValue()
    {
        ParsedArguments parsed = Ok(["--json"]);

        Assert.True(parsed.Has("json"));
        Assert.False(parsed.TryGetValue("json", out _));
    }

    [Fact]
    public void ALongOptionTakesTheNextArgument()
    {
        ParsedArguments parsed = Ok(["--partition", "2", "image.dmg"]);

        Assert.True(parsed.TryGetValue("partition", out string? value));
        Assert.Equal("2", value);
        Assert.Equal(["image.dmg"], parsed.Positionals);
    }

    [Fact]
    public void ALongOptionAlsoTakesAnInlineValue()
    {
        ParsedArguments parsed = Ok(["--partition=2", "image.dmg"]);

        Assert.True(parsed.TryGetValue("partition", out string? value));
        Assert.Equal("2", value);
        Assert.Equal(["image.dmg"], parsed.Positionals);
    }

    [Fact]
    public void AnInlineValueMayBeEmptyAndMayContainEqualsSigns()
    {
        Assert.Equal(string.Empty, Value(Ok(["--partition="]), "partition"));
        Assert.Equal("a=b", Value(Ok(["--password-env=a=b"]), "password-env"));
    }

    [Fact]
    public void AShortFlagIsTheSameAsItsLongForm()
    {
        Assert.True(Ok(["-q"]).Has("quiet"));
    }

    [Fact]
    public void ShortFlagsCluster()
    {
        // -q is the only short flag in this spec, so the cluster pairs it with the
        // short form of an option that takes a value, given last.
        ParsedArguments parsed = Ok(["-qp", "3"]);

        Assert.True(parsed.Has("quiet"));
        Assert.Equal("3", Value(parsed, "partition"));
    }

    [Fact]
    public void AShortOptionTakesTheNextArgumentOrAnInlineValue()
    {
        Assert.Equal("2", Value(Ok(["-p", "2"]), "partition"));
        Assert.Equal("2", Value(Ok(["-p=2"]), "partition"));
    }

    [Fact]
    public void AShortOptionThatTakesAValueMustEndItsCluster()
    {
        DmgError error = Fails(["-pq", "2"]);

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("has to come last", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTerminatorEndsTheOptions()
    {
        ParsedArguments parsed = Ok(["--json", "--", "--partition"]);

        Assert.True(parsed.Has("json"));
        Assert.False(parsed.Has("partition"));
        Assert.Equal(["--partition"], parsed.Positionals);
    }

    [Fact]
    public void TheTerminatorDoesNotExcuseAPositionalTooMany()
    {
        // `--` stops options being read. It does not turn `info` into a verb that
        // takes two images - and the message proves both words landed as
        // positionals rather than as --partition and -q.
        DmgError error = Fails(["--", "--partition", "-q"]);

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("takes one argument", error.Message, StringComparison.Ordinal);
        Assert.Contains("'--partition', '-q'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABareDashIsAPositional()
    {
        Assert.Equal(["-"], Ok(["-"]).Positionals);
    }

    [Fact]
    public void PositionalsKeepTheirOrder()
    {
        CommandLineSpec spec = new("x", [], ["A", "B"]);
        Result<ParsedArguments> parsed = ArgumentParser.Parse(spec, ["first", "second"]);

        Assert.Equal(["first", "second"], parsed.Value!.Positionals);
    }

    [Fact]
    public void AnUnknownLongOptionSuggestsTheNearestMatch()
    {
        DmgError error = Fails(["--jsn"]);

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("--jsn is not an option of 'info'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean --json?", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownLongOptionWithNothingCloseJustSaysSo()
    {
        DmgError error = Fails(["--zzzzzzzz"]);

        Assert.Contains("is not an option of 'info'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownShortOptionIsNamedAsTyped()
    {
        DmgError error = Fails(["-z"]);

        Assert.Contains("-z is not an option of 'info'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionAtTheEndOfTheLineWithNoValueIsAUsageError()
    {
        Assert.Contains("needs a value", Fails(["--partition"]).Message, StringComparison.Ordinal);
        Assert.Contains("needs a value", Fails(["-p"]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivingAFlagAValueIsAUsageErrorRatherThanSilentlyIgnored()
    {
        DmgError error = Fails(["--json=true"]);

        Assert.Contains("is a switch and takes no value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingAnOptionThatMayNotRepeatIsAUsageError()
    {
        DmgError error = Fails(["--partition", "1", "--partition", "2"]);

        Assert.Contains("--partition was given more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatableOptionKeepsEveryValue()
    {
        ParsedArguments parsed = Ok(["--tag", "a", "-t", "b"]);

        Assert.Equal(["a", "b"], parsed.Values("tag"));
        Assert.Equal("b", Value(parsed, "tag"));
    }

    [Fact]
    public void TooManyPositionalsIsAUsageErrorThatShowsTheUsageLine()
    {
        DmgError error = Fails(["one.dmg", "two.dmg"]);

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("takes one argument", error.Message, StringComparison.Ordinal);
        Assert.Contains("dmg info [OPTIONS] IMAGE", error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerbThatDeclaresNoPositionalsAcceptsAnyNumberOfThem()
    {
        CommandLineSpec spec = new("help", []);

        Result<ParsedArguments> parsed = ArgumentParser.Parse(spec, ["info", "mount"]);

        Assert.True(parsed.Ok);
        Assert.Equal(["info", "mount"], parsed.Value!.Positionals);
    }

    [Fact]
    public void ALoneDoubleDashPrefixWithNoNameIsAUsageError()
    {
        Assert.Contains("is not an option name", Fails(["--=x"]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ArgumentParser.Parse(null!, []));
        Assert.Throws<ArgumentNullException>(() => ArgumentParser.Parse(Spec, null!));
    }

    [Fact]
    public void TryGetInt32ReadsANumberAndRefusesAnythingElse()
    {
        Assert.Equal(2, Ok(["--partition", "2"]).TryGetInt32("partition").Value);
        Assert.Null(Ok([]).TryGetInt32("partition").Value);

        Result<int?> bad = Ok(["--partition", "two"]).TryGetInt32("partition");

        Assert.False(bad.Ok);
        Assert.Equal(DmgExitCode.UsageError, bad.Error.Code);
    }

    [Fact]
    public void TryGetInt32RefusesASignedOrSpacedNumber()
    {
        Assert.False(Ok(["--partition", "-1"]).TryGetInt32("partition").Ok);
        Assert.False(Ok(["--partition", " 1"]).TryGetInt32("partition").Ok);
    }

    private static ParsedArguments Ok(string[] arguments)
    {
        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, arguments);

        Assert.True(parsed.Ok, parsed.Ok ? string.Empty : parsed.Error.ToString());

        return parsed.Value!;
    }

    private static DmgError Fails(string[] arguments)
    {
        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, arguments);

        Assert.False(parsed.Ok);

        return parsed.Error;
    }

    private static string Value(ParsedArguments parsed, string name)
    {
        Assert.True(parsed.TryGetValue(name, out string? value));

        return value!;
    }
}
