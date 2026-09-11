using Dmg.Cli.Help;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Commands;

/// <summary>
/// Turns a command line into a verb call and an exit code.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>Main</c> would be if <c>Main</c> could be called from a test. It
/// takes the raw arguments and an <see cref="IOutput"/>, finds the verb, hands the
/// rest of the line to it, and returns the code the process should exit with.
/// Nothing here writes to <see cref="Console"/> or calls
/// <see cref="Environment.Exit(int)"/>, so a test drives the whole tool - argument
/// handling, verb, output, exit code - in-process.
/// </para>
/// <para>
/// <b>Help is answered here, once, for every verb.</b> <c>--help</c> is checked
/// before the verb's own parse runs, so <c>dmg info --help</c> prints the help
/// rather than complaining that <c>IMAGE</c> is missing - which is what a verb
/// checking for it after parsing would do, and is the single most common way a
/// hand-rolled CLI gets help wrong. It also means a verb cannot forget to support
/// it, and cannot support it differently from its neighbour.
/// </para>
/// <para>
/// <b>Where help goes.</b> Asked for, it is a result: stdout, exit 0. Reached by
/// getting something wrong, it is an error: stderr, exit 2 - and what goes there is
/// the line saying what was wrong plus a pointer to the help, never the help
/// itself, because a screen of options printed on top of an error message buries
/// the message.
/// </para>
/// <para>
/// <b>The top-level exception handler lives here, not in <c>Main</c>.</b> Every
/// expected failure in this codebase is a <see cref="Result{T}"/>, so an exception
/// reaching this point is by definition a bug in the tool. It is reported as
/// <see cref="DmgExitCode.InternalError"/> - never as a stack trace on a user's
/// terminal, and never as a silent exit 0. Putting the handler here rather than in
/// <c>Main</c> means the guarantee itself is covered by a test.
/// </para>
/// </remarks>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;

    /// <summary>Builds a dispatcher over a set of verbs.</summary>
    /// <param name="registry">The verbs this build knows.</param>
    public CommandDispatcher(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
    }

    /// <summary>The verbs this dispatcher can route to.</summary>
    public CommandRegistry Registry => _registry;

    /// <summary>
    /// Routes <paramref name="arguments"/> to a verb and returns the process exit
    /// code. Never throws.
    /// </summary>
    /// <param name="arguments">The process arguments, verb included.</param>
    /// <param name="output">Where the verb, and any failure here, reports.</param>
    public DmgExitCode Execute(IReadOnlyList<string> arguments, IOutput output)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            return Route(arguments, output);
        }
        catch (Exception exception)
        {
            // A bug, by construction: everything a user or an image can do is a
            // Result. Report it as one line plus the detail at --verbose, so the
            // user gets something they can paste into an issue rather than a wall
            // of stack frames.
            output.Error(DmgError.Internal(
                "dmg hit an internal error and stopped. This is a bug in dmg, not in your image.",
                $"{exception.GetType().Name}: {exception.Message}"));

            if (output.Verbosity >= Verbosity.Verbose)
            {
                output.Trace(exception.ToString());
            }

            return DmgExitCode.InternalError;
        }
    }

    private DmgExitCode Route(IReadOnlyList<string> arguments, IOutput output)
    {
        if (arguments.Count == 0)
        {
            // Bare `dmg` is a mistake, not a request. The help would be a fine
            // thing to show, but it belongs on stdout and this exits 2, so what
            // stderr gets is the mistake and the way out of it.
            output.Error(DmgError.Usage(
                $"No command given.{KnownVerbs()} {HelpText.Pointer(null)}",
                "Usage: dmg [OPTIONS] <command> [ARGUMENTS]"));

            return DmgExitCode.UsageError;
        }

        string verb = arguments[0];

        // `dmg --help` and `dmg -h`, before anything is treated as a verb.
        if (verb.Equals(HelpRequest.LongForm, StringComparison.Ordinal)
            || verb.Equals(HelpRequest.ShortForm, StringComparison.Ordinal))
        {
            HelpText.WriteTo(output, HelpText.ForTool(_registry));

            return DmgExitCode.Success;
        }

        // `dmg --version` is the switch spelling of the verb. Both exist because
        // both are what people type, and neither should have its own code path.
        if (verb.Equals("--version", StringComparison.Ordinal) && _registry.TryGet("version", out ICliCommand? version))
        {
            return version.Execute(new CliContext([], output, _registry));
        }

        if (!_registry.TryGet(verb, out ICliCommand? command))
        {
            string? suggestion = NearestMatch.Find(verb, _registry.Verbs);

            output.Error(DmgError.Usage(
                suggestion is null
                    ? $"'{verb}' is not a dmg command.{KnownVerbs()} {HelpText.Pointer(null)}"
                    : $"'{verb}' is not a dmg command. Did you mean '{suggestion}'?",
                "Verbs are matched exactly, in lower case."));

            return DmgExitCode.UsageError;
        }

        string[] rest = new string[arguments.Count - 1];

        for (int index = 1; index < arguments.Count; index++)
        {
            rest[index - 1] = arguments[index];
        }

        // Checked before the verb runs, so `dmg info --help` answers the question
        // asked rather than complaining that IMAGE is missing.
        if (HelpRequest.IsRequestedIn(rest))
        {
            HelpText.WriteTo(output, HelpText.ForCommand(command));

            return DmgExitCode.Success;
        }

        return command.Execute(new CliContext(rest, output, _registry));
    }

    /// <summary>
    /// " Try one of: a, b, c." - or nothing at all when the registry is empty,
    /// because "Try one of: ." is worse than saying nothing.
    /// </summary>
    private string KnownVerbs() =>
        _registry.Verbs.Count == 0
            ? string.Empty
            : $" Try one of: {string.Join(", ", _registry.Verbs)}.";
}
