namespace Dmg.E2E.Tests;

/// <summary>
/// The two small, checked-in images this suite mounts, and the one known file
/// every populated fixture in this repo's corpus carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked in, not generated.</b> Every other fixture in this repo lives in the
/// gitignored <c>fixtures/generated/</c> and is produced fresh by
/// <c>tools/make-fixtures.sh</c> - a Mac-only step, because it calls Apple's own
/// <c>hdiutil</c>. Windows CI runners have no <c>hdiutil</c> and cannot generate
/// that corpus, so the two images this suite needs are committed instead, here
/// under <c>Fixtures/</c>, copied to the test output directory alongside
/// <c>dmg.exe</c> by the <c>&lt;None Include="Fixtures\**"&gt;</c> item in this
/// project's <c>.csproj</c>. See <c>Fixtures/README.md</c> for exactly how each one
/// was produced.
/// </para>
/// <para>
/// <b>The known file's content is fixture-independent.</b> <c>tools/make-fixtures.sh</c>
/// writes the same three files onto every populated volume every time it runs;
/// only the container's own bytes change between regenerations (hdiutil bakes a
/// fresh UUID into every image). <see cref="KnownFileContent"/> is therefore safe
/// to hardcode - it is not a hash pinned against one specific generation of the
/// corpus, which the top-level fixtures' own README explicitly forbids.
/// </para>
/// </remarks>
internal static class E2eFixtures
{
    /// <summary>exFAT, UDZO (zlib chunks) - the positive case.</summary>
    public const string ExFatZlib = "exfat-zlib.dmg";

    /// <summary>HFS+, UDZO - the negative case: Windows has no driver for it.</summary>
    public const string HfsPlus = "hfsplus.dmg";

    /// <summary>The name of the known file every populated volume in the corpus carries.</summary>
    public const string KnownFileName = "HELLO.TXT";

    /// <summary>
    /// The known file's exact content - see <c>build_stage()</c> in
    /// <c>tools/make-fixtures.sh</c> and "Volume contents" in <c>fixtures/README.md</c>.
    /// </summary>
    public const string KnownFileContent = "Hello, DMG fixture!\n";

    private static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>The full path of a checked-in fixture, after asserting it is actually there.</summary>
    public static string PathOf(string fileName)
    {
        string full = Path.Combine(Root, fileName);

        if (!File.Exists(full))
        {
            throw new InvalidOperationException(
                $"Fixture '{fileName}' is missing at '{full}'. It should be checked in under "
                + "tests/Dmg.E2E.Tests/Fixtures/ and copied to the output directory by this "
                + "project's .csproj.");
        }

        return full;
    }
}
