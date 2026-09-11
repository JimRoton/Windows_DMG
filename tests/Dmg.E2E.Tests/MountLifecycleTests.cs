using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmg.Cli.Output;
using Dmg.Core;

namespace Dmg.E2E.Tests;

/// <summary>
/// S10.4 (#84): mount a real exFAT fixture through the real, compiled
/// <c>dmg.exe</c> and the real <c>virtdisk.dll</c>, prove Windows actually
/// attached it, then unmount it and prove Windows actually let it go.
/// </summary>
/// <remarks>
/// <para>
/// One test walks the whole lifecycle - mount, list, unmount, list again - rather
/// than four independent ones, because steps 2 through 4 only mean anything against
/// the mount step 1 made. Splitting them across separate <c>[Fact]</c> methods would
/// either duplicate the mount in each one (multiplying the slowest, most fragile
/// part of the suite) or rely on xUnit's undocumented execution order to share
/// state between them, which is worse.
/// </para>
/// <para>
/// <b>No partition-table decoding here.</b> <c>mount</c> writes only the selected
/// volume - no partition table - to the scratch VHD, so if Windows ever refuses to
/// assign that VHD a drive letter, that is a real, visible failure this test must
/// report, not paper over. Nothing here retries a missing letter under a different
/// strategy; see <c>docs/AGENT-BRIEF.md</c>.
/// </para>
/// </remarks>
public sealed class MountLifecycleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void MountsListsAndUnmountsTheRealExFatFixture()
    {
        if (E2eEnvironment.Skip(out string? why))
        {
            Assert.True(true, why);
            return;
        }

        string fixturePath = E2eFixtures.PathOf(E2eFixtures.ExFatZlib);

        string? mountedId = null;

        try
        {
            // --- 1. mount: a drive letter appears, and the known file reads back
            // with the exact content and hash the corpus is documented to carry.
            DmgResult mountResult = DmgProcess.Run("--json", "mount", fixturePath);

            Assert.True(
                mountResult.ExitCode == (int)DmgExitCode.Success,
                $"'dmg mount {fixturePath}' exited {mountResult.ExitCode}, expected "
                + $"{(int)DmgExitCode.Success}. stderr:\n{mountResult.StdErr}");

            MountPayload mount = Deserialize<MountPayload>(mountResult.StdOut);
            mountedId = mount.Id;

            Assert.False(
                string.IsNullOrEmpty(mount.DriveLetter),
                "dmg mount reported success but assigned no drive letter. Per docs/AGENT-BRIEF.md this "
                + "is a real failure to let stand, not something to work around: the scratch VHD carries "
                + "only the selected volume, no partition table, and Windows did not give it a letter.");

            string knownFilePath = $"{mount.DriveLetter}:\\{E2eFixtures.KnownFileName}";

            Assert.True(
                File.Exists(knownFilePath),
                $"Expected '{knownFilePath}' to exist on the freshly mounted volume; it does not. "
                + $"Drive letter reported: {mount.DriveLetter}, physical path: {mount.PhysicalPath}.");

            byte[] actualBytes = File.ReadAllBytes(knownFilePath);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(E2eFixtures.KnownFileContent);

            Assert.Equal(Encoding.UTF8.GetString(expectedBytes), Encoding.UTF8.GetString(actualBytes));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedBytes)), Convert.ToHexString(SHA256.HashData(actualBytes)));

            // --- 2. list: the mount just made is on it.
            DmgResult firstListResult = DmgProcess.Run("--json", "list");

            Assert.True(
                firstListResult.ExitCode == (int)DmgExitCode.Success,
                $"'dmg list' exited {firstListResult.ExitCode}, expected {(int)DmgExitCode.Success}. "
                + $"stderr:\n{firstListResult.StdErr}");

            MountListPayload firstList = Deserialize<MountListPayload>(firstListResult.StdOut);

            Assert.Contains(firstList.Mounts, entry => entry.Id == mount.Id);

            // --- unmount: the scratch VHD is deleted.
            DmgResult unmountResult = DmgProcess.Run("--json", "unmount", mount.Id);

            Assert.True(
                unmountResult.ExitCode == (int)DmgExitCode.Success,
                $"'dmg unmount {mount.Id}' exited {unmountResult.ExitCode}, expected "
                + $"{(int)DmgExitCode.Success}. stderr:\n{unmountResult.StdErr}");

            UnmountPayload unmount = Deserialize<UnmountPayload>(unmountResult.StdOut);
            mountedId = null; // Whatever happens below, dmg itself has already detached it.

            Assert.True(unmount.WasMounted, $"'dmg unmount {mount.Id}' reported nothing was mounted there.");
            Assert.True(unmount.ScratchDeleted, "dmg unmount did not delete the scratch VHD.");
            Assert.False(
                File.Exists(mount.VhdPath),
                $"The scratch VHD '{mount.VhdPath}' is still on disk after 'dmg unmount' reported it deleted.");

            // --- list again: empty.
            DmgResult secondListResult = DmgProcess.Run("--json", "list");

            Assert.True(
                secondListResult.ExitCode == (int)DmgExitCode.Success,
                $"'dmg list' exited {secondListResult.ExitCode}, expected {(int)DmgExitCode.Success}. "
                + $"stderr:\n{secondListResult.StdErr}");

            MountListPayload secondList = Deserialize<MountListPayload>(secondListResult.StdOut);

            Assert.Empty(secondList.Mounts);
        }
        finally
        {
            // Best-effort: if an assertion above threw before the unmount step ran,
            // do not leave the fixture attached on the runner for whatever else
            // shares it in this job.
            if (mountedId is not null)
            {
                try
                {
                    DmgProcess.Run("unmount", mountedId);
                }
                catch (Exception)
                {
                    // The assertion failure above is the one this test reports;
                    // a cleanup failure on top of it would only obscure that.
                }
            }
        }
    }

    private static T Deserialize<T>(string json)
    {
        T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);

        return value ?? throw new InvalidOperationException(
            $"dmg --json produced 'null' where a {typeof(T).Name} was expected. Raw output:\n{json}");
    }
}
