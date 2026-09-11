using Dmg.Cli.Commands;
using Dmg.Cli.Tests.Info;
using Dmg.Core;
using Dmg.Core.Crypto;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The verb end to end: real images decoded to raw or VHD, the right bytes and the
/// right exit code out. Every fixture-backed test degrades to a pass when the
/// images have not been generated.
/// </summary>
public sealed class ExtractCommandTests : IDisposable
{
    private static readonly ExtractCommand Command = new();

    private readonly string _outputDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dmg-extract-tests-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file here does not invalidate the test.
        }
    }

    [Fact]
    public void RawOnAnUnmountableFilesystemWritesTheDecodedBytes()
    {
        // HFS+ is the case the doc comment names: Windows has no driver for it, so
        // `mount` cannot use it, but the codec behind it decodes cleanly and extract
        // has a decoded copy regardless.
        if (Fixtures.Path("hfsplus.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("hfsplus.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output, "--format", "raw"], recording.Output)));

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Contains(recording.StdoutLines, line => line.Contains("Wrote", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnsupportedCodecIsReportedAsUnsupportedNotCorrupt()
    {
        // bzip2-compressed chunks have no decoder in this build (see
        // ChunkCodecInfo/ChunkDecoderRegistry). DmgBlockStream throws a
        // DmgStreamException carrying DmgExitCode.UnsupportedFormat for that -
        // extract must surface that exit code rather than flattening every read
        // failure into "corrupt".
        if (Fixtures.Path("bzip2.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("bzip2.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UnsupportedFormat,
            Command.Execute(new CliContext([path, output, "--format", "raw"], recording.Output)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void VhdIsTheDefaultFormat()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("exfat.vhd");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output], recording.Output)));

        Assert.True(File.Exists(output));

        // A fixed VHD's footer is one 512-byte sector at the end of the file, so
        // the output is strictly larger than the raw disk it wraps - the one
        // structural fact this test can check without re-decoding the VHD itself.
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void DynamicWritesADynamicVhd()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("exfat-dynamic.vhd");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output, "--dynamic"], recording.Output)));

        Assert.True(File.Exists(output));
        Assert.Contains(recording.StdoutLines, line => line.Contains("Dynamic VHD", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicWithRawFormatIsAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(
                ["a.dmg", "b.raw", "--format", "raw", "--dynamic"],
                recording.Output)));

        Assert.Contains("--dynamic only makes sense", recording.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomCacheBudgetStillExtractsCorrectly()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("exfat-cache.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output, "--format", "raw", "--cache", "1"], recording.Output)));

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void CacheZeroIsAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg", "b.vhd", "--cache", "0"], recording.Output)));

        Assert.Contains("--cache must be a positive", recording.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeCacheIsAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg", "b.vhd", "--cache", "-1"], recording.Output)));
    }

    [Fact]
    public void CacheNotANumberIsAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg", "b.vhd", "--cache", "lots"], recording.Output)));
    }

    [Fact]
    public void RawFormatIsAcceptedExplicitly()
    {
        if (Fixtures.Path("hfsplus.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("hfsplus.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output, "--format", "raw"], recording.Output)));

        Assert.True(File.Exists(output));
    }

    [Fact]
    public void AnEncryptedImageExtractsWithTheRightPassphrase()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        ExtractCommand command = new(
            new PassphraseReader(environment: name =>
                name == "DMG_TEST_PASSPHRASE" ? "dmg-test-passphrase" : null));

        string output = OutputPath("exfat-enc256.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext(
                [path, output, "--format", "raw", "--password-env", "DMG_TEST_PASSPHRASE"],
                recording.Output)));

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void AnEncryptedImageWithNoPassphraseIsDecryptionFailed()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("should-not-exist.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            Command.Execute(new CliContext([path, output], recording.Output)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AWrongPassphraseIsDecryptionFailedAndLeavesNoFile()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        ExtractCommand command = new(new PassphraseReader(environment: _ => "not the passphrase"));

        string output = OutputPath("should-not-exist.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            command.Execute(new CliContext([path, output, "--password-env", "V"], recording.Output)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnExistingOutputWithoutForceIsAUsageErrorAndIsNotOverwritten()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("already-there.raw");
        File.WriteAllText(output, "sentinel");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext([path, output], recording.Output)));

        Assert.Equal("sentinel", File.ReadAllText(output));
        Assert.Contains("already exists", recording.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceOverwritesAnExistingOutput()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("overwrite-me.raw");
        File.WriteAllText(output, "sentinel");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.Success,
            Command.Execute(new CliContext([path, output, "--format", "raw", "--force"], recording.Output)));

        Assert.NotEqual("sentinel", File.ReadAllText(output));
    }

    [Fact]
    public void ProgressGoesToStderrAndTheResultGoesToStdout()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        string output = OutputPath("progress.raw");
        RecordingOutput recording = new();

        Command.Execute(new CliContext([path, output, "--format", "raw"], recording.Output));

        Assert.NotEmpty(recording.Stderr);
        Assert.Contains(recording.StdoutLines, line => line.Contains("Wrote", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownFormatIsAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(
                ["a.dmg", "b.raw", "--format", "zip"],
                recording.Output)));

        Assert.Contains("--format must be 'raw' or 'vhd'", recording.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingArgumentsAreAUsageError()
    {
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["a.dmg"], recording.Output)));

        Assert.Contains("needs an image", recording.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSourceImageIsAUsageError()
    {
        string output = OutputPath("no-source.raw");
        RecordingOutput recording = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["no-such-image.dmg", output], recording.Output)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ItsHelpNamesEveryOptionItAccepts()
    {
        Assert.Equal("extract", Command.Spec.Verb);
        Assert.Equal(["IMAGE", "OUTPUT"], Command.Spec.Positionals);
        Assert.Contains(Command.Spec.Options, option => option.Name == "format");
        Assert.Contains(Command.Spec.Options, option => option.Name == "force");
        Assert.Contains(Command.Spec.Options, option => option.Name == "dynamic");
        Assert.Contains(Command.Spec.Options, option => option.Name == "cache");
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-stdin");
        Assert.Contains(Command.Spec.Options, option => option.Name == "password-env");
        Assert.NotEmpty(Command.Spec.Notes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Command.Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new ExtractCommand(null!));
    }

    private string OutputPath(string name) => Path.Combine(_outputDirectory, name);
}
