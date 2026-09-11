using Dmg.Cli.Commands;
using Dmg.Cli.Tests.Info;
using Dmg.Core;
using Dmg.Core.Crypto;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The verb end to end: real images decoded chunk by chunk, the right exit code
/// and - on failure - the right chunk named. Every fixture-backed test degrades
/// to a pass when the images have not been generated.
/// </summary>
public sealed class VerifyCommandTests
{
    private static readonly VerifyCommand Command = new();

    [Theory]
    [InlineData("exfat-zlib.dmg")]
    [InlineData("hfsplus.dmg")]
    [InlineData("apfs.dmg")]
    [InlineData("exfat-raw.dmg")]
    [InlineData("adc.dmg")]
    [InlineData("multipart.dmg")]
    [InlineData("zerofill.dmg")]
    public void AnIntactImageOfEveryKindVerifiesClean(string fixture)
    {
        if (Fixtures.Path(fixture) is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains(output.StdoutLines, line => line.Contains("OK", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIntactImageReportsOkOnStdout()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains(output.StdoutLines, line => line.Contains("OK", StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressGoesToStderrNotStdout()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Command.Execute(new CliContext([path], output.Output));

        Assert.NotEmpty(output.Stderr);
    }

    [Fact]
    public void ACorruptChunkIsReportedByIndexAndEntryTypeAndExitsCorrupt()
    {
        // zlib-payload-garbage.dmg (tools/make-hostile-corpus.py) is a structurally
        // valid exfat-zlib.dmg whose entire data fork was overwritten with 0xFF, so
        // it opens cleanly - the koly trailer, plist and chunk table all still
        // parse - and the first chunk's "zlib" stream is not zlib at all. That is
        // exactly the failure verify exists to catch: info would never notice,
        // because info never decodes the data fork.
        if (Fixtures.HostilePath("zlib-payload-garbage.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.CorruptImage, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains("Chunk 0", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("Zlib", output.Stderr, StringComparison.Ordinal);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void AnEncryptedImageVerifiesWithTheRightPassphrase()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        VerifyCommand command = new(
            new PassphraseReader(environment: name =>
                name == "DMG_TEST_PASSPHRASE" ? "dmg-test-passphrase" : null));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext(
                [path, "--password-env", "DMG_TEST_PASSPHRASE"],
                output.Output)));
    }

    [Fact]
    public void AnEncryptedImageWithNoPassphraseIsDecryptionFailed()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            Command.Execute(new CliContext([path], output.Output)));
    }

    [Fact]
    public void AWrongPassphraseIsDecryptionFailed()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        VerifyCommand command = new(new PassphraseReader(environment: _ => "not the passphrase"));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            command.Execute(new CliContext([path, "--password-env", "V"], output.Output)));
    }

    [Fact]
    public void AnUnsupportedCodecIsAChunkFailureLikeAnyOther()
    {
        // Opening a bzip2 image succeeds - the container parses fine - so the
        // decode pass reaches the first bzip2 chunk and finds no decoder for it.
        // The story's contract for verify is binary (every chunk decoded, or it
        // did not), so this is reported exactly like a corrupt chunk would be:
        // first failing index and EntryType, exit 9. That is deliberately
        // different from `extract`, which surfaces UnsupportedFormat so a caller
        // can tell the two apart.
        if (Fixtures.Path("bzip2.dmg") is not string path)
        {
            return;
        }

        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.CorruptImage, Command.Execute(new CliContext([path], output.Output)));
        Assert.Contains("Chunk", output.Stderr, StringComparison.Ordinal);
        Assert.Contains("Bzip2", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoImageIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.UsageError, Command.Execute(new CliContext([], output.Output)));
        Assert.Contains("needs an image", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["no-such-image.dmg"], output.Output)));
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
    public void ItsHelpNamesEveryOptionItAccepts()
    {
        Assert.Equal("verify", Command.Spec.Verb);
        Assert.Equal(["IMAGE"], Command.Spec.Positionals);
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-stdin");
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-env");
        Assert.NotEmpty(Command.Spec.Notes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Command.Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new VerifyCommand(null!));
    }
}
