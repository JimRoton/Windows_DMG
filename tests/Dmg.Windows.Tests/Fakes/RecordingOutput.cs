using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// An <see cref="IOutput"/> that keeps everything instead of printing it, so a test
/// can assert that the user was told.
/// </summary>
/// <remarks>
/// "Report and continue" is half behaviour and half message: a registry that is
/// quietly ignored looks exactly like a registry that was empty, and the user's
/// mounts have vanished either way. Recording the warnings is what lets the
/// difference be tested.
/// </remarks>
public sealed class RecordingOutput : IOutput
{
    private readonly List<string> _warnings = [];
    private readonly List<string> _errors = [];
    private readonly List<string> _lines = [];
    private readonly List<string> _traces = [];

    /// <summary>Everything passed to <see cref="Warning"/>.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Everything passed to either <c>Error</c> overload.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Everything written to stdout.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Everything passed to <see cref="Trace"/>. Kept, unlike the real
    /// verbosity-gated sinks, so a test can assert something was said at verbose
    /// level without the recorder itself needing to model <see cref="Verbosity"/>.
    /// </summary>
    public IReadOnlyList<string> Traces => _traces;

    /// <inheritdoc />
    public Verbosity Verbosity => Verbosity.Normal;

    /// <inheritdoc />
    public bool IsJson => false;

    /// <summary>True when any warning mentions <paramref name="fragment"/>.</summary>
    public bool WarnedAbout(string fragment) =>
        _warnings.Exists(warning => warning.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when any trace mentions <paramref name="fragment"/>.</summary>
    public bool TracedAbout(string fragment) =>
        _traces.Exists(trace => trace.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public void WriteLine(string message) => _lines.Add(message);

    /// <inheritdoc />
    public void Write(string message) => _lines.Add(message);

    /// <inheritdoc />
    public void Progress(string message)
    {
    }

    /// <inheritdoc />
    public void Warning(string message) => _warnings.Add(message);

    /// <inheritdoc />
    public void Trace(string message) => _traces.Add(message);

    /// <inheritdoc />
    public void Error(string message) => _errors.Add(message);

    /// <inheritdoc />
    public void Error(DmgError error) => _errors.Add(error.ToString());

    /// <inheritdoc />
    public void WriteJson(string json) => _lines.Add(json);
}
