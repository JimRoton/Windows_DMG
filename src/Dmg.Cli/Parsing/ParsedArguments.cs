using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Dmg.Core;

namespace Dmg.Cli.Parsing;

/// <summary>
/// A command line after the parser has been over it: which options were given, with
/// what values, and what was left over.
/// </summary>
/// <remarks>
/// Options are looked up by their long name without dashes - <c>arguments.Has("json")</c>
/// - because that is the one name every option has. Nothing here reparses or
/// revalidates; by the time a verb holds one of these, every option on it was in the
/// verb's own spec.
/// </remarks>
public sealed class ParsedArguments
{
    private readonly Dictionary<string, List<string?>> _options;

    internal ParsedArguments(
        CommandLineSpec spec,
        Dictionary<string, List<string?>> options,
        IReadOnlyList<string> positionals)
    {
        Spec = spec;
        _options = options;
        Positionals = positionals;
    }

    /// <summary>The spec this line was parsed against.</summary>
    public CommandLineSpec Spec { get; }

    /// <summary>The arguments that were not options, in order.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>True when the option appeared at all.</summary>
    /// <param name="name">The long name, without dashes.</param>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _options.ContainsKey(name);
    }

    /// <summary>
    /// The value of an option that takes one - the last, if it was allowed to repeat.
    /// </summary>
    /// <param name="name">The long name, without dashes.</param>
    /// <param name="value">The value, when the option was given.</param>
    public bool TryGetValue(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_options.TryGetValue(name, out List<string?>? values) && values.Count > 0)
        {
            value = values[^1];
            return value is not null;
        }

        value = null;
        return false;
    }

    /// <summary>Every value given for a repeatable option, in the order typed.</summary>
    /// <param name="name">The long name, without dashes.</param>
    public IReadOnlyList<string> Values(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _options.TryGetValue(name, out List<string?>? values)
            ? [.. values.Where(value => value is not null).Select(value => value!)]
            : [];
    }

    /// <summary>
    /// An option's value read as a non-negative integer.
    /// </summary>
    /// <param name="name">The long name, without dashes.</param>
    /// <returns>
    /// The number, or null when the option was not given, or a usage failure when it
    /// was given something that is not one.
    /// </returns>
    public Result<int?> TryGetInt32(string name)
    {
        if (!TryGetValue(name, out string? text))
        {
            return Result<int?>.Success(null);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Result<int?>.Success(value)
            : Result<int?>.Failure(DmgError.Usage(
                $"--{name} needs a whole number, but got '{text}'.",
                $"Parsed against {nameof(NumberStyles)}.{nameof(NumberStyles.None)}, invariant culture."));
    }
}
