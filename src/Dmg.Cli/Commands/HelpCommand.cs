using Dmg.Cli.Help;
using Dmg.Cli.Parsing;
using Dmg.Core;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg help</c> and <c>dmg help &lt;command&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same two screens the dispatcher prints for <c>--help</c>, reachable as a
/// verb because <c>dmg help mount</c> is what people type and because a verb can be
/// listed in the help while a switch cannot list itself.
/// </para>
/// <para>
/// A name it does not recognise is a usage error, not an empty screen - and it gets
/// the same "did you mean" treatment as an unknown verb, since asking for help on
/// something is exactly when a typo is most likely and least expected.
/// </para>
/// </remarks>
public sealed class HelpCommand : ICliCommand
{
    /// <inheritdoc />
    public string Verb => "help";

    /// <inheritdoc />
    public string Summary => "Show this help, or the help for one command.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "help",
        [],
        // Displayed with its brackets: the argument is optional, and the usage
        // line is the only place that can say so.
        positionals: ["[COMMAND]"],
        notes:
        [
            "'dmg <command> --help' prints the same thing.",
        ]);

    /// <inheritdoc />
    public DmgExitCode Execute(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, context.Arguments);

        if (!parsed.TryGetValue(out ParsedArguments? arguments))
        {
            context.Output.Error(parsed.Error);

            return parsed.Error.Code;
        }

        if (arguments.Positionals.Count == 0)
        {
            HelpText.WriteTo(context.Output, HelpText.ForTool(context.Registry));

            return DmgExitCode.Success;
        }

        string wanted = arguments.Positionals[0];

        if (!context.Registry.TryGet(wanted, out ICliCommand? command))
        {
            string? suggestion = NearestMatch.Find(wanted, context.Registry.Verbs);

            context.Output.Error(DmgError.Usage(
                suggestion is null
                    ? $"There is no '{wanted}' command, so there is no help for it. "
                      + HelpText.Pointer(null)
                    : $"There is no '{wanted}' command. Did you mean '{suggestion}'?",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        HelpText.WriteTo(context.Output, HelpText.ForCommand(command));

        return DmgExitCode.Success;
    }
}
