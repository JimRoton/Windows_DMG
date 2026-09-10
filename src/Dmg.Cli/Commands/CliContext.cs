using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Commands;

/// <summary>
/// Everything a verb is allowed to touch: the arguments that were meant for it,
/// and the sink it says things through.
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
/// </remarks>
/// <param name="Arguments">The arguments after the verb, in order, never null.</param>
/// <param name="Output">Where results, progress and errors go.</param>
public sealed record CliContext(IReadOnlyList<string> Arguments, IOutput Output)
{
    /// <summary>The arguments after the verb. Never null; empty when there were none.</summary>
    public IReadOnlyList<string> Arguments { get; } =
        Arguments ?? throw new ArgumentNullException(nameof(Arguments));

    /// <summary>Where results, progress and errors go. Never null.</summary>
    public IOutput Output { get; } =
        Output ?? throw new ArgumentNullException(nameof(Output));
}
