namespace Dmg.Core.Crypto;

/// <summary>Where a passphrase is to come from.</summary>
public enum PassphraseSource
{
    /// <summary>Nothing on the command line asked for one; prompt if an image turns out to need it.</summary>
    Unspecified = 0,

    /// <summary>Read from standard input, to the end or to the first newline.</summary>
    StandardInput,

    /// <summary>Read from a named environment variable.</summary>
    Environment,
}

/// <summary>
/// The passphrase options, parsed off a command line.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>--password &lt;value&gt;</c>, by design.</b> A passphrase on the
/// command line is written to the shell's history file, is visible in the process
/// table to every user on the machine for as long as the process runs, and ends up
/// in any CI log that echoes its commands. None of those is fixable from inside
/// this program, so the option does not exist. Passing it is a usage error with a
/// message that says what to use instead, rather than silently doing nothing -
/// someone who typed it has already leaked the passphrase and needs to be told.
/// </para>
/// <para>
/// The three ways in are: <c>--password-stdin</c> for pipes and scripts,
/// <c>--password-env VAR</c> for CI systems that inject secrets as environment
/// variables, and, when neither is given, a prompt on the terminal that does not
/// echo.
/// </para>
/// </remarks>
/// <param name="Source">Where the passphrase comes from.</param>
/// <param name="EnvironmentVariable">The variable to read, when <see cref="Source"/> is <see cref="PassphraseSource.Environment"/>.</param>
/// <param name="RemainingArguments">The arguments with the passphrase options removed, for the rest of the command line to parse.</param>
public sealed record PassphraseOptions(
    PassphraseSource Source,
    string? EnvironmentVariable,
    IReadOnlyList<string> RemainingArguments)
{
    /// <summary>The option that reads standard input.</summary>
    public const string StandardInputOption = "--password-stdin";

    /// <summary>The option that names an environment variable.</summary>
    public const string EnvironmentOption = "--password-env";

    /// <summary>
    /// The options that do not exist and never will, and are refused by name so the
    /// refusal can explain itself.
    /// </summary>
    public static IReadOnlyList<string> RejectedOptions { get; } =
        ["--password", "--passphrase", "-p", "--pass"];

    /// <summary>
    /// Pulls the passphrase options out of <paramref name="arguments"/>.
    /// </summary>
    /// <param name="arguments">The command line, or the part of it left to parse.</param>
    public static Result<PassphraseOptions> Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        PassphraseSource source = PassphraseSource.Unspecified;
        string? variable = null;
        List<string> remaining = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];

            if (IsRejected(argument, out string? rejected))
            {
                return Rejected(rejected);
            }

            if (argument.Equals(StandardInputOption, StringComparison.Ordinal))
            {
                if (source != PassphraseSource.Unspecified)
                {
                    return Conflict();
                }

                source = PassphraseSource.StandardInput;
                continue;
            }

            if (argument.Equals(EnvironmentOption, StringComparison.Ordinal))
            {
                if (source != PassphraseSource.Unspecified)
                {
                    return Conflict();
                }

                if (index + 1 >= arguments.Count)
                {
                    return Result<PassphraseOptions>.Failure(DmgError.Usage(
                        $"{EnvironmentOption} needs the name of an environment variable.",
                        $"Example: {EnvironmentOption} DMG_PASSPHRASE"));
                }

                variable = arguments[++index];

                if (string.IsNullOrWhiteSpace(variable))
                {
                    return Result<PassphraseOptions>.Failure(DmgError.Usage(
                        $"{EnvironmentOption} was given an empty variable name.",
                        $"Example: {EnvironmentOption} DMG_PASSPHRASE"));
                }

                source = PassphraseSource.Environment;
                continue;
            }

            if (argument.StartsWith($"{EnvironmentOption}=", StringComparison.Ordinal))
            {
                if (source != PassphraseSource.Unspecified)
                {
                    return Conflict();
                }

                variable = argument[(EnvironmentOption.Length + 1)..];

                if (string.IsNullOrWhiteSpace(variable))
                {
                    return Result<PassphraseOptions>.Failure(DmgError.Usage(
                        $"{EnvironmentOption} was given an empty variable name.",
                        $"Example: {EnvironmentOption}=DMG_PASSPHRASE"));
                }

                source = PassphraseSource.Environment;
                continue;
            }

            remaining.Add(argument);
        }

        return Result<PassphraseOptions>.Success(new PassphraseOptions(source, variable, remaining));
    }

    private static bool IsRejected(string argument, out string? name)
    {
        foreach (string option in RejectedOptions)
        {
            if (argument.Equals(option, StringComparison.Ordinal)
                || argument.StartsWith($"{option}=", StringComparison.Ordinal))
            {
                name = option;
                return true;
            }
        }

        name = null;
        return false;
    }

    private static Result<PassphraseOptions> Rejected(string? option) =>
        Result<PassphraseOptions>.Failure(DmgError.Usage(
            $"There is no {option} option, on purpose: a passphrase on the command line is "
            + "written to your shell history and is visible in the process table to everyone "
            + $"on this machine. Use {StandardInputOption}, or {EnvironmentOption} VAR, or "
            + "leave it out and type it at the prompt.",
            "The passphrase you just typed should be considered exposed."));

    private static Result<PassphraseOptions> Conflict() =>
        Result<PassphraseOptions>.Failure(DmgError.Usage(
            $"Give the passphrase one way only: {StandardInputOption} or {EnvironmentOption} VAR.",
            "Both were given."));
}
