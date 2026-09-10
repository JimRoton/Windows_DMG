using Dmg.Cli.Commands;
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

            CommandDispatcher dispatcher = new(CommandCatalog.CreateRegistry());

            return (int)dispatcher.Execute(args, output);
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
