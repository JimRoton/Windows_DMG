using System.Diagnostics.CodeAnalysis;

namespace Dmg.Cli.Parsing;

/// <summary>
/// Everything one verb accepts: its options, and what its positional arguments are
/// called.
/// </summary>
/// <remarks>
/// One table, two jobs. <see cref="ArgumentParser"/> reads it to parse, and the help
/// (S9.9) reads it to print. Anything that can only be described in one of the two
/// places is a rule the user will discover by getting it wrong.
/// </remarks>
public sealed class CommandLineSpec
{
    private readonly OptionSpec[] _options;
    private readonly Dictionary<string, OptionSpec> _byLongName;
    private readonly Dictionary<char, OptionSpec> _byShortName;

    /// <summary>Builds a spec.</summary>
    /// <param name="verb">The verb this describes, for messages.</param>
    /// <param name="options">The options it accepts, in help-listing order.</param>
    /// <param name="positionals">
    /// The names of the positional arguments, in order - <c>["IMAGE"]</c> for
    /// <c>dmg info IMAGE</c>. Used for the usage line and for the "too many
    /// arguments" message.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Two options share a long or short name. A wiring bug, so it throws.
    /// </exception>
    public CommandLineSpec(string verb, IEnumerable<OptionSpec> options, IEnumerable<string>? positionals = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(options);

        Verb = verb;
        _options = [.. options];
        Positionals = [.. positionals ?? []];
        _byLongName = new Dictionary<string, OptionSpec>(_options.Length, StringComparer.Ordinal);
        _byShortName = [];

        foreach (OptionSpec option in _options)
        {
            ArgumentNullException.ThrowIfNull(option, nameof(options));

            if (!_byLongName.TryAdd(option.Name, option))
            {
                throw new ArgumentException(
                    $"'{verb}' declares --{option.Name} twice.",
                    nameof(options));
            }

            if (option.Short is char letter && !_byShortName.TryAdd(letter, option))
            {
                throw new ArgumentException(
                    $"'{verb}' gives -{letter} to both --{_byShortName[letter].Name} and --{option.Name}.",
                    nameof(options));
            }
        }
    }

    /// <summary>The verb this spec belongs to.</summary>
    public string Verb { get; }

    /// <summary>The options, in help-listing order.</summary>
    public IReadOnlyList<OptionSpec> Options => _options;

    /// <summary>The names of the positional arguments, in order.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>The usage line: <c>dmg info [OPTIONS] IMAGE</c>.</summary>
    public string UsageLine
    {
        get
        {
            string options = _options.Length == 0 ? string.Empty : " [OPTIONS]";
            string arguments = Positionals.Count == 0
                ? string.Empty
                : " " + string.Join(" ", Positionals);

            return $"dmg {Verb}{options}{arguments}";
        }
    }

    /// <summary>Finds an option by its long name, given without dashes.</summary>
    public bool TryGetLong(string name, [NotNullWhen(true)] out OptionSpec? option)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byLongName.TryGetValue(name, out option);
    }

    /// <summary>Finds an option by its single-letter alias.</summary>
    public bool TryGetShort(char letter, [NotNullWhen(true)] out OptionSpec? option) =>
        _byShortName.TryGetValue(letter, out option);
}
