using Dmg.Core;

namespace Dmg.Cli.Parsing;

/// <summary>
/// The hand-rolled command-line parser: <c>--flag</c>, <c>--opt value</c>,
/// <c>--opt=value</c>, short <c>-v</c>, and <c>--</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hand-rolled.</b> Shipping code in this repository takes no third-party
/// packages, and <c>System.CommandLine</c> is not in the BCL. The grammar below is
/// small enough to specify in a paragraph and is fully covered by tests, which is
/// the trade being made.
/// </para>
/// <para>
/// <b>The grammar.</b>
/// </para>
/// <list type="bullet">
/// <item><c>--name</c> sets a flag, or takes the next argument as its value when the
/// spec says it takes one.</item>
/// <item><c>--name=value</c> always supplies the value inline, and is an error on a
/// flag - <c>--json=true</c> is a misunderstanding worth naming rather than
/// quietly accepting.</item>
/// <item><c>-v</c> is the short form of one option. Flags cluster: <c>-qv</c> is
/// <c>-q -v</c>. An option that takes a value may be given as <c>-o value</c> or
/// <c>-o=value</c>, and inside a cluster only as its last letter.</item>
/// <item><c>--</c> ends the options. Everything after it is positional, dashes and
/// all, so a file really called <c>--weird</c> can still be opened.</item>
/// <item>A bare <c>-</c> is positional, by long convention.</item>
/// </list>
/// <para>
/// <b>Every failure is a usage error.</b> Unknown option, missing value, a repeat of
/// something that may not repeat, a positional too many: all
/// <see cref="DmgExitCode.UsageError"/>, all as a returned
/// <see cref="Result{T}"/> rather than a throw, and all naming the option involved.
/// An unknown option is offered the nearest thing the verb does accept.
/// </para>
/// </remarks>
public static class ArgumentParser
{
    /// <summary>The end-of-options marker.</summary>
    public const string Terminator = "--";

