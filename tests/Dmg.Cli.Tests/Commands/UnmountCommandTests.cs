using Dmg.Cli.Commands;
using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// <c>dmg unmount</c> end to end, with the Windows-only virtual disk service behind
/// <see cref="FakeMountVirtualDiskService"/>. What cannot be exercised here - the
/// real <c>virtdisk.dll</c> - needs a Windows box; see <see cref="DetachService"/>
/// for the logic this verb is a thin wrapper over.
/// </summary>
public sealed class UnmountCommandTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _registryDirectory =
        Path.Combine(Path.GetTempPath(), $"dmg-unmount-registry-{Guid.NewGuid():N}");

    private readonly string _scratchDirectory =
        Path.Combine(Path.GetTempPath(), $"dmg-unmount-scratch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TryDeleteDirectory(_registryDirectory);
        TryDeleteDirectory(_scratchDirectory);
    }

    [Fact]
    public void UnmountingByIdDetachesAndReportsReclaimedSpace()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 4096);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([stored.Id], output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("Unmounted", StringComparison.Ordinal));
        Assert.Contains(output.StdoutLines, line => line.Contains("Reclaimed 4.00 KiB", StringComparison.Ordinal));
        Assert.False(File.Exists(vhdPath));
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void UnmountingByDriveLetterAcceptsTheColonForm()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 1024);
        Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext(["E:"], output.Output)));

        Assert.Empty(registry.Read());
    }

    [Fact]
    public void KeepScratchLeavesTheVhdInPlaceAndReportsNothingReclaimed()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 2048);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext([stored.Id, "--keep-scratch"], output.Output)));

        Assert.True(File.Exists(vhdPath));
        Assert.Contains(output.StdoutLines, line => line.Contains("kept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATargetThatIsNotOneOfOursIsNotAnErrorButSaysSoPlainly()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext(["Z"], output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("Nothing is mounted", StringComparison.Ordinal));
        Assert.Empty(virtualDisks.Calls);
    }

    [Fact]
    public void AnOpenHandleIsReportedAndNothingIsForced()
    {
        FakeMountVirtualDiskService virtualDisks = new() { DetachFailure = DmgError.Internal(
            "The virtual disk could not be detached because a process still has a handle open on E:.") };
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 512);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.InternalError, command.Execute(new CliContext([stored.Id], output.Output)));

        Assert.Contains("handle open", output.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(vhdPath));
        Assert.Single(registry.Read());
    }

    [Fact]
    public void AllDetachesEveryMountAndSumsReclaimedSpace()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);
        string first = WriteVhd("a.vhd", 1024);
        string second = WriteVhd("b.vhd", 2048);
        Store(registry, @"C:\images\a.dmg", first, "E");
        Store(registry, @"C:\images\b.dmg", second, "F");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext(["--all"], output.Output)));

        Assert.Empty(registry.Read());
        Assert.Contains(output.StdoutLines, line => line.Contains("Unmounted 2 of 2", StringComparison.Ordinal));
        Assert.Contains(output.StdoutLines, line => line.Contains("reclaimed 3.00 KiB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllOnAnEmptyRegistrySaysSoRatherThanPrintingNothing()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext(["--all"], output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("Nothing is mounted", StringComparison.Ordinal));
    }

    [Fact]
    public void AllWithABusyHandleExitsMountFailedAndLeavesTheMountOnRecord()
    {
        FakeMountVirtualDiskService virtualDisks = new() { DetachFailure = DmgError.Internal("busy: handle open") };
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 1024);
        Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.MountFailed, command.Execute(new CliContext(["--all"], output.Output)));

        Assert.Single(registry.Read());
        Assert.Contains(output.StdoutLines, line => line.Contains("Could not unmount", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonOutputCarriesTheReclaimedBytes()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 8192);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        UnmountCommand command = NewCommand(virtualDisks, registry);
        RecordingOutput output = new(isJson: true);

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([stored.Id], output.Output)));

        Assert.Contains("\"reclaimedBytes\": 8192", output.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"wasMounted\": true", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAndATargetTogetherIsAUsageError()
    {
        UnmountCommand command = new(new FakeMountVirtualDiskService());
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.UsageError, command.Execute(new CliContext(["E", "--all"], output.Output)));
    }

    [Fact]
    public void NoTargetAndNoAllIsAUsageError()
    {
        UnmountCommand command = new(new FakeMountVirtualDiskService());
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.UsageError, command.Execute(new CliContext([], output.Output)));
        Assert.Contains("needs a drive letter or an id", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ItsHelpNamesEveryOptionItAccepts()
    {
        UnmountCommand command = new(new FakeMountVirtualDiskService());

        Assert.Equal("unmount", command.Spec.Verb);
        Assert.Contains(command.Spec.Options, option => option.Name == "all");
        Assert.Contains(command.Spec.Options, option => option.Name == "keep-scratch");
        Assert.NotEmpty(command.Spec.Notes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new UnmountCommand().Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new UnmountCommand(null!));
    }

    private UnmountCommand NewCommand(FakeMountVirtualDiskService virtualDisks, MountRegistry registry) =>
        new(virtualDisks, () => Result<MountRegistry>.Success(registry));

    private static MountRecord Store(MountRegistry registry, string sourcePath, string vhdPath, string driveLetter)
    {
        Result<MountRecord> created = MountRecord.Create(
            sourcePath, vhdPath, driveLetter, VirtualDiskAccessMode.ReadOnly, Noon);
        Assert.True(created.Ok, created.Ok ? string.Empty : created.Error.ToString());

        Result<MountRecord> added = registry.Add(created.Value!);
        Assert.True(added.Ok, added.Ok ? string.Empty : added.Error.ToString());

        return added.Value!;
    }

    private string WriteVhd(string name, int bytes)
    {
        Directory.CreateDirectory(_scratchDirectory);
        string path = Path.Combine(_scratchDirectory, name);
        File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file here does not invalidate the test.
        }
    }
}
