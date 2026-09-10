using System.Xml.Linq;

namespace Dmg.Core.Tests;

/// <summary>
/// Guards the structural promises made in story S1.1. These are not unit tests of
/// behaviour - they are a red build for anyone who quietly adds a NuGet package to
/// the shipping code or drags a Windows dependency into <c>Dmg.Core</c>.
/// </summary>
public sealed class RepositoryLayoutTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void SolutionContainsTheSixExpectedProjects()
    {
        string solution = File.ReadAllText(Path.Combine(RepositoryRoot, "Windows_DMG.sln"));

        string[] expected =
        [
            "Dmg.Core.csproj",
            "Dmg.Windows.csproj",
            "Dmg.Cli.csproj",
            "Dmg.Core.Tests.csproj",
            "Dmg.Windows.Tests.csproj",
            "Dmg.E2E.Tests.csproj",
        ];

        foreach (string project in expected)
        {
            Assert.Contains(project, solution, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoProjectUnderSrcDeclaresAPackageReference()
    {
        foreach (string project in ProjectsUnder("src"))
        {
            XDocument document = XDocument.Load(project);

            IEnumerable<string> packages = document
                .Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? "<unnamed>");

            Assert.True(
                !packages.Any(),
                $"{Path.GetFileName(project)} declares PackageReference(s): {string.Join(", ", packages)}. " +
                "Shipping code has zero third-party dependencies.");
        }
    }

    [Fact]
    public void DmgCoreIsPortableAndHasNoWindowsDependency()
    {
        XDocument document = XDocument.Load(
            Path.Combine(RepositoryRoot, "src", "Dmg.Core", "Dmg.Core.csproj"));

        string? targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value;
        Assert.Equal("net10.0", targetFramework);

        IEnumerable<string> references = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty);

        Assert.DoesNotContain(references, reference =>
            reference.Contains("Dmg.Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("src/Dmg.Windows/Dmg.Windows.csproj")]
    [InlineData("src/Dmg.Cli/Dmg.Cli.csproj")]
    [InlineData("tests/Dmg.Windows.Tests/Dmg.Windows.Tests.csproj")]
    [InlineData("tests/Dmg.E2E.Tests/Dmg.E2E.Tests.csproj")]
    public void WindowsOnlyProjectsTargetTheWindowsFlavouredFramework(string relativePath)
    {
        XDocument document = XDocument.Load(
            Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal("net10.0-windows", document.Descendants("TargetFramework").FirstOrDefault()?.Value);
    }

    [Fact]
    public void DirectoryBuildPropsSetsTheNonNegotiableProperties()
    {
        XDocument document = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        AssertProperty(document, "Nullable", "enable");
        AssertProperty(document, "TreatWarningsAsErrors", "true");
        AssertProperty(document, "InvariantGlobalization", "true");
        AssertProperty(document, "ImplicitUsings", "enable");
        AssertProperty(document, "EnableNETAnalyzers", "true");
    }

    [Fact]
    public void ShippingProjectsOptIntoTheAotAnalyzers()
    {
        foreach (string project in ProjectsUnder("src"))
        {
            XDocument document = XDocument.Load(project);
            Assert.Equal("true", document.Descendants("IsAotCompatible").FirstOrDefault()?.Value);
        }
    }

    [Fact]
    public void DmgCoreHasAFolderForEveryPortableArea()
    {
        string core = Path.Combine(RepositoryRoot, "src", "Dmg.Core");

        string[] areas =
        [
            "Containers", "Codecs", "Crypto", "Imaging",
            "Partitions", "Filesystems", "Vhd", "Diagnostics",
        ];

        foreach (string area in areas)
        {
            Assert.True(Directory.Exists(Path.Combine(core, area)), $"Missing area folder: {area}");
        }
    }

    private static void AssertProperty(XDocument document, string name, string expected)
    {
        string? actual = document.Descendants(name).FirstOrDefault()?.Value;
        Assert.True(
            string.Equals(actual, expected, StringComparison.Ordinal),
            $"Directory.Build.props: expected {name} = '{expected}' but found '{actual ?? "<absent>"}'.");
    }

    private static IEnumerable<string> ProjectsUnder(string relativeFolder) =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, relativeFolder),
            "*.csproj",
            SearchOption.AllDirectories);

    /// <summary>
    /// Walks up from the test binaries until the solution file appears. Keeps the
    /// tests independent of how deep the bin/ output happens to be nested.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Windows_DMG.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Windows_DMG.sln above '{AppContext.BaseDirectory}'.");
    }
}
