namespace Dmg.Cli.Tests.Info;

/// <summary>
/// Where the generated images are, and whether there are any.
/// </summary>
/// <remarks>
/// The fixtures are gitignored and made by <c>tools/make-fixtures.sh</c>, so a
/// clean clone has none. Every test that wants one asks here first and returns
/// quietly when the answer is no: a fixture-less checkout should report a green
/// suite, not a red one, because nothing is broken - the images simply have not
/// been generated.
/// </remarks>
internal static class Fixtures
{
    private static readonly string? Directory = FindDirectory();

    /// <summary>True when the generated images are present.</summary>
    internal static bool Available => Directory is not null;

    /// <summary>The path to one fixture, or null when there are none.</summary>
    /// <param name="name">The file name, e.g. <c>exfat-zlib.dmg</c>.</param>
    internal static string? Path(string name)
    {
        if (Directory is null)
        {
            return null;
        }

        string path = System.IO.Path.Combine(Directory, name);

        return File.Exists(path) ? path : null;
    }

    private static string? FindDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Windows_DMG.sln")))
            {
                string generated = System.IO.Path.Combine(directory.FullName, "fixtures", "generated");

                return System.IO.Directory.Exists(generated) ? generated : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
