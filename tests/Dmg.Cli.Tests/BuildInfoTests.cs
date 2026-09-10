using System.Runtime.InteropServices;
using Dmg.Cli;

namespace Dmg.Cli.Tests;

/// <summary>
/// What can be asserted about a build from inside it: that nothing is blank, that
/// the version and the commit are separated, and that the architectures are the
/// ones the runtime reports.
/// </summary>
public sealed class BuildInfoTests
{
    [Fact]
    public void NothingComesBackBlank()
    {
        BuildInfo build = BuildInfo.Current;

        Assert.False(string.IsNullOrWhiteSpace(build.Version));
        Assert.False(string.IsNullOrWhiteSpace(build.Configuration));
        Assert.False(string.IsNullOrWhiteSpace(build.Framework));
        Assert.False(string.IsNullOrWhiteSpace(build.OperatingSystem));
        Assert.All(build.Details, detail =>
        {
            Assert.False(string.IsNullOrWhiteSpace(detail.Label));
            Assert.False(string.IsNullOrWhiteSpace(detail.Value));
        });
    }

    [Fact]
    public void TheVersionIsSplitFromTheCommit()
    {
        BuildInfo build = BuildInfo.Current;

        // "0.1.0+<sha>" is one string from the SDK and two facts. A headline
        // carrying forty characters of hex is a headline nobody reads.
        Assert.DoesNotContain("+", build.Version, StringComparison.Ordinal);
        Assert.DoesNotContain("+", build.Headline, StringComparison.Ordinal);

        if (build.Commit is not null)
        {
            Assert.NotEmpty(build.Commit);
            Assert.Contains(build.Details, detail => detail.Label == "commit");
        }
    }

    [Fact]
    public void TheHeadlineNamesTheToolTheVersionAndTheArchitecture()
    {
        BuildInfo build = BuildInfo.Current;

        Assert.StartsWith("dmg ", build.Headline, StringComparison.Ordinal);
        Assert.Contains(build.Version, build.Headline, StringComparison.Ordinal);
        Assert.Contains(build.Configuration, build.Headline, StringComparison.Ordinal);
        Assert.Contains(
            BuildInfo.Name(build.ProcessArchitecture),
            build.Headline,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchitecturesAreTheRuntimesOwn()
    {
        Assert.Equal(RuntimeInformation.ProcessArchitecture, BuildInfo.Current.ProcessArchitecture);
        Assert.Equal(RuntimeInformation.OSArchitecture, BuildInfo.Current.OsArchitecture);
        Assert.Equal(
            RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture,
            BuildInfo.Current.IsEmulated);
    }

    [Fact]
    public void EmulationIsCalledOutRatherThanLeftToBeInferred()
    {
        BuildInfo build = BuildInfo.Current;
        string runningOn = build.Details.Single(detail => detail.Label == "running on").Value;

        if (build.IsEmulated)
        {
            Assert.Contains("emulation", runningOn, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(BuildInfo.Name(build.OsArchitecture), runningOn);
        }
    }

    [Theory]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm, "arm")]
    [InlineData(Architecture.Arm64, "arm64")]
    public void ArchitecturesAreSpelledTheWayPeopleTypeThem(Architecture architecture, string expected)
    {
        Assert.Equal(expected, BuildInfo.Name(architecture));
    }

    [Fact]
    public void ItIsReadOnce()
    {
        Assert.Same(BuildInfo.Current, BuildInfo.Current);
    }
}
