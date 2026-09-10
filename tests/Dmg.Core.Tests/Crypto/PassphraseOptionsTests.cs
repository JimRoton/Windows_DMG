using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// Parsing the three ways a passphrase can be asked for on the command line - and
/// the fourth way, <c>--password &lt;value&gt;</c>, which does not exist and is
/// refused by name rather than silently ignored.
/// </summary>
public sealed class PassphraseOptionsTests
{
    [Fact]
    public void NoPassphraseOptionMeansUnspecified()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["info", "image.dmg"]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PassphraseSource.Unspecified, options.Source);
        Assert.Null(options.EnvironmentVariable);
        Assert.Equal(["info", "image.dmg"], options.RemainingArguments);
    }

    [Fact]
    public void PasswordStdinIsRecognisedAndRemoved()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["mount", "--password-stdin", "image.dmg"]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PassphraseSource.StandardInput, options.Source);
        Assert.Equal(["mount", "image.dmg"], options.RemainingArguments);
    }

    [Fact]
    public void PasswordEnvAsTwoArgumentsIsRecognisedAndRemoved()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["mount", "--password-env", "DMG_PASSPHRASE", "image.dmg"]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PassphraseSource.Environment, options.Source);
        Assert.Equal("DMG_PASSPHRASE", options.EnvironmentVariable);
        Assert.Equal(["mount", "image.dmg"], options.RemainingArguments);
    }

    [Fact]
    public void PasswordEnvWithAnEqualsSignIsRecognisedAndRemoved()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["--password-env=DMG_PASSPHRASE", "image.dmg"]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PassphraseSource.Environment, options.Source);
        Assert.Equal("DMG_PASSPHRASE", options.EnvironmentVariable);
        Assert.Equal(["image.dmg"], options.RemainingArguments);
    }

    [Fact]
    public void PasswordEnvWithoutANameIsAUsageError()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["--password-env"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void PasswordEnvWithAnEmptyNameIsAUsageError()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["--password-env", "  "]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void PasswordEnvWithAnEmptyNameAfterTheEqualsSignIsAUsageError()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["--password-env="]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Theory]
    [InlineData("--password", "secret")]
    [InlineData("--passphrase", "secret")]
    [InlineData("-p", "secret")]
    [InlineData("--pass", "secret")]
    public void EveryRejectedOptionIsRefusedByName(string option, string value)
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse([option, value]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
        Assert.Contains(option, result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("--password-env", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedOptionWithAnEqualsSignIsAlsoCaught()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["--password=secret"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void ARejectedOptionSaysThePassphraseIsAlreadyExposed()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(["--password", "hunter2"]);

        Assert.False(result.Ok);
        Assert.Contains("exposed", result.Error.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StdinThenEnvIsAConflict()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["--password-stdin", "--password-env", "VAR"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void EnvThenStdinIsAConflict()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["--password-env", "VAR", "--password-stdin"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void StdinGivenTwiceIsAConflict()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["--password-stdin", "--password-stdin"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void RemainingArgumentsPreserveTheirOriginalOrder()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["mount", "--password-stdin", "image.dmg", "--verbose"]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(["mount", "image.dmg", "--verbose"], options.RemainingArguments);
    }

    [Fact]
    public void EmptyArgumentsParseToUnspecifiedWithNothingRemaining()
    {
        Result<PassphraseOptions> result = PassphraseOptions.Parse([]);

        Assert.True(result.TryGetValue(out PassphraseOptions? options), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PassphraseSource.Unspecified, options.Source);
        Assert.Empty(options.RemainingArguments);
    }

    [Fact]
    public void ParseRejectsANullArgumentList() =>
        Assert.Throws<ArgumentNullException>(() => PassphraseOptions.Parse(null!));

    [Fact]
    public void RejectedOptionsAreCheckedBeforeTheRealOnesSoNoneCanHide()
    {
        // A rejected option earlier in the line must not be masked by a real one
        // parsed first - the whole point is that typing it at all is the mistake.
        Result<PassphraseOptions> result = PassphraseOptions.Parse(
            ["--password", "secret", "--password-stdin"]);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }
}
