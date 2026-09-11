using System.Text.Json;
using Dmg.Cli.Commands;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// <c>dmg version</c>. The values themselves depend on the build, so these check
/// the shape: the headline, the labelled lines, valid JSON with the fields a script
/// would branch on, and stdout/stderr kept apart.
/// </summary>
public sealed class VersionCommandTests
{
    private static readonly VersionCommand Command = new();

    [Fact]
    public void ThePlainScreenLeadsWithTheHeadlineAndGoesToStdout()
    {
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([], output.Output)));

        Assert.Empty(output.Stderr);
        Assert.Equal(BuildInfo.Current.Headline, output.StdoutLines[0]);
    }

    [Fact]
    public void EveryDetailAppearsWithItsLabel()
    {
        RecordingOutput output = new();

        Command.Execute(new CliContext([], output.Output));

        foreach ((string label, string value) in BuildInfo.Current.Details)
        {
            Assert.Contains(
                output.StdoutLines,
                line => line.Contains(label, StringComparison.Ordinal)
                    && line.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheArchitectureIsAlwaysReported()
    {
        RecordingOutput output = new();

        Command.Execute(new CliContext([], output.Output));

        // The reason this verb exists: there is an x64 build and an arm64 build and
        // they behave differently, so the screen has to name which one this is.
        Assert.Contains(
            output.StdoutLines,
            line => line.Contains("built for", StringComparison.Ordinal)
                && line.Contains(BuildInfo.Name(BuildInfo.Current.ProcessArchitecture), StringComparison.Ordinal));
    }

    [Fact]
    public void JsonModeWritesOneParseableDocumentWithTheSameFacts()
    {
        RecordingOutput output = new(isJson: true);

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([], output.Output)));
        Assert.Empty(output.Stderr);

        using JsonDocument document = JsonDocument.Parse(output.Stdout);
        JsonElement root = document.RootElement;

        Assert.Equal("dmg", root.GetProperty("tool").GetString());
        Assert.Equal(BuildInfo.Current.Version, root.GetProperty("version").GetString());
        Assert.Equal(
            BuildInfo.Name(BuildInfo.Current.ProcessArchitecture),
            root.GetProperty("architecture").GetString());
        Assert.Equal(BuildInfo.Current.IsEmulated, root.GetProperty("emulated").GetBoolean());
    }

    [Fact]
    public void JsonIsNotEscapedForHtmlItWillNeverBeIn()
    {
        RecordingOutput output = new(isJson: true);

        Command.Execute(new CliContext([], output.Output));

        // The default encoder writes a "+" in a version as +, which is valid
        // and unreadable. Nothing on this screen should come back escaped.
        Assert.DoesNotContain("\\u00", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void QuietStillMeansQuiet()
    {
        RecordingOutput output = new(Verbosity.Quiet);

        Assert.Equal(DmgExitCode.Success, Command.Execute(new CliContext([], output.Output)));
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void AnArgumentIsAUsageErrorOnStderr()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["1.2.3"], output.Output)));

        Assert.Empty(output.Stdout);
        Assert.Contains("takes no arguments", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOptionIsAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            Command.Execute(new CliContext(["--nope"], output.Output)));

        Assert.Contains("is not an option of 'version'", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Command.Execute(null!));
    }
}
