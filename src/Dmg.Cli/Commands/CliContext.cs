using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Commands;

/// <summary>
/// Everything a verb is allowed to touch: the arguments that were meant for it,
/// the sink it says things through, and the set of verbs this build has.
/// </summary>
/// <remarks>
/// <para>
/// A verb reaches for nothing else. No <see cref="Console"/>, no
/// <see cref="Environment.Exit(int)"/>, no <see cref="Environment.GetCommandLineArgs"/> -
/// which is what makes <c>dmg info image.dmg</c> a method call in a unit test
/// rather than a process launch with its stdout scraped back. The one exception is
/// the filesystem, because reading the image the user named is the job.
/// </para>
/// <para>
/// <see cref="Arguments"/> is the command line with the verb already removed. The
/// dispatcher owns finding the verb; a verb never has to skip past its own name.
/// </para>
/// <para>
/// <b>Why the registry is here.</b> <c>dmg help</c> is a verb whose subject is the
/// other verbs, and it needs to list them and print their specs. Passing the
/// registry through the context is the alternative to <c>HelpCommand</c> holding a
/// lazily-patched reference to the registry that contains it. It is a read-only
/// catalogue: a verb may describe another verb, and must not execute one. Routing
/// belongs to <see cref="CommandDispatcher"/>, which is the only thing that can
/// keep the exit-code contract honest.
/// </para>
/// </remarks>
/// <param name="Arguments">The arguments after the verb, in order, never null.</param>
/// <param name="Output">Where results, progress and errors go.</param>
/// <param name="Registry">
/// The verbs this build knows. Optional: a test exercising one verb has no reason
/// to build a catalogue, so it defaults to <see cref="CommandRegistry.Empty"/>.
/// </param>
public sealed record CliContext(
    IReadOnlyList<string> Arguments,
    IOutput Output,
    CommandRegistry? Registry = null)
{
    /// <summary>The arguments after the verb. Never null; empty when there were none.</summary>
    public IReadOnlyList<string> Arguments { get; } =
        Arguments ?? throw new ArgumentNullException(nameof(Arguments));

    /// <summary>Where results, progress and errors go. Never null.</summary>
    public IOutput Output { get; } =
        Output ?? throw new ArgumentNullException(nameof(Output));

    /// <summary>The verbs this build knows. Never null; empty when none were given.</summary>
    public CommandRegistry Registry { get; } = Registry ?? CommandRegistry.Empty;
}
