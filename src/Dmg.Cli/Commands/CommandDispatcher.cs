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
            output.Error(DmgError.Usage($"No command given.{KnownVerbs()}"));

            return DmgExitCode.UsageError;
        }

        string verb = arguments[0];

        if (!_registry.TryGet(verb, out ICliCommand? command))
        {
            output.Error(DmgError.Usage(
                $"'{verb}' is not a dmg command.{KnownVerbs()}",
                "Verbs are matched exactly, in lower case."));

            return DmgExitCode.UsageError;
        }

        string[] rest = new string[arguments.Count - 1];

        for (int index = 1; index < arguments.Count; index++)
        {
            rest[index - 1] = arguments[index];
        }

        return command.Execute(new CliContext(rest, output));
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
