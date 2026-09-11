using Dmg.Cli.Commands;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// S9.10: every <see cref="DmgExitCode"/> the dispatcher can hand back, reported
/// correctly in both human and <c>--json</c> output modes.
/// </summary>
/// <remarks>
/// A verb reports failure through <see cref="IOutput.Error(DmgError)"/>, which
/// always writes to stderr as plain text - the JSON contract is only ever about
/// what lands on stdout. So for the nine failure codes, both modes are held to the
/// same two things: the exit code matches, and stdout stays empty, because a
/// failed verb never gets as far as producing a result to report. What
/// distinguishes the modes is exercised separately, by <see cref="Success"/> -
/// the one code with something to say when a verb succeeds.
/// </remarks>
public sealed class ExitCodeContractTests
{
    [Fact]
    public void Success()
    {
        FakeCommand command = new("info", context =>
        {
            if (context.Output.IsJson)
            {
                context.Output.WriteJson("""{"ok":true}""");
            }
            else
            {
                context.Output.WriteLine("OK: done.");
            }

            return DmgExitCode.Success;
        });

        AssertRuns(command, DmgExitCode.Success, isJson: false, expectedStdout: "OK: done.");
        AssertRuns(command, DmgExitCode.Success, isJson: true, expectedStdout: """{"ok":true}""");
    }

    [Fact]
    public void InternalError()
    {
        // Real code never returns this one directly - it is what the dispatcher
        // turns an escaped exception into (S1.2) - so the fake reproduces that
        // path rather than returning the code itself.
        FakeCommand command = new("info", _ => throw new InvalidOperationException("boom"));

        AssertFails(command, DmgExitCode.InternalError, "internal error");
    }

    [Fact]
    public void UsageError() =>
        AssertFails(ErrorCommand(DmgError.Usage("bad usage")), DmgExitCode.UsageError, "bad usage");

    [Fact]
    public void UnsupportedFormat() =>
        AssertFails(
            ErrorCommand(DmgError.Unsupported("bad format")),
            DmgExitCode.UnsupportedFormat,
            "bad format");

    [Fact]
    public void DecryptionFailed() =>
        AssertFails(
            ErrorCommand(new DmgError(DmgExitCode.DecryptionFailed, "wrong passphrase")),
            DmgExitCode.DecryptionFailed,
            "wrong passphrase");

    [Fact]
    public void FilesystemNotMountable() =>
        AssertFails(
            ErrorCommand(new DmgError(DmgExitCode.FilesystemNotMountable, "no driver for this filesystem")),
            DmgExitCode.FilesystemNotMountable,
            "no driver for this filesystem");

    [Fact]
    public void MountFailed() =>
        AssertFails(
            ErrorCommand(new DmgError(DmgExitCode.MountFailed, "attach failed")),
            DmgExitCode.MountFailed,
            "attach failed");

    [Fact]
    public void ElevationRequired() =>
        AssertFails(
            ErrorCommand(new DmgError(DmgExitCode.ElevationRequired, "needs an elevated shell")),
            DmgExitCode.ElevationRequired,
            "needs an elevated shell");

    [Fact]
    public void InsufficientSpace() =>
        AssertFails(
            ErrorCommand(new DmgError(DmgExitCode.InsufficientSpace, "not enough scratch space")),
            DmgExitCode.InsufficientSpace,
            "not enough scratch space");

    [Fact]
    public void CorruptImage() =>
        AssertFails(ErrorCommand(DmgError.Corrupt("bad koly trailer")), DmgExitCode.CorruptImage, "bad koly trailer");

    private static FakeCommand ErrorCommand(DmgError error) =>
        new("info", context =>
        {
            context.Output.Error(error);

            return error.Code;
        });

    /// <summary>Runs the fake in both modes and checks each against the same exit code and message.</summary>
    private static void AssertFails(FakeCommand command, DmgExitCode expected, string messageFragment)
    {
        AssertRuns(command, expected, isJson: false, expectedStdout: null, expectedStderrFragment: messageFragment);
        AssertRuns(command, expected, isJson: true, expectedStdout: null, expectedStderrFragment: messageFragment);
    }

    private static void AssertRuns(
        FakeCommand command,
        DmgExitCode expected,
        bool isJson,
        string? expectedStdout,
        string? expectedStderrFragment = null)
    {
        CommandDispatcher dispatcher = new(new CommandRegistry([command]));
        RecordingOutput recorder = new(isJson: isJson);

        DmgExitCode code = dispatcher.Execute(["info"], recorder.Output);

        Assert.Equal(expected, code);

        if (expectedStdout is null)
        {
            Assert.Empty(recorder.Stdout);
        }
        else
        {
            Assert.Contains(expectedStdout, recorder.Stdout, StringComparison.Ordinal);
        }

        if (expectedStderrFragment is not null)
        {
            Assert.Contains(expectedStderrFragment, recorder.Stderr, StringComparison.Ordinal);
        }
    }
}
