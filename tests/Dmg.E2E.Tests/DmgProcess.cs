using System.Diagnostics;
using System.Text;

namespace Dmg.E2E.Tests;

/// <summary>
/// Runs the real, built <c>dmg.exe</c> as a child process and captures what it
/// did - exit code, stdout, stderr.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subprocess, and not <c>CommandDispatcher.Execute</c> in-process.</b>
/// <see cref="Dmg.Cli.Tests"/> already drives every verb in-process, against fakes,
/// for exactly the parts of the contract a fake can prove. This suite exists for
/// the part a fake cannot: whether Windows itself - <c>virtdisk.dll</c>,
/// <c>AttachVirtualDisk</c>, drive-letter assignment - actually does what
/// <see cref="Dmg.Windows.VirtualDisk.WindowsVirtualDiskService"/> assumes it does.
/// That only happens by running the real, compiled executable.
/// </para>
/// <para>
/// <b>Locating the executable.</b> <c>Dmg.E2E.Tests.csproj</c> carries a
/// <c>ProjectReference</c> on <c>src/Dmg.Cli/Dmg.Cli.csproj</c>, so MSBuild copies
/// <c>dmg.exe</c> and everything it needs next to this test assembly as part of
/// the normal build - no separate publish step, no path configuration. That is
/// deliberate: the exe under test is exactly the one this build just produced.
/// </para>
/// <para>
/// <b>A hard timeout, no interactive prompts.</b> The same discipline
/// <c>tools/make-fixtures.sh</c> applies to <c>hdiutil</c> applies here to
/// <c>dmg.exe</c>: every call gets a wall-clock ceiling and its stdin is never left
/// attached to the test runner's own, so a hang in the process under test cannot
/// hang the test run or block on a prompt nobody is present to answer.
/// </para>
/// </remarks>
internal static class DmgProcess
{
    /// <summary>How long any single <c>dmg.exe</c> invocation is allowed to run.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The full path to the <c>dmg.exe</c> this test assembly was built alongside,
    /// or null when it is not there (a cross-build on a non-Windows machine never
    /// produces one worth running).
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "dmg.exe");

            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>Runs <c>dmg.exe</c> with the given arguments and waits for it to exit.</summary>
    /// <param name="arguments">The command-line arguments, unquoted - each one its own array element.</param>
    public static DmgResult Run(params string[] arguments)
    {
        string? exePath = ExecutablePath;

        if (exePath is null)
        {
            throw new InvalidOperationException(
                "dmg.exe was not found next to the test assembly. The E2E suite depends on the "
                + "ProjectReference to src/Dmg.Cli building it there; check the build output.");
        }

        ProcessStartInfo startInfo = new(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };

        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();

        // Nothing dmg.exe does prompts on stdin; closing it immediately means a
        // build that regresses into asking for one hangs against a closed pipe and
        // fails fast, rather than blocking for the full timeout below.
        process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check above and the kill - fine.
            }

            throw new TimeoutException(
                $"'dmg {string.Join(' ', arguments)}' did not exit within {Timeout}. Killed it. "
                + $"stdout so far:\n{stdout}\nstderr so far:\n{stderr}");
        }

        // WaitForExit(int) does not guarantee the async output pumps have delivered
        // their last line; the parameterless overload does, and is cheap once the
        // process has already exited.
        process.WaitForExit();

        return new DmgResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>One completed run of <c>dmg.exe</c>.</summary>
/// <param name="ExitCode">The process exit code - a <see cref="Dmg.Core.DmgExitCode"/> value.</param>
/// <param name="StdOut">Everything written to stdout.</param>
/// <param name="StdErr">Everything written to stderr.</param>
internal sealed record DmgResult(int ExitCode, string StdOut, string StdErr);
