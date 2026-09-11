namespace Dmg.Cli.Parsing;

/// <summary>
/// One option a verb accepts: what it is called, whether it takes a value, and how
/// to describe it in help.
/// </summary>
/// <remarks>
/// <para>
/// The parser is table-driven rather than clever. It cannot guess whether
/// <c>--partition 2</c> is an option with a value or a flag followed by a
/// positional, and guessing wrong is how a CLI ends up with a rule nobody can
/// remember. So every verb declares its options, and the same table both drives the
/// parse and prints the help - which means the two can never drift apart.
/// </para>
/// <para>
/// <see cref="Name"/> is the long name without its dashes: <c>"json"</c>, not
/// <c>"--json"</c>.
/// </para>
/// </remarks>
/// <param name="Name">The long name, without the leading <c>--</c>.</param>
/// <param name="Description">One line for the help listing.</param>
/// <param name="Short">The single-letter alias, or null. Given without its dash.</param>
/// <param name="ValueName">
/// The placeholder shown in help - <c>VAR</c>, <c>N</c>, <c>PATH</c>. Null for a
/// flag, and it is this that makes an option one that takes a value.
/// </param>
/// <param name="AllowMultiple">
/// True when repeating the option is meaningful. When false - the default -
/// repeating it is a usage error rather than a silent last-one-wins, because
/// silently discarding half of what the user typed is how a script ends up doing
/// something other than what it says.
/// </param>
public sealed record OptionSpec(
    string Name,
    string Description,
    char? Short = null,
    string? ValueName = null,
    bool AllowMultiple = false)
{
    /// <summary>The long name, without dashes. Never blank.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name)
        ? Name
        : throw new ArgumentException("An option needs a name.", nameof(Name));

    /// <summary>True when the option is followed by a value.</summary>
    public bool TakesValue => ValueName is not null;

    /// <summary>The long form as the user types it: <c>--json</c>.</summary>
    public string LongForm => "--" + Name;

    /// <summary>The short form as the user types it, or null.</summary>
    public string? ShortForm => Short is char letter ? "-" + letter : null;

    /// <summary>
    /// How the option appears in a help listing: <c>-q, --quiet</c> or
    /// <c>--password-env VAR</c>.
    /// </summary>
    public string Syntax
    {
        get
        {
            string forms = ShortForm is null ? LongForm : $"{ShortForm}, {LongForm}";

            return TakesValue ? $"{forms} {ValueName}" : forms;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Syntax;
}
