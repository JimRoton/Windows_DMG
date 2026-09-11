using Dmg.Cli.Help;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Parsing;

/// <summary>
/// The three switches that belong to the tool rather than to any one verb:
/// <c>--quiet</c>, <c>--verbose</c> and <c>--json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are lifted out before the verb is even found.</b> They decide how
/// the <see cref="IOutput"/> is built, and the output has to exist before anything
/// can be reported - including the error for an unknown verb. So they are stripped
/// from the command line first, wherever they appear, and a verb never sees them.
/// </para>
/// <para>
/// <b>Asking for help beats asking for quiet.</b> <c>--quiet</c> drops everything
/// but errors and <c>--json</c> drops everything but the document, so under the
/// plain rules <c>dmg --quiet --help</c> prints nothing at all - a user typing
/// <c>--help</c> and getting an empty screen back. The combination is nonsense
/// either way; the useful way to resolve it is to answer the question, so a help
/// request forces a plain human sink and <see cref="Verbosity"/> and
/// <see cref="IsJson"/> report what would otherwise have applied.
/// </para>
/// <para>
/// <b>The one blind spot, and its escape hatch.</b> Scanning the whole line means a
/// value that happens to be spelled <c>--json</c> would be taken as the switch.
/// Scanning stops at <c>--</c>, so <c>dmg info -- --json</c> opens a file with that
/// improbable name. It is the same trade every Unix tool makes.
/// </para>
/// </remarks>
/// <param name="Verbosity">How much the tool should say.</param>
/// <param name="IsJson">True when stdout is reserved for one JSON document.</param>
/// <param name="Remaining">The command line with these switches removed.</param>
/// <param name="WantsHelp">
/// True when <c>--help</c> or <c>-h</c> is on the line. It is not one of these
/// switches and is left in <see cref="Remaining"/> for the dispatcher to route;
/// it is reported here only because it overrides the other two.
/// </param>
public sealed record GlobalOptions(
    Verbosity Verbosity,
    bool IsJson,
    IReadOnlyList<string> Remaining,
    bool WantsHelp = false)
{
    /// <summary>The switches, as their specs, for the help listing.</summary>
    public static IReadOnlyList<OptionSpec> Specs { get; } =
    [
        new OptionSpec("quiet", "Errors only. Nothing on success.", 'q'),
        new OptionSpec("verbose", "Everything, including the per-step trace.", 'v'),
        new OptionSpec("json", "Write one JSON document to stdout and nothing else."),
    ];

    /// <summary>
    /// Pulls the global switches off <paramref name="arguments"/>.
    /// </summary>
    /// <param name="arguments">The whole command line, verb included.</param>
    /// <returns>
    /// The chosen verbosity and mode with the switches removed, or a usage failure
    /// when <c>--quiet</c> and <c>--verbose</c> were both given.
    /// </returns>
    public static Result<GlobalOptions> Extract(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        bool quiet = false;
        bool verbose = false;
        bool json = false;
        List<string> remaining = [];
        bool optionsEnded = false;

        foreach (string raw in arguments)
        {
            string argument = raw ?? string.Empty;

            if (optionsEnded)
            {
                remaining.Add(argument);
                continue;
            }

            if (argument.Equals(ArgumentParser.Terminator, StringComparison.Ordinal))
            {
                optionsEnded = true;
                remaining.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--quiet" or "-q":
                    quiet = true;
                    continue;
                case "--verbose" or "-v":
                    verbose = true;
                    continue;
                case "--json":
                    json = true;
                    continue;
                default:
                    remaining.Add(argument);
                    continue;
            }
        }

        if (quiet && verbose)
        {
            return Result<GlobalOptions>.Failure(DmgError.Usage(
                "--quiet and --verbose contradict each other. Give one or neither.",
                "--quiet is errors only; --verbose adds the per-step trace."));
        }

        Verbosity verbosity = quiet
            ? Verbosity.Quiet
            : verbose ? Verbosity.Verbose : Verbosity.Normal;

        return Result<GlobalOptions>.Success(new GlobalOptions(
            verbosity,
            json,
            remaining,
            HelpRequest.IsRequestedIn(arguments)));
    }
}
