using Dmg.Core;

namespace Dmg.Cli.Commands;

/// <summary>
/// One verb of <c>dmg.exe</c>: <c>info</c>, <c>mount</c>, <c>unmount</c>, and the
/// rest.
/// </summary>
/// <remarks>
/// <para>
/// A verb is a class with one method and no ambient state. It is handed a
/// <see cref="CliContext"/> and returns an exit code; it never writes to
/// <see cref="Console"/> and never calls <see cref="Environment.Exit(int)"/>. That
/// is the whole point of the interface: every verb can be exercised end to end from
/// a unit test, with a recording <see cref="Core.Diagnostics.IOutput"/> in place of
/// the console, without launching a process and parsing its output back.
/// </para>
/// <para>
/// <b>Returning, not throwing.</b> An expected failure is a
/// <see cref="Result{T}"/> reported through the context's output and turned into an
/// exit code here. Exceptions are for bugs, and the dispatcher's top-level handler
/// catches those and returns <see cref="DmgExitCode.InternalError"/>.
/// </para>
/// </remarks>
public interface ICliCommand
{
    /// <summary>
    /// The word the user types. Lower-case, no punctuation, matched ordinally.
    /// </summary>
    string Verb { get; }

    /// <summary>
    /// One line describing the verb, for the top-level help listing.
    /// </summary>
    string Summary { get; }

    /// <summary>
    /// Runs the verb.
    /// </summary>
    /// <param name="context">The arguments meant for this verb, and the output sink.</param>
    /// <returns>
    /// The process exit code. <see cref="DmgExitCode.Success"/> when the verb did
    /// what it was asked; anything else after the failure has already been reported
    /// through <see cref="CliContext.Output"/>.
    /// </returns>
    DmgExitCode Execute(CliContext context);
}
