using Dmg.Cli.Parsing;
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
    /// What this verb accepts: its options and the names of its positional
    /// arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One table, two readers. <see cref="Parsing.ArgumentParser"/> parses against
    /// it and <c>dmg help &lt;verb&gt;</c> prints from it, so an option cannot exist
    /// without being documented and cannot be documented without existing. Every
    /// CLI that keeps its help in a separate string eventually ships one that lies.
    /// </para>
    /// <para>
    /// It is on the interface rather than a detail of each verb because
    /// <see cref="CommandDispatcher"/> answers <c>--help</c> for every verb from
    /// here, once, instead of each verb remembering to check for it.
    /// </para>
    /// </remarks>
    CommandLineSpec Spec { get; }

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
