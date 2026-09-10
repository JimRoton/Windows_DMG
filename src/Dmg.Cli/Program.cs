using Dmg.Core;

namespace Dmg.Cli;

/// <summary>
/// Entry point for <c>dmg.exe</c>.
/// </summary>
/// <remarks>
/// Verb dispatch arrives with the CLI stories in epic 9. For now this exists so
/// the executable project has a real entry point and the solution builds.
/// </remarks>
internal static class Program
{
    internal static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DmgError error = DmgError.Usage("No verbs are wired up yet.");
        Console.Error.WriteLine($"dmg: {error}");

        return (int)error.Code;
    }
}
