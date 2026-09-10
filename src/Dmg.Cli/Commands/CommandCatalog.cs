namespace Dmg.Cli.Commands;

/// <summary>
/// The verbs a real <c>dmg.exe</c> ships with, in the order the help listing prints
/// them.
/// </summary>
/// <remarks>
/// The one place a new verb has to be added. Kept apart from
/// <see cref="CommandRegistry"/> so a test can build a registry of fakes without
/// dragging every shipping verb - and every dependency behind it - into the test.
/// </remarks>
public static class CommandCatalog
{
    /// <summary>Builds the shipping registry.</summary>
    public static CommandRegistry CreateRegistry() => new(CreateCommands());

    /// <summary>The shipping verbs, in listing order.</summary>
    private static IEnumerable<ICliCommand> CreateCommands()
    {
        // Verbs arrive with their own stories: `info` in S9.3, `help` and
        // `version` in S9.9, the mount family in epic 10. Registration is
        // deliberately a one-line edit here rather than a switch elsewhere.
        yield break;
    }
}
