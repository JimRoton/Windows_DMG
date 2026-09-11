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
        // Listing order, not alphabetical: what the tool is for comes first and
        // the two that describe the tool itself come last. `unmount` and `list`
        // sit beside `mount`, above `help`, as the rest of the mount family.
        // Registration is deliberately a one-line edit here rather than a switch
        // elsewhere.
        yield return new InfoCommand();
        yield return new ExtractCommand();
        yield return new VerifyCommand();
        yield return new MountCommand();
        yield return new UnmountCommand();
        yield return new ListCommand();
        yield return new HelpCommand();
        yield return new VersionCommand();
    }
}
