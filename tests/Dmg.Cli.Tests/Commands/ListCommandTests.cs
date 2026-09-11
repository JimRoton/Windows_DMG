using Dmg.Cli.Commands;
using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// <c>dmg list</c> end to end, with the Windows-only virtual disk service and
/// filesystem probe behind hand-written fakes.
/// </summary>
public sealed class ListCommandTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _registryDirectory =
        Path.Combine(Path.GetTempPath(), $"dmg-list-registry-{Guid.NewGuid():N}");

    private readonly string _scratchDirectory =
        Path.Combine(Path.GetTempPath(), $"dmg-list-scratch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TryDeleteDirectory(_registryDirectory);
        TryDeleteDirectory(_scratchDirectory);
    }

    [Fact]
    public void AnEmptyRegistryPrintsAHelpfulLineRatherThanABlankTable()
    {
        ListCommand command = NewCommand(new FakeMountVirtualDiskService(), new MountRegistry(_registryDirectory));
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        Assert.Single(output.StdoutLines);
        Assert.Contains("No images are mounted", output.StdoutLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ListsEveryColumnForAMountedImage()
    {
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 4096);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        ListCommand command = NewCommand(
            new FakeMountVirtualDiskService(),
            registry,
            new FakeVolumeFilesystemProbe().With("E", "exFAT"));
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        Assert.Equal(2, output.StdoutLines.Count);
        Assert.Contains("ID", output.StdoutLines[0], StringComparison.Ordinal);
        Assert.Contains("FILESYSTEM", output.StdoutLines[0], StringComparison.Ordinal);

        string row = output.StdoutLines[1];
        Assert.Contains(stored.Id, row, StringComparison.Ordinal);
        Assert.Contains("E:", row, StringComparison.Ordinal);
        Assert.Contains("exFAT", row, StringComparison.Ordinal);
        Assert.Contains("4.00 KiB", row, StringComparison.Ordinal);
        Assert.Contains("ro", row, StringComparison.Ordinal);
        Assert.Contains(@"C:\images\a.dmg", row, StringComparison.Ordinal);
    }

    [Fact]
    public void AMountWithNoDriveLetterShowsADashForLetterAndFilesystem()
    {
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 1024);
        Store(registry, @"C:\images\a.dmg", vhdPath, driveLetter: null);

        ListCommand command = NewCommand(new FakeMountVirtualDiskService(), registry);
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        string row = output.StdoutLines[1];
        Assert.Contains("-", row, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilesystemProbeFailureShowsUnknownRatherThanFailingTheCommand()
    {
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 1024);
        Store(registry, @"C:\images\a.dmg", vhdPath, "E");

        ListCommand command = NewCommand(
            new FakeMountVirtualDiskService(), registry, new FakeVolumeFilesystemProbe());
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        Assert.Contains("unknown", output.StdoutLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputCarriesEveryField()
    {
        MountRegistry registry = new(_registryDirectory);
        string vhdPath = WriteVhd("a.vhd", 2048);
        MountRecord stored = Store(registry, @"C:\images\a.dmg", vhdPath, "F");

        ListCommand command = NewCommand(
            new FakeMountVirtualDiskService(), registry, new FakeVolumeFilesystemProbe().With("F", "NTFS"));
        RecordingOutput output = new(isJson: true);

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        Assert.Contains($"\"id\": \"{stored.Id}\"", output.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"filesystem\": \"NTFS\"", output.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"sizeBytes\": 2048", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputOnAnEmptyRegistryIsAnEmptyArrayNotAMessage()
    {
        ListCommand command = NewCommand(new FakeMountVirtualDiskService(), new MountRegistry(_registryDirectory));
        RecordingOutput output = new(isJson: true);

        Assert.Equal(DmgExitCode.Success, command.Execute(new CliContext([], output.Output)));

        Assert.Contains("\"mounts\": []", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraArgumentsAreAUsageError()
    {
        ListCommand command = NewCommand(new FakeMountVirtualDiskService(), new MountRegistry(_registryDirectory));
        RecordingOutput output = new();

        Assert.Equal(DmgExitCode.UsageError, command.Execute(new CliContext(["nope"], output.Output)));
    }

    [Fact]
    public void ItsSpecTakesNoPositionalArguments()
    {
        ListCommand command = new(new FakeMountVirtualDiskService());

        Assert.Equal("list", command.Spec.Verb);
        Assert.Empty(command.Spec.Positionals);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ListCommand().Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new ListCommand(null!));
    }

    private ListCommand NewCommand(
        FakeMountVirtualDiskService virtualDisks,
        MountRegistry registry,
        FakeVolumeFilesystemProbe? filesystems = null) =>
        new(virtualDisks, () => Result<MountRegistry>.Success(registry), filesystems);

    private static MountRecord Store(MountRegistry registry, string sourcePath, string vhdPath, string? driveLetter)
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
