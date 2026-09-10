using Dmg.Cli.Commands;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli;

/// <summary>
/// Entry point for <c>dmg.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately almost empty. Everything worth testing - finding the verb, running
/// it, turning a failure into an exit code, turning a bug into
/// <see cref="DmgExitCode.InternalError"/> - lives in
/// <see cref="CommandDispatcher"/>, which a unit test can call directly. What is
/// left here is the part a test cannot reach anyway: the real console streams and
/// the <c>int</c> the operating system wants back.
/// </para>
/// <para>
/// The global switches are lifted off the line first because they decide how the
/// output sink is built, and the sink has to exist before anything - including the
/// failure to parse them - can be reported. A provisional sink covers that one
/// gap.
/// </para>
/// <para>
/// <c>--help</c> is the exception that shapes the sink without being one of those
/// switches: it overrides <c>--quiet</c> and <c>--json</c>, both of which would
/// otherwise leave a user who typed <c>--help</c> looking at a blank screen. The
/// switch itself stays on the line, because which help to print is the
/// dispatcher's business.
/// </para>
/// <para>
/// The <c>catch</c> below is the second of two nets. <see cref="CommandDispatcher.Execute"/>
/// already turns any exception out of a verb into
/// <see cref="DmgExitCode.InternalError"/>; this one covers the sliver of code
/// outside it - building the sink, building the registry - so that no path through
/// this program can put a stack trace on a user's terminal or, worse, exit 0 after
/// failing.
/// </para>
/// </remarks>
internal static class Program
{
    internal static int Main(string[] args)
    {
        IOutput output = ConsoleOutput.ForConsole();

        try
        {
            ArgumentNullException.ThrowIfNull(args);

            Result<GlobalOptions> extracted = GlobalOptions.Extract(args);

            if (!extracted.TryGetValue(out GlobalOptions? globals))
            {
                output.Error(extracted.Error);

                return (int)extracted.Error.Code;
            }

            // A help request forces a plain human sink: --quiet would suppress the
            // screen and --json would swallow it, and either way the user typed
            // --help and would get nothing back.
            output = globals.WantsHelp
                ? ConsoleOutput.ForConsole()
                : ConsoleOutput.ForConsole(globals.Verbosity, globals.IsJson);

            CommandDispatcher dispatcher = new(CommandCatalog.CreateRegistry());

            return (int)dispatcher.Execute(globals.Remaining, output);
        }
        catch (Exception exception)
        {
            output.Error(DmgError.Internal(
                "dmg hit an internal error and stopped. This is a bug in dmg, not in your image.",
                $"{exception.GetType().Name}: {exception.Message}"));

            return (int)DmgExitCode.InternalError;
        }
    }
}
