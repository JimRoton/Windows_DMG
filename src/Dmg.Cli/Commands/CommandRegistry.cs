using System.Diagnostics.CodeAnalysis;

namespace Dmg.Cli.Commands;

/// <summary>
/// The verbs this build knows, looked up by name.
/// </summary>
/// <remarks>
/// <para>
/// A registry rather than a <c>switch</c> in <c>Main</c> so that the set of verbs is
/// a value a test can construct. A test that wants to prove the dispatcher's
/// unknown-verb message can build a registry of two fake verbs, and a test of one
/// real verb never has to drag the others in behind it.
/// </para>
/// <para>
/// Verbs are matched ordinally and case-sensitively. <c>dmg INFO</c> is a usage
/// error rather than a silent success, because a CLI that quietly accepts a second
/// spelling has two spellings forever.
/// </para>
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICliCommand> _byVerb;
    private readonly ICliCommand[] _commands;

    /// <summary>
    /// Builds a registry over <paramref name="commands"/>, in the order given -
    /// which is the order the help listing prints them in.
    /// </summary>
    /// <param name="commands">The verbs to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/>, or one of them, is null.</exception>
    /// <exception cref="ArgumentException">
    /// Two commands claim the same verb, or a verb is blank. Both are wiring bugs in
    /// this codebase, not something a user can cause, so both throw.
    /// </exception>
    public CommandRegistry(IEnumerable<ICliCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        _commands = [.. commands];
        _byVerb = new Dictionary<string, ICliCommand>(_commands.Length, StringComparer.Ordinal);

        foreach (ICliCommand command in _commands)
        {
            ArgumentNullException.ThrowIfNull(command, nameof(commands));

            if (string.IsNullOrWhiteSpace(command.Verb))
            {
                throw new ArgumentException(
                    $"{command.GetType().Name} registered a blank verb.",
                    nameof(commands));
            }

            if (!_byVerb.TryAdd(command.Verb, command))
            {
                throw new ArgumentException(
                    $"Two commands both claim the verb '{command.Verb}': "
                    + $"{_byVerb[command.Verb].GetType().Name} and {command.GetType().Name}. "
                    + "The second would be unreachable.",
                    nameof(commands));
            }
        }
    }

    /// <summary>The registered verbs, in registration order.</summary>
    public IReadOnlyList<ICliCommand> Commands => _commands;

    /// <summary>The verb names, in registration order.</summary>
    public IReadOnlyList<string> Verbs => [.. _commands.Select(command => command.Verb)];

    /// <summary>Finds the verb the user typed.</summary>
    /// <param name="verb">The word typed on the command line.</param>
    /// <param name="command">The matching command, when there is one.</param>
    public bool TryGet(string verb, [NotNullWhen(true)] out ICliCommand? command)
    {
        ArgumentNullException.ThrowIfNull(verb);

        return _byVerb.TryGetValue(verb, out command);
    }
}
