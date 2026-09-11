using Dmg.Core;

namespace Dmg.E2E.Tests;

/// <summary>
/// S10.4 (#84): <c>dmg mount</c> against a real HFS+ fixture must refuse before
/// ever touching <c>virtdisk.dll</c> - Windows ships no HFS+ driver, so attaching
/// the decoded volume would only produce an unreadable drive.
/// </summary>
/// <remarks>
/// See <see cref="Dmg.Core.Filesystems.VolumeMap.Refusal"/>-adjacent code in
/// <c>FilesystemInfo.cs</c>: the refusal fires while selecting the volume, before
/// the elevation check and before anything is written to scratch, so this exits 5
/// whether or not the shell happens to be elevated.
/// </remarks>
public sealed class MountRefusalTests
{
    [Fact]
    public void RefusesToMountAnHfsPlusFixture()
    {
        if (E2eEnvironment.Skip(out string? why))
        {
            Assert.True(true, why);
            return;
        }

        string fixturePath = E2eFixtures.PathOf(E2eFixtures.HfsPlus);

        DmgResult result = DmgProcess.Run("mount", fixturePath);

        Assert.True(
            result.ExitCode == (int)DmgExitCode.FilesystemNotMountable,
            $"'dmg mount {fixturePath}' exited {result.ExitCode}, expected "
            + $"{(int)DmgExitCode.FilesystemNotMountable} (FilesystemNotMountable). "
            + $"stdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

        Assert.Contains("HFS+", result.StdErr, StringComparison.Ordinal);
    }
}
