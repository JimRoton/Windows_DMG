using Dmg.Cli.Parsing;

namespace Dmg.Cli.Help;

/// <summary>
/// Spotting <c>--help</c> anywhere on the line, in the one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// Two very different pieces of code need this answer and they must not disagree.
/// <see cref="GlobalOptions"/> asks so it can build a sink that will actually print
/// - a help screen suppressed by <c>--quiet</c> or swallowed by <c>--json</c> is a
/// user typing <c>--help</c> and getting nothing back, which is the worst outcome
/// of the three. <see cref="Commands.CommandDispatcher"/> asks so it can answer
/// with the right screen and stop. One predicate, both callers.
/// </para>
/// <para>
/// <c>--help</c> is matched wherever it appears because that is where people put
/// it: <c>dmg info --help</c> and <c>dmg --help info</c> are the same question. The
/// scan stops at <c>--</c>, so a file genuinely called <c>--help</c> is still
/// openable as <c>dmg info -- --help</c>.
/// </para>
/// <para>
/// Only the exact tokens count. <c>-h</c> inside a cluster such as <c>-qh</c> does
/// not, because reading it would mean knowing which letters in the cluster take
/// values, which means knowing the verb, which is the thing this runs before.
/// </para>
/// </remarks>
public static class HelpRequest
{
    /// <summary>The long spelling.</summary>
    public const string LongForm = "--help";

    /// <summary>The short spelling.</summary>
    public const string ShortForm = "-h";

    /// <summary>True when the user asked for help somewhere on this line.</summary>
    /// <param name="arguments">The command line, or any tail of it.</param>
    public static bool IsRequestedIn(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                continue;
            }

            if (argument.Equals(ArgumentParser.Terminator, StringComparison.Ordinal))
            {
                return false;
            }

            if (argument.Equals(LongForm, StringComparison.Ordinal)
                || argument.Equals(ShortForm, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
