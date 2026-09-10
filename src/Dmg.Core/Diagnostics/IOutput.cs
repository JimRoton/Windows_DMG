namespace Dmg.Core.Diagnostics;

/// <summary>
/// Everything the tool says, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stream split.</b> Human-readable results go to <c>stdout</c>. Everything
/// else - progress, warnings, trace, errors - goes to <c>stderr</c>. That is what
/// makes <c>dmg info image.dmg | grep exFAT</c> work while the user still sees the
/// progress on their terminal.
/// </para>
/// <para>
/// <b>The JSON invariant.</b> When <see cref="IsJson"/> is true, <c>stdout</c>
/// carries exactly one thing: the single JSON document passed to
/// <see cref="WriteJson"/>. Human output is dropped rather than printed, and
/// diagnostics still go to <c>stderr</c>. A caller can therefore pipe stdout
/// straight into a parser without filtering, and does not need to redirect stderr
/// to keep it clean. Implementations must enforce this, not merely document it.
/// </para>
/// <para>
/// Nothing here formats a value: callers pass finished strings. That keeps
/// globalization out of the picture entirely (the build sets
/// <c>InvariantGlobalization</c>) and keeps this interface trim- and AOT-safe.
/// </para>
/// </remarks>
public interface IOutput
{
    /// <summary>How much this sink is willing to say.</summary>
    Verbosity Verbosity { get; }

    /// <summary>True when stdout is reserved for a single JSON document.</summary>
    bool IsJson { get; }

    /// <summary>
    /// A line of the human-readable result, to stdout. Suppressed at
    /// <see cref="Verbosity.Quiet"/> and in JSON mode.
    /// </summary>
    void WriteLine(string message);

    /// <summary>
    /// Part of a line of the human-readable result, to stdout, with no trailing
    /// newline. Same suppression rules as <see cref="WriteLine"/>.
    /// </summary>
    void Write(string message);

    /// <summary>
    /// A progress note, to stderr. Suppressed at <see cref="Verbosity.Quiet"/>.
    /// Safe in JSON mode - stderr is not the JSON channel.
    /// </summary>
    void Progress(string message);

    /// <summary>
    /// Something the user should know about but which is not fatal, to stderr.
    /// Suppressed at <see cref="Verbosity.Quiet"/>.
    /// </summary>
    void Warning(string message);

    /// <summary>
    /// Step-by-step diagnostics, to stderr. Emitted only at
    /// <see cref="Verbosity.Verbose"/>.
    /// </summary>
    void Trace(string message);

    /// <summary>A failure, to stderr. Never suppressed, at any verbosity.</summary>
    void Error(string message);

    /// <summary>
    /// A failure, to stderr. Never suppressed. <see cref="DmgError.Detail"/> is
    /// printed only at <see cref="Verbosity.Verbose"/>.
    /// </summary>
    void Error(DmgError error);

    /// <summary>
    /// Writes the one JSON document this invocation produces to stdout. Valid
    /// only when <see cref="IsJson"/> is true.
    /// </summary>
    /// <param name="json">A complete, already-serialized JSON document.</param>
    /// <exception cref="InvalidOperationException">
    /// <para>
    /// <see cref="IsJson"/> is false. Outside JSON mode stdout carries
    /// human-readable output, so a JSON document written into it would leave
    /// stdout parseable as neither. Implementations must throw rather than write.
    /// </para>
    /// <para>
    /// Or: a JSON document has already been written. There is exactly one per run
    /// - a second would make stdout unparseable.
    /// </para>
    /// </exception>
    void WriteJson(string json);
}
