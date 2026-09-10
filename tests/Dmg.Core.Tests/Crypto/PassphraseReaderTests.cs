using System.Text;
using Dmg.Core.Crypto;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// Getting the passphrase from wherever <see cref="PassphraseOptions"/> says,
/// without it ever becoming a string the process cannot scrub - and the one
/// exception the type itself documents, the environment path, where a string is
/// unavoidable.
/// </summary>
public sealed class PassphraseReaderTests
{
    [Fact]
    public void StandardInputReadsWhateverIsThere()
    {
        PassphraseReader reader = new(standardInput: () => Stream("hello"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello", Text(passphrase));
    }

    [Fact]
    public void StandardInputStripsExactlyOneTrailingNewline()
    {
        PassphraseReader reader = new(standardInput: () => Stream("hello\n"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello", Text(passphrase));
    }

    [Fact]
    public void StandardInputStripsACarriageReturnBeforeTheNewlineToo()
    {
        PassphraseReader reader = new(standardInput: () => Stream("hello\r\n"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello", Text(passphrase));
    }

    [Fact]
    public void StandardInputDoesNotStripACarriageReturnOnItsOwn()
    {
        // Only a newline - optionally preceded by a carriage return - counts as the
        // terminator. A lone \r is part of the passphrase.
        PassphraseReader reader = new(standardInput: () => Stream("hello\r"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello\r", Text(passphrase));
    }

    [Fact]
    public void StandardInputKeepsATrailingSpace()
    {
        // Only a newline is trimmed - trailing whitespace that is not a newline is
        // part of a passphrase someone genuinely typed that way.
        PassphraseReader reader = new(standardInput: () => Stream("hello  "));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello  ", Text(passphrase));
    }

    [Fact]
    public void StandardInputStripsOnlyOneNewlineNotAllTrailingOnes()
    {
        PassphraseReader reader = new(standardInput: () => Stream("hello\n\n"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal("hello\n", Text(passphrase));
    }

    [Fact]
    public void AnEmptyStandardInputIsAnEmptyPassphrase()
    {
        PassphraseReader reader = new(standardInput: () => Stream(""));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal(0, passphrase.Length);
    }

    [Fact]
    public void StandardInputThatIsJustANewlineIsAnEmptyPassphrase()
    {
        PassphraseReader reader = new(standardInput: () => Stream("\n"));

        using Passphrase passphrase = Read(reader, PassphraseSource.StandardInput);

        Assert.Equal(0, passphrase.Length);
    }

    [Fact]
    public void AFailureOpeningStandardInputIsAUsageError()
    {
        PassphraseReader reader = new(standardInput: () => throw new IOException("pipe closed"));

        Result<Passphrase> result = reader.Read(new PassphraseOptions(
            PassphraseSource.StandardInput,
            EnvironmentVariable: null,
            RemainingArguments: []));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void EnvironmentReadsTheNamedVariable()
    {
        PassphraseReader reader = new(environment: name => name == "DMG_PASSPHRASE" ? "swordfish" : null);

        using Passphrase passphrase = Read(reader, PassphraseSource.Environment, "DMG_PASSPHRASE");

        Assert.Equal("swordfish", Text(passphrase));
    }

    [Fact]
    public void AnUnsetEnvironmentVariableIsAUsageErrorNamingIt()
    {
        PassphraseReader reader = new(environment: _ => null);

        Result<Passphrase> result = reader.Read(new PassphraseOptions(
            PassphraseSource.Environment,
            "DMG_PASSPHRASE",
            RemainingArguments: []));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
        Assert.Contains("DMG_PASSPHRASE", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyEnvironmentVariableIsAUsageError()
    {
        PassphraseReader reader = new(environment: _ => "");

        Result<Passphrase> result = reader.Read(new PassphraseOptions(
            PassphraseSource.Environment,
            "DMG_PASSPHRASE",
            RemainingArguments: []));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void AMissingEnvironmentVariableNameIsAUsageError()
    {
        // Parse never produces this - it always fills EnvironmentVariable when the
        // source is Environment - but Read is a public entry point in its own
        // right and must not trust a hand-built options record either.
        PassphraseReader reader = new(environment: _ => "irrelevant");

        Result<Passphrase> result = reader.Read(new PassphraseOptions(
            PassphraseSource.Environment,
            EnvironmentVariable: null,
            RemainingArguments: []));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
    }

    [Fact]
    public void UnspecifiedFallsBackToThePromptFunction()
    {
        bool promptWasAsked = false;

        PassphraseReader reader = new(prompt: text =>
        {
            promptWasAsked = true;
            return Result<Passphrase>.Success(Passphrase.FromString($"typed:{text}"));
        });

        using Passphrase passphrase = Read(reader, PassphraseSource.Unspecified, prompt: "Enter it: ");

        Assert.True(promptWasAsked);
        Assert.Equal("typed:Enter it: ", Text(passphrase));
    }

    [Fact]
    public void TheDefaultPromptTextIsUsedWhenNoneIsGiven()
    {
        string? seen = null;

        PassphraseReader reader = new(prompt: text =>
        {
            seen = text;
            return Result<Passphrase>.Success(Passphrase.Adopt([1]));
        });

        using Passphrase passphrase = reader.Read(new PassphraseOptions(
                PassphraseSource.Unspecified,
                EnvironmentVariable: null,
                RemainingArguments: []))
            .Value!;

        Assert.Equal(PassphraseReader.DefaultPrompt, seen);
    }

    [Fact]
    public void ReadRejectsNullOptions()
    {
        PassphraseReader reader = new();

        Assert.Throws<ArgumentNullException>(() => reader.Read(null!));
    }

    [Fact]
    public void TheRealConsolePromptRefusesWhenInputIsRedirected()
    {
        // The default prompt function - not an injected one - reached through
        // Unspecified. Every CI runner and every xunit host has its input
        // redirected, so this exercises Console.IsInputRedirected for real rather
        // than mocking it away.
        PassphraseReader reader = new();

        Result<Passphrase> result = reader.Read(new PassphraseOptions(
            PassphraseSource.Unspecified,
            EnvironmentVariable: null,
            RemainingArguments: []));

        if (!Console.IsInputRedirected)
        {
            // A genuinely interactive test host: nothing to assert without typing,
            // so this environment cannot exercise the redirected-input branch.
            return;
        }

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
        Assert.Contains("--password-stdin", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("--password-env", result.Error.Message, StringComparison.Ordinal);
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);

    private static string Text(Passphrase passphrase) => Encoding.UTF8.GetString(passphrase.Bytes);

    private static Passphrase Read(
        PassphraseReader reader,
        PassphraseSource source,
        string? environmentVariable = null,
        string prompt = PassphraseReader.DefaultPrompt)
    {
        Result<Passphrase> result = reader.Read(
            new PassphraseOptions(source, environmentVariable, RemainingArguments: []),
            prompt);

        Assert.True(result.TryGetValue(out Passphrase? passphrase), result.Ok ? "" : result.Error.ToString());

        return passphrase;
    }
}