    /// <summary>Parses <paramref name="arguments"/> against a verb's spec.</summary>
    /// <param name="spec">What the verb accepts.</param>
    /// <param name="arguments">The arguments after the verb.</param>
    public static Result<ParsedArguments> Parse(CommandLineSpec spec, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(arguments);

        Dictionary<string, List<string?>> options = new(StringComparer.Ordinal);
        List<string> positionals = [];
        bool optionsEnded = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;

            if (optionsEnded)
            {
                positionals.Add(argument);
                continue;
            }

            if (argument.Equals(Terminator, StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            Result<int> consumed = argument.StartsWith("--", StringComparison.Ordinal)
                ? ReadLong(spec, arguments, index, argument, options)
                : IsShortForm(argument)
                    ? ReadShort(spec, arguments, index, argument, options)
                    : Positional(positionals, argument);

            if (!consumed.TryGetValue(out int step))
            {
                return consumed.CastFailure<ParsedArguments>();
            }

            index += step;
        }

        if (spec.Positionals.Count > 0 && positionals.Count > spec.Positionals.Count)
        {
            return Result<ParsedArguments>.Failure(DmgError.Usage(
                $"'{spec.Verb}' takes {Describe(spec.Positionals.Count)}, but {positionals.Count} were given: "
                + string.Join(", ", positionals.Select(value => $"'{value}'")) + ".",
                $"Usage: {spec.UsageLine}"));
        }

        return Result<ParsedArguments>.Success(new ParsedArguments(spec, options, positionals));
    }

    /// <summary>An argument that is a short option, rather than a positional or a bare dash.</summary>
    private static bool IsShortForm(string argument) =>
        argument.Length >= 2 && argument[0] == '-' && argument[1] != '-';

    private static Result<int> Positional(List<string> positionals, string argument)
    {
        positionals.Add(argument);

        return Result<int>.Success(0);
    }

    /// <summary>Reads one <c>--name</c> or <c>--name=value</c>.</summary>
    /// <returns>How many extra arguments were consumed: 1 when the value came next, else 0.</returns>
    private static Result<int> ReadLong(
        CommandLineSpec spec,
        IReadOnlyList<string> arguments,
        int index,
        string argument,
        Dictionary<string, List<string?>> options)
    {
        string body = argument[2..];
        string name = body;
        string? inline = null;

        int equals = body.IndexOf('=', StringComparison.Ordinal);

        if (equals >= 0)
        {
            name = body[..equals];
            inline = body[(equals + 1)..];
        }

        if (name.Length == 0)
        {
            return Result<int>.Failure(DmgError.Usage(
                $"'{argument}' is not an option name.",
                $"Usage: {spec.UsageLine}"));
        }

        if (!spec.TryGetLong(name, out OptionSpec? option))
        {
            return Result<int>.Failure(Unknown(spec, "--" + name));
        }

        if (!option.TakesValue)
        {
            if (inline is not null)
            {
                return Result<int>.Failure(DmgError.Usage(
                    $"{option.LongForm} is a switch and takes no value, so '{argument}' cannot be right. "
                    + $"Write {option.LongForm} on its own.",
                    $"Usage: {spec.UsageLine}"));
            }

            return Store(spec, options, option, value: null, consumed: 0);
        }

        if (inline is not null)
        {
            return Store(spec, options, option, inline, consumed: 0);
        }

        if (index + 1 >= arguments.Count)
        {
            return Result<int>.Failure(MissingValue(spec, option, option.LongForm));
        }

        return Store(spec, options, option, arguments[index + 1], consumed: 1);
    }

    /// <summary>Reads one <c>-v</c>, <c>-qv</c>, <c>-o value</c> or <c>-o=value</c>.</summary>
    private static Result<int> ReadShort(
        CommandLineSpec spec,
        IReadOnlyList<string> arguments,
        int index,
        string argument,
        Dictionary<string, List<string?>> options)
    {
        string cluster = argument[1..];
        string? inline = null;

        int equals = cluster.IndexOf('=', StringComparison.Ordinal);

        if (equals >= 0)
        {
            inline = cluster[(equals + 1)..];
            cluster = cluster[..equals];

            if (cluster.Length != 1)
            {
                return Result<int>.Failure(DmgError.Usage(
                    $"'{argument}' is not a short option. Only a single letter can take a value inline, "
                    + "as in -o=value.",
                    $"Usage: {spec.UsageLine}"));
            }
        }

        for (int position = 0; position < cluster.Length; position++)
        {
            char letter = cluster[position];

            if (!spec.TryGetShort(letter, out OptionSpec? option))
            {
                return Result<int>.Failure(Unknown(spec, "-" + letter));
            }

            if (!option.TakesValue)
            {
                if (inline is not null)
                {
                    return Result<int>.Failure(DmgError.Usage(
                        $"-{letter} ({option.LongForm}) is a switch and takes no value, so '{argument}' "
                        + "cannot be right.",
                        $"Usage: {spec.UsageLine}"));
                }

                Result<int> flag = Store(spec, options, option, value: null, consumed: 0);

                if (!flag.Ok)
                {
                    return flag;
                }

                continue;
            }

            // An option that wants a value has to be the last letter, otherwise the
            // letters after it would silently become the value - or worse, be read
            // as more flags.
            if (position != cluster.Length - 1)
            {
                return Result<int>.Failure(DmgError.Usage(
                    $"-{letter} ({option.LongForm}) takes a value, so it has to come last in '{argument}'.",
                    $"Usage: {spec.UsageLine}"));
            }

            if (inline is not null)
            {
                return Store(spec, options, option, inline, consumed: 0);
            }

            if (index + 1 >= arguments.Count)
            {
                return Result<int>.Failure(MissingValue(spec, option, "-" + letter));
            }

            return Store(spec, options, option, arguments[index + 1], consumed: 1);
        }

        return Result<int>.Success(0);
    }

    private static Result<int> Store(
        CommandLineSpec spec,
        Dictionary<string, List<string?>> options,
        OptionSpec option,
        string? value,
        int consumed)
    {
        if (!options.TryGetValue(option.Name, out List<string?>? values))
        {
            values = [];
            options[option.Name] = values;
        }
        else if (!option.AllowMultiple)
        {
            return Result<int>.Failure(DmgError.Usage(
                $"{option.LongForm} was given more than once.",
                $"Usage: {spec.UsageLine}"));
        }

        values.Add(value);

        return Result<int>.Success(consumed);
    }

    private static DmgError MissingValue(CommandLineSpec spec, OptionSpec option, string asTyped) =>
        DmgError.Usage(
            $"{asTyped} needs a value: {option.LongForm} {option.ValueName}.",
            $"Usage: {spec.UsageLine}");

    /// <summary>
    /// "--jsn is not an option of 'info'. Did you mean --json?" - the suggestion is
    /// the whole reason this path is not a one-liner.
    /// </summary>
    private static DmgError Unknown(CommandLineSpec spec, string asTyped)
    {
        string bare = asTyped.TrimStart('-');

        string? suggestion = NearestMatch.Find(
            bare,
            spec.Options.Select(option => option.Name));

        string hint = suggestion is null
            ? string.Empty
            : $" Did you mean --{suggestion}?";

        return DmgError.Usage(
            $"{asTyped} is not an option of '{spec.Verb}'.{hint}",
            $"Usage: {spec.UsageLine}");
    }

    private static string Describe(int count) =>
        count == 1 ? "one argument" : $"{count} arguments";
}
