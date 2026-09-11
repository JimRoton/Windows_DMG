using Dmg.Cli.Commands;
using Dmg.Cli.Tests.Info;
using Dmg.Core;
using Dmg.Core.Crypto;
using Dmg.Windows.Mounts;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// The verb end to end, with the Windows-only pieces behind hand-written fakes:
/// real images decoded to a scratch VHD, the right ordering of checks, and cleanup
/// on every failure that never actually attached anything. Every fixture-backed
/// test degrades to a pass when the images have not been generated. What cannot be
/// exercised here - the real <c>virtdisk.dll</c> and volume APIs - needs a Windows
/// box; see the class remarks on <see cref="MountCommand"/>.
/// </summary>
public sealed class MountCommandTests : IDisposable
{
    private readonly string _scratchRoot =
        Path.Combine(Path.GetTempPath(), $"dmg-mount-scratch-{Guid.NewGuid():N}");

    private readonly string _registryDirectory =
        Path.Combine(Path.GetTempPath(), $"dmg-mount-registry-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TryDeleteDirectory(_scratchRoot);
        TryDeleteDirectory(_registryDirectory);
    }

    [Fact]
    public void ASuccessfulMountWritesTheRegistryAndReportsTheDriveLetter()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        FakeMountVolumeService volumes = new FakeMountVolumeService().WithDriveLetter("E");
        MountCommand command = NewCommand(virtualDisks, FakeMountPrivilegeService.Elevated(), volumes);
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("Mounted", StringComparison.Ordinal));
        Assert.Contains(output.StdoutLines, line => line.Contains("E:", StringComparison.Ordinal));

        MountRegistry registry = new(_registryDirectory);
        IReadOnlyList<MountRecord> records = registry.Read();

        Assert.Single(records);
        Assert.Equal("E", records[0].DriveLetter);
        Assert.Equal(path, records[0].SourcePath);
        Assert.True(File.Exists(records[0].VhdPath));

        // The disk is "attached": nothing must have been cleaned up.
        Assert.True(File.Exists(records[0].VhdPath));
        Assert.Contains(virtualDisks.Calls, call => call.StartsWith("Attach(", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonOutputCarriesTheDriveLetterAndTheId()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        MountCommand command = NewCommand(
            new FakeMountVirtualDiskService(),
            FakeMountPrivilegeService.Elevated(),
            new FakeMountVolumeService().WithDriveLetter("F"));

        RecordingOutput output = new(isJson: true);

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Contains("\"driveLetter\": \"F\"", output.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"id\"", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RwPassesReadWriteToTheOpenCall()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(
            virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService().WithDriveLetter("E"));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot, "--rw"], output.Output)));

        Assert.Contains(virtualDisks.Calls, call => call == "Open(ReadWrite)");
    }

    [Fact]
    public void DefaultAccessIsReadOnly()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(
            virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService().WithDriveLetter("E"));

        RecordingOutput output = new();

        command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output));

        Assert.Contains(virtualDisks.Calls, call => call == "Open(ReadOnly)");
    }

    [Fact]
    public void AnExplicitLetterIsAssignedRatherThanWaitedFor()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(
            virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.Success,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot, "--letter", "Q:"], output.Output)));

        Assert.Contains(output.StdoutLines, line => line.Contains("Q:", StringComparison.Ordinal));
        Assert.Contains(virtualDisks.Calls, call => call.Contains("noDriveLetter=True", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvalidLetterIsAUsageErrorBeforeAnyWorkHappens()
    {
        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            command.Execute(new CliContext(["a.dmg", "--letter", "nope"], output.Output)));

        Assert.Empty(virtualDisks.Calls);
    }

    [Fact]
    public void AnUnelevatedShellIsRefusedBeforeTheVhdIsWritten()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(
            virtualDisks, FakeMountPrivilegeService.Unelevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.ElevationRequired,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Empty(virtualDisks.Calls);
        Assert.Contains("does not have", output.Stderr, StringComparison.OrdinalIgnoreCase);

        // Nothing was attached, so the (empty) scratch directory must be gone -
        // only the root the test asked for is left, and it is empty.
        Assert.True(!Directory.Exists(_scratchRoot) || !Directory.EnumerateFileSystemEntries(_scratchRoot).Any());
    }

    [Fact]
    public void InsufficientScratchSpaceIsRefusedBeforeElevationEvenMatters()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();

        // Unelevated on purpose: the free-space precheck must fail first, so an
        // unelevated shell never even gets asked about its privileges for this.
        MountCommand command = new(
            new PassphraseReader(),
            virtualDisks,
            FakeMountPrivilegeService.Unelevated(),
            new FakeMountVolumeService(),
            () => Result<MountRegistry>.Success(new MountRegistry(_registryDirectory)),
            new FakeFreeSpaceProbe(availableBytes: 1));

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.InsufficientSpace,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Empty(virtualDisks.Calls);
        Assert.True(!Directory.Exists(_scratchRoot) || !Directory.EnumerateFileSystemEntries(_scratchRoot).Any());
    }

    [Fact]
    public void AnImageWithNothingMountableIsFilesystemNotMountableBeforeAnyScratchIsMade()
    {
        // HFS+: Windows has no driver for it, so nothing here is mountable at all.
        if (Fixtures.Path("hfsplus.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.FilesystemNotMountable,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Empty(virtualDisks.Calls);

        // ScratchSpace.Create never ran: volume selection fails first.
        Assert.False(Directory.Exists(_scratchRoot));
    }

    [Fact]
    public void ANonExistentPartitionIsAUsageError()
    {
        if (Fixtures.Path("exfat-zlib.dmg") is not string path)
        {
            return;
        }

        MountCommand command = NewCommand(
            new FakeMountVirtualDiskService(), FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            command.Execute(new CliContext([path, "--partition", "99"], output.Output)));
    }

    [Fact]
    public void MissingArgumentsAreAUsageError()
    {
        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.UsageError,
            new MountCommand().Execute(new CliContext([], output.Output)));

        Assert.Contains("needs an image", output.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedImageWithNoPassphraseIsDecryptionFailed()
    {
        if (Fixtures.Path("exfat-enc256.dmg") is not string path)
        {
            return;
        }

        FakeMountVirtualDiskService virtualDisks = new();
        MountCommand command = NewCommand(virtualDisks, FakeMountPrivilegeService.Elevated(), new FakeMountVolumeService());

        RecordingOutput output = new();

        Assert.Equal(
            DmgExitCode.DecryptionFailed,
            command.Execute(new CliContext([path, "--scratch", _scratchRoot], output.Output)));

        Assert.Empty(virtualDisks.Calls);
    }

    [Fact]
    public void ItsHelpNamesEveryOptionItAccepts()
    {
        MountCommand command = new();

        Assert.Equal("mount", command.Spec.Verb);
        Assert.Equal(["IMAGE"], command.Spec.Positionals);
        Assert.Contains(command.Spec.Options, option => option.Name == "partition");
        Assert.Contains(command.Spec.Options, option => option.Name == "rw");
        Assert.Contains(command.Spec.Options, option => option.Name == "letter");
        Assert.Contains(command.Spec.Options, option => option.Name == "scratch");
        Assert.Contains(command.Spec.Options, option => option.Name == "keep-scratch");
        Assert.Contains(command.Spec.Options, option => option.Name == "password-stdin");
        Assert.Contains(command.Spec.Options, option => option.Name == "password-env");
        Assert.NotEmpty(command.Spec.Notes);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MountCommand().Execute(null!));

        Assert.Throws<ArgumentNullException>(() => new MountCommand(
            null!,
            new FakeMountVirtualDiskService(),
            FakeMountPrivilegeService.Elevated(),
            new FakeMountVolumeService()));
    }

    private MountCommand NewCommand(
        FakeMountVirtualDiskService virtualDisks,
        FakeMountPrivilegeService privileges,
        FakeMountVolumeService volumes) =>
        new(
            new PassphraseReader(),
            virtualDisks,
            privileges,
            volumes,
            () => Result<MountRegistry>.Success(new MountRegistry(_registryDirectory)));

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
