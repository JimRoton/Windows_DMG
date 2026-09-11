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
    private static readonly string? Directory = FindDirectory("generated");
    private static readonly string? HostileDirectory = FindDirectory("hostile");

    /// <summary>True when the generated images are present.</summary>
    internal static bool Available => Directory is not null;

    /// <summary>The path to one fixture, or null when there are none.</summary>
    /// <param name="name">The file name, e.g. <c>exfat-zlib.dmg</c>.</param>
    internal static string? Path(string name) => PathIn(Directory, name);

    /// <summary>
    /// The path to one deliberately malformed image from
    /// <c>tools/make-hostile-corpus.py</c>, or null when the corpus has not been
    /// generated. Unlike <see cref="Path"/>, the file's bytes are not the same
    /// every regeneration only in the sense that the corpus is derived from
    /// <c>fixtures/generated</c> - a test using this still must not pin a hash.
    /// </summary>
    /// <param name="name">The file name, e.g. <c>zlib-payload-garbage.dmg</c>.</param>
    internal static string? HostilePath(string name) => PathIn(HostileDirectory, name);

    private static string? PathIn(string? directory, string name)
    {
        if (directory is null)
        {
            return null;
        }

        string path = System.IO.Path.Combine(directory, name);

        return File.Exists(path) ? path : null;
    }

    private static string? FindDirectory(string folderName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Windows_DMG.sln")))
            {
                string candidate = System.IO.Path.Combine(directory.FullName, "fixtures", folderName);

                return System.IO.Directory.Exists(candidate) ? candidate : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
