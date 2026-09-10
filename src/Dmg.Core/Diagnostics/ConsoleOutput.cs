namespace Dmg.Core.Diagnostics;

/// <summary>
/// The <see cref="IOutput"/> the CLI actually uses: two <see cref="TextWriter"/>s,
/// a verbosity, and the JSON invariant enforced rather than trusted.
/// </summary>
/// <remarks>
/// The writers are injected instead of reaching for <see cref="Console"/> directly
/// so the stdout/stderr split and the JSON invariant can be asserted in a unit test
/// rather than eyeballed in a terminal. <see cref="ForConsole"/> supplies the real
/// ones.
/// </remarks>
public sealed class ConsoleOutput : IOutput
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private bool _jsonWritten;

    /// <summary>Creates a sink over the given writers.</summary>
    /// <param name="stdout">Receives human-readable results, or the JSON document.</param>
    /// <param name="stderr">Receives progress, warnings, trace and errors.</param>
    /// <param name="verbosity">How much to say.</param>
    /// <param name="isJson">
    /// When true, stdout is reserved for a single JSON document and human-readable
    /// output is dropped.
    /// </param>
    public ConsoleOutput(TextWriter stdout, TextWriter stderr, Verbosity verbosity = Verbosity.Normal, bool isJson = false)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        _stdout = stdout;
        _stderr = stderr;
        Verbosity = verbosity;
        IsJson = isJson;
    }

    /// <summary>A sink over the process's real console streams.</summary>
    public static ConsoleOutput ForConsole(Verbosity verbosity = Verbosity.Normal, bool isJson = false) =>
        new(Console.Out, Console.Error, verbosity, isJson);

    /// <inheritdoc />
    public Verbosity Verbosity { get; }

    /// <inheritdoc />
    public bool IsJson { get; }

    /// <inheritdoc />
    public void WriteLine(string message)
    {
        if (WantsHumanOutput)
        {
            _stdout.WriteLine(message);
        }
    }

    /// <inheritdoc />
    public void Write(string message)
    {
        if (WantsHumanOutput)
        {
            _stdout.Write(message);
        }
    }

    /// <inheritdoc />
    public void Progress(string message)
    {
        if (Verbosity >= Verbosity.Normal)
        {
            _stderr.WriteLine(message);
        }
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
        if (Verbosity >= Verbosity.Normal)
        {
            _stderr.WriteLine($"warning: {message}");
        }
    }

    /// <inheritdoc />
    public void Trace(string message)
    {
        if (Verbosity >= Verbosity.Verbose)
        {
            _stderr.WriteLine($"trace: {message}");
        }
    }

    /// <inheritdoc />
    public void Error(string message) => _stderr.WriteLine($"dmg: {message}");

    /// <inheritdoc />
    public void Error(DmgError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        _stderr.WriteLine($"dmg: {error.Message}");

        if (Verbosity >= Verbosity.Verbose && error.Detail is not null)
        {
            _stderr.WriteLine($"  detail: {error.Detail}");
        }
    }

    /// <inheritdoc />
    public void WriteJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (_jsonWritten)
        {
            throw new InvalidOperationException(
                "A JSON document has already been written to stdout. Exactly one document is emitted per run; "
                + "a second would make stdout unparseable.");
        }

        _jsonWritten = true;
        _stdout.WriteLine(json);
    }

    /// <summary>
    /// Human-readable output is dropped entirely in JSON mode - not redirected to
    /// stderr, because the JSON document already carries the same information and
    /// duplicating it would just make the diagnostic stream harder to read.
    /// </summary>
    private bool WantsHumanOutput => !IsJson && Verbosity >= Verbosity.Normal;
}
