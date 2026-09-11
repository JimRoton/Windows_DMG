using System.Text.Json;
using Dmg.Cli.Commands;
using Dmg.Cli.Tests.Info;
using Dmg.Core;
using Dmg.Core.Crypto;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The verb end to end: real images in, the right stream and the right exit code
/// out. Every fixture-backed test degrades to a pass when the images have not been
/// generated.
/// </summary>
public sealed class InfoCommandTests
{
    private static readonly InfoCommand Command = new();

    [Theory]
    [InlineData("exfat-zlib.dmg")]
    [InlineData("hfsplus.dmg")]
    [InlineData("apfs.dmg")]
    [InlineData("bzip2.dmg")]
    [InlineData("exfat-raw.dmg")]
    public void EveryImageItCanDescribeIsExitZero(string fixture)
    {
        if (Fixtures.Path(fixture) is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        // The contract that matters: an image that cannot be decoded, and one that
        // cannot be mounted, are both questions answered. Only a question it could
        // not answer is a failure.
        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.NotEmpty(output.StdoutLines);
    }

    [Fact]
    public void TheDescriptionGoesToStdoutAndNothingGoesToStderr()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Command.Execute(new CliContext([path], output.Output));

        Assert.Empty(output.Stderr);
        Assert.Contains(output.StdoutLines, line => line.Contains("exFAT", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndecodableImageIsDescribedAndTheReasonIsOnTheScreen()
    {
        if (Fixtures.Path("bzip2.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains(output.StdoutLines, line => line.Contains("bzip2", StringComparison.Ordinal));
        Assert.Contains(output.StdoutLines, line => line.Contains("not read", StringComparison.Ordinal));

        // The note is in the report, where a reader is looking for it. A second
        // copy on stderr would be a duplicate whenever both are the same terminal.
        Assert.Empty(output.Stderr);
    }

    [Fact]
    public void JsonModeWritesOneParseableDocumentAndNothingElse()
    {
        if (Fixtures.Path("hfsplus.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new(isJson: true);

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Empty(output.Stderr);

        using JsonDocument document = JsonDocument.Parse(output.Stdout);
        JsonElement root = document.RootElement;

        Assert.Equal("Udif", root.GetProperty("format").GetString());
        Assert.True(root.GetProperty("canDecode").GetBoolean());
        Assert.False(root.GetProperty("canMount").GetBoolean());
        Assert.Equal("gpt", root.GetProperty("partitioning").GetString());

        JsonElement partition = root.GetProperty("partitions")[0];

        Assert.Equal("HFS+", partition.GetProperty("filesystem").GetString());
        Assert.False(partition.GetProperty("mountable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(partition.GetProperty("refusal").GetString()));
    }

    [Fact]
    public void JsonCarriesTheReasonWhenThePartitioningCouldNotBeRead()
    {
        if (Fixtures.Path("bzip2.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new(isJson: true);

        Command.Execute(new CliContext([path], output.Output));

        using JsonDocument document = JsonDocument.Parse(output.Stdout);
        JsonElement root = document.RootElement;

        // An empty list with no note means "no partitions"; an empty list with a
        // note means "could not tell". A caller has to be able to distinguish them.
        Assert.False(root.GetProperty("canDecode").GetBoolean());
        Assert.Empty(root.GetProperty("partitions").EnumerateArray());
        Assert.Contains(
            "bzip2",
            root.GetProperty("partitioningNote").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("partitioning").ValueKind);
    }

    [Fact]
    public void APassphraseFromTheEnvironmentOpensAnEncryptedImage()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        InfoCommand command = new(
            new Cli.Info.ImageInspector(),
            new PassphraseReader(environment: name =>
                name == "DMG_TEST_PASSPHRASE" ? "dmg-test-passphrase" : null));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext(
                [path, "--password-env", "DMG_TEST_PASSPHRASE"],
                output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("AES-256", StringComparison.Ordinal));
        Assert.Contains(output.StdoutLines, line => line.Contains("exFAT", StringComparison.Ordinal));
    }

    [Fact]
    public void AWrongPassphraseIsExitFour()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        InfoCommand command = new(
            new Cli.Info.ImageInspector(),
            new PassphraseReader(environment: _ => "not the passphrase"));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            command.Execute(new CliContext([path, "--password-env", "V"], output.Output)));

        Assert.Empty(output.Stdout);
        Assert.NotEmpty(output.Stderr);
    }

    [Fact]
    public void AnEncryptedImageWithNoPassphraseIsStillExitZero()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        // It does not stop to prompt when nobody may be watching; it describes the
        // wrapper and says what it could not see past it.
        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains(output.StdoutLines, line => line.Contains("AES-256", StringComparison.Ordinal));
    }

    [Fact]
    public void NoImageIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.UsageError, Command.Execute(new CliContext([], output.Output)));
        Assert.Contains("needs an image", output.Stderr, StringComparison.Ordinal);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void TwoImagesIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg", "b.dmg"], output.Output)));

        Assert.Contains("takes one argument", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPassphraseSourcesAtOnceIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(
                ["a.dmg", "--password-stdin", "--password-env", "V"],
                output.Output)));

        Assert.Contains("alternatives", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void APasswordOnTheCommandLineIsNotEvenAnOption()
    {
        RecordingOutput output = new();

        // The parser rejects it as unknown, which is the point: there is no way to
        // put a passphrase in shell history through this verb.
        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg", "--password", "hunter2"], output.Output)));

        Assert.Contains("--password is not an option of 'info'", output.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsExitTwo()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["no-such-image.dmg"], output.Output)));
    }

    [Fact]
    public void QuietStillMeansQuiet()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new(Verbosity.Quiet);

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void ItsHelpNamesEveryOptionItAccepts()
    {
        Assert.Equal("info", Command.Spec.Verb);
        Assert.Equal(["IMAGE"], Command.Spec.Positionals);
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-stdin");
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-env");
        Assert.NotEmpty(Command.Spec.Notes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Command.Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new InfoCommand(null!, new PassphraseReader()));
        Assert.Throws<ArgumentNullException>(() => new InfoCommand(new Cli.Info.ImageInspector(), null!));
    }
}
