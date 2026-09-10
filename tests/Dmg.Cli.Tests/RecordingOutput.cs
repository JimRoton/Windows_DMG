using System.Text;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Tests;

/// <summary>
/// A <see cref="ConsoleOutput"/> over two <see cref="StringWriter"/>s, so a test can
/// read back exactly what the tool would have printed and to which stream.
/// </summary>
/// <remarks>
/// This wraps the real <see cref="ConsoleOutput"/> rather than reimplementing
/// <see cref="IOutput"/>: the suppression rules and the JSON invariant are part of
/// what the CLI tests are checking, so a hand-rolled stand-in that got them subtly
/// different would test nothing.
/// </remarks>
internal sealed class RecordingOutput
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    internal RecordingOutput(Verbosity verbosity = Verbosity.Normal, bool isJson = false) =>
        Output = new ConsoleOutput(_stdout, _stderr, verbosity, isJson);

    /// <summary>The sink to hand to a verb.</summary>
    internal IOutput Output { get; }

    /// <summary>Everything written to stdout.</summary>
    internal string Stdout => _stdout.ToString();

    /// <summary>Everything written to stderr.</summary>
    internal string Stderr => _stderr.ToString();

    /// <summary>stdout split into lines, with the trailing blank dropped.</summary>
    internal IReadOnlyList<string> StdoutLines => SplitLines(Stdout);

    /// <summary>stderr split into lines, with the trailing blank dropped.</summary>
    internal IReadOnlyList<string> StderrLines => SplitLines(Stderr);

    /// <inheritdoc />
    public override string ToString() =>
        new StringBuilder()
            .AppendLine("--- stdout ---")
            .Append(Stdout)
            .AppendLine("--- stderr ---")
            .Append(Stderr)
            .ToString();

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Length == 0
            ? []
            : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}
