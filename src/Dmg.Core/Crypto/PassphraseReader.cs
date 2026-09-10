using System.Security.Cryptography;
using System.Text;

namespace Dmg.Core.Crypto;

/// <summary>
/// Gets the passphrase from wherever <see cref="PassphraseOptions"/> says, without
/// it ever becoming a string the process cannot scrub.
/// </summary>
/// <remarks>
/// <para>
/// Every dependency is injected, so the whole thing is testable without a terminal:
/// standard input, the environment lookup and the interactive prompt are all
/// functions with real defaults. That matters because the interactive path is the
/// one nobody can test by hand twice the same way, and it is also the one where a
/// mistake means the passphrase is echoed to a shared screen.
/// </para>
/// <para>
/// <b>Standard input: one trailing newline is stripped.</b> <c>echo secret | dmg</c>
/// sends four extra bytes on Windows and one everywhere else, and none of them were
/// typed by the user. What is stripped is a single <c>\n</c>, and the <c>\r</c>
/// before it if there is one - not all trailing whitespace, because a passphrase is
/// allowed to end in a space. Note that this is the mirror image of the
/// hdiutil-created case, where the terminator <em>is</em> part of the passphrase;
/// the unwrap retries with it appended for exactly that reason.
/// </para>
/// </remarks>
public sealed class PassphraseReader
{
    private readonly Func<Stream> _standardInput;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, Result<Passphrase>> _prompt;

    /// <summary>
    /// Builds a reader. Every argument defaults to the real thing; tests pass their
    /// own.
    /// </summary>
    /// <param name="standardInput">Opens the process's standard input.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="prompt">Asks the user, without echoing. The argument is the prompt text.</param>
    public PassphraseReader(
        Func<Stream>? standardInput = null,
        Func<string, string?>? environment = null,
        Func<string, Result<Passphrase>>? prompt = null)
    {
        _standardInput = standardInput ?? Console.OpenStandardInput;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _prompt = prompt ?? ReadFromConsole;
    }

    /// <summary>The default prompt text.</summary>
    public const string DefaultPrompt = "Passphrase: ";

    /// <summary>
    /// Reads the passphrase the options ask for.
    /// </summary>
    /// <param name="options">The parsed passphrase options.</param>
    /// <param name="prompt">The prompt text, when it comes to that.</param>
    /// <returns>
    /// A passphrase the caller owns and must dispose - ideally with <c>using</c>, so
    /// the zeroing survives a throw.
    /// </returns>
    public Result<Passphrase> Read(PassphraseOptions options, string prompt = DefaultPrompt)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Source switch
        {
            PassphraseSource.StandardInput => ReadFromStandardInput(),
            PassphraseSource.Environment => ReadFromEnvironment(options.EnvironmentVariable),
            _ => _prompt(prompt),
        };
    }

    private Result<Passphrase> ReadFromStandardInput()
    {
        byte[] raw;

        try
        {
            using Stream input = _standardInput();
            using MemoryStream buffer = new();
            input.CopyTo(buffer);
            raw = buffer.ToArray();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                $"Could not read a passphrase from standard input.",
                exception.Message));
        }

        int length = TrimOneNewline(raw);

        if (length == raw.Length)
        {
            return Result<Passphrase>.Success(Passphrase.Adopt(raw));
        }

        try
        {
            return Result<Passphrase>.Success(Passphrase.CopyFrom(raw.AsSpan(0, length)));
        }
        finally
        {
            // The untrimmed copy still holds the passphrase; it does not get to
            // linger just because a newline was cut off it.
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private Result<Passphrase> ReadFromEnvironment(string? variable)
    {
        if (string.IsNullOrWhiteSpace(variable))
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                $"{PassphraseOptions.EnvironmentOption} needs the name of an environment variable.",
                "No name was given."));
        }

        string? value = _environment(variable);

        if (value is null)
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                $"The environment variable '{variable}' is not set, so there is no passphrase "
                + "to read.",
                $"{PassphraseOptions.EnvironmentOption} {variable}"));
        }

        if (value.Length == 0)
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                $"The environment variable '{variable}' is empty.",
                "An empty passphrase would only ever fail to unlock the image."));
        }

        // The string itself cannot be scrubbed - that is the cost of taking a
        // passphrase through the environment, and the reason it is the second-best
        // of the three ways in.
        return Result<Passphrase>.Success(Passphrase.FromString(value));
    }

    /// <summary>
    /// Reads from the terminal with echo off, one key at a time.
    /// </summary>
    /// <remarks>
    /// <see cref="Console.ReadLine"/> would echo. Reading keys with
    /// <c>intercept: true</c> is the only way in the BCL to keep the passphrase off
    /// a screen someone may be sharing. When input is redirected there is no console
    /// to read keys from, and that case is turned into a usage error naming the
    /// option that does work rather than an exception.
    /// </remarks>
    private static Result<Passphrase> ReadFromConsole(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                "This image needs a passphrase, but there is no terminal to ask on because "
                + $"input is redirected. Use {PassphraseOptions.StandardInputOption} or "
                + $"{PassphraseOptions.EnvironmentOption} VAR.",
                "Console.IsInputRedirected was true."));
        }

        List<char> typed = [];

        try
        {
            Console.Write(prompt);

            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (typed.Count > 0)
                    {
                        typed[^1] = '\0';
                        typed.RemoveAt(typed.Count - 1);
                    }

                    continue;
                }

                if (key.KeyChar != '\0')
                {
                    typed.Add(key.KeyChar);
                }
            }

            char[] characters = [.. typed];

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(characters);
                return Result<Passphrase>.Success(Passphrase.Adopt(bytes));
            }
            finally
            {
                Array.Clear(characters);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            return Result<Passphrase>.Failure(DmgError.Usage(
                "Could not read a passphrase from the terminal. Use "
                + $"{PassphraseOptions.StandardInputOption} or "
                + $"{PassphraseOptions.EnvironmentOption} VAR instead.",
                exception.Message));
        }
        finally
        {
            for (int index = 0; index < typed.Count; index++)
            {
                typed[index] = '\0';
            }

            typed.Clear();
        }
    }

    /// <summary>
    /// The length of <paramref name="raw"/> with one trailing newline removed.
    /// </summary>
    private static int TrimOneNewline(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 0 && raw[^1] == (byte)'\n')
        {
            return raw.Length >= 2 && raw[^2] == (byte)'\r' ? raw.Length - 2 : raw.Length - 1;
        }

        return raw.Length;
    }
}
