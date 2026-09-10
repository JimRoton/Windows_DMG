using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli;

/// <summary>
/// Entry point for <c>dmg.exe</c>.
/// </summary>
/// <remarks>
/// Verb dispatch, argument parsing and the <c>--quiet</c>/<c>--verbose</c>/
/// <c>--json</c> switches arrive with the CLI stories in epic 9. What exists here
/// now is the shape everything else will hang off: build an <see cref="IOutput"/>,
/// do the work, report through it, and return the taxonomy's exit code.
/// </remarks>
internal static class Program
{
    internal static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        IOutput output = ConsoleOutput.ForConsole();

        DmgError error = DmgError.Usage("No verbs are wired up yet.");
        output.Error(error);

        return (int)error.Code;
    }
}
