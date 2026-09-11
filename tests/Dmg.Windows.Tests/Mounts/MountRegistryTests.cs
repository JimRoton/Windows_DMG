using System.Reflection;
using System.Text.Json.Serialization;
using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.Mounts;

/// <summary>
/// The registry is the only record of which drive letter came from which image, and
/// it lives in a folder the user can open, on a machine that can lose power
/// mid-write. So these tests are mostly about damage: every way the file can be
/// wrong has to cost a warning and an empty list, never an exception in the middle
/// of somebody's <c>dmg list</c>.
/// </summary>
/// <remarks>
/// Every registry here is built on a <see cref="TemporaryDirectory"/>. Nothing in
/// this file may reach the real <c>%LOCALAPPDATA%\dmg</c>.
/// </remarks>
public sealed class MountRegistryTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMachineThatHasNeverMountedAnythingReadsAsEmptyAndSaysNothing()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        Assert.Empty(registry.Read());

        // "No mounts yet" is the ordinary state of a fresh machine, not a problem.
        Assert.Empty(output.Warnings);
        Assert.False(File.Exists(registry.FilePath));
    }

    [Fact]
    public void ReadingDoesNotCreateTheFolder()
    {
        using TemporaryDirectory directory = new();
        string missing = directory.File("never-created");
        MountRegistry registry = new(missing);

        Assert.Empty(registry.Read());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void AMountSurvivesEveryFieldOfARoundTrip()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        MountRecord added = Store(registry, @"C:\images\ubuntu.dmg", @"C:\scratch\ubuntu.vhd", "E");

        MountRecord read = Assert.Single(new MountRegistry(directory.Path).Read());

        Assert.Equal(added.Id, read.Id);
        Assert.Equal(@"C:\images\ubuntu.dmg", read.SourcePath);
        Assert.Equal(@"C:\scratch\ubuntu.vhd", read.VhdPath);
        Assert.Equal("E", read.DriveLetter);
        Assert.Equal(MountMode.ReadOnly, read.Mode);
        Assert.Equal(Noon, read.MountedAtUtc);
    }

    [Fact]
    public void AReadWriteMountReadsBackAsReadWrite()
    {
        // The field that must never drift. `dmg list` says whether a mount can be
        // written to without asking Windows, and saying "read-only" about a
        // writable disk is how someone loses an image.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E", VirtualDiskAccessMode.ReadWrite);

        MountRecord read = Assert.Single(registry.Read());

        Assert.Equal(MountMode.ReadWrite, read.Mode);
        Assert.Equal(VirtualDiskAccessMode.ReadWrite, read.AccessMode);
    }

    [Fact]
    public void ALetterlessMountRoundTripsAsHavingNoLetter()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", driveLetter: null);

        MountRecord read = Assert.Single(registry.Read());

        Assert.Null(read.DriveLetter);
        Assert.False(read.HasDriveLetter);
    }

    [Fact]
    public void MountsComeBackInTheOrderTheyWereAdded()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        Store(registry, @"C:\images\b.dmg", @"C:\scratch\b.vhd", "F");
        Store(registry, @"C:\images\c.dmg", @"C:\scratch\c.vhd", "G");

        Assert.Equal(
            ["E", "F", "G"],
            registry.Read().Select(record => record.DriveLetter));
    }

    [Fact]
    public void TheFolderIsCreatedOnTheFirstWrite()
    {
        using TemporaryDirectory directory = new();
        string nested = Path.Combine(directory.Path, "dmg");
        MountRegistry registry = new(nested);

        Assert.False(Directory.Exists(nested));

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        Assert.True(File.Exists(registry.FilePath));
    }

    [Fact]
    public void TheFileIsVersionedCamelCaseJsonAPersonCanRead()
    {
        // Someone will open this file while working out why a drive letter is still
        // taken. It is also the reason for the version field: a bare array has
        // nowhere to say which schema wrote it.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        string json = File.ReadAllText(registry.FilePath);

        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"mounts\"", json, StringComparison.Ordinal);
        Assert.Contains("\"driveLetter\": \"E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"ro\"", json, StringComparison.Ordinal);
        Assert.Contains('\n', json);
    }

    [Fact]
    public void TheSerializerIsSourceGeneratedBecauseTheCliShipsAheadOfTimeCompiled()
    {
        // Not a style preference. Reflection-based serialization compiles, passes
        // on a JIT test host, and then fails on the NativeAOT binary that actually
        // ships - the metadata it needs is exactly what AOT publishing trims. This
        // test fails the build if the context is ever swapped out for the
        // reflection path.
        Type? context = typeof(MountRegistry).Assembly
            .GetType("Dmg.Windows.Mounts.MountRegistryJson", throwOnError: false);

        Assert.NotNull(context);
        Assert.True(typeof(JsonSerializerContext).IsAssignableFrom(context));

        JsonSourceGenerationOptionsAttribute? options =
            context.GetCustomAttribute<JsonSourceGenerationOptionsAttribute>();

        Assert.NotNull(options);
        Assert.Equal(JsonKnownNamingPolicy.CamelCase, options.PropertyNamingPolicy);
    }

    [Fact]
    public void NoTemporaryFileIsLeftBesideTheRegistry()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        Assert.False(File.Exists(registry.FilePath + ".tmp"));
    }

    [Fact]
    public void AnIdThatIsAlreadyTakenIsReplacedRatherThanDuplicated()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        MountRecord first = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        MountRecord second = Store(
            registry,
            @"C:\images\b.dmg",
            @"C:\scratch\b.vhd",
            "F",
            id: first.Id);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, registry.Read().Count);
        Assert.Equal(2, registry.Read().Select(record => record.Id).Distinct().Count());
    }

    [Fact]
    public void AddRefusesARecordThatIsNotWorthWriting()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Result<MountRecord> result = registry.Add(
            new MountRecord("abcd1234", string.Empty, @"C:\scratch\a.vhd", "E", MountMode.ReadOnly, Noon));

        Assert.False(result.Ok);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void RemovingAMountLeavesTheOthersAlone()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        MountRecord first = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        Store(registry, @"C:\images\b.dmg", @"C:\scratch\b.vhd", "F");

        Result<bool> removed = registry.Remove(first.Id);

        Assert.True(removed.Ok);
        Assert.True(removed.Value);
        Assert.Equal("F", Assert.Single(registry.Read()).DriveLetter);
    }

    [Fact]
    public void AnIdIsMatchedWhateverCaseItIsTypedIn()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        MountRecord stored = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        Assert.True(registry.Remove(stored.Id.ToUpperInvariant()).Value);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void RemovingSomethingThatIsAlreadyGoneIsNotAFailure()
    {
        // The user asked for it not to be there, and it is not there. Reporting an
        // error would make `dmg unmount --all` fail on the second run.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Result<bool> removed = registry.Remove("deadbeef");

        Assert.True(removed.Ok);
        Assert.False(removed.Value);
    }

    [Fact]
    public void ReplaceSwapsTheWholeListAtOnce()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        Store(registry, @"C:\images\b.dmg", @"C:\scratch\b.vhd", "F");

        Result replaced = registry.Replace([Valid() with { DriveLetter = "Z" }]);

        Assert.True(replaced.Ok, replaced.Ok ? string.Empty : replaced.Error.ToString());
        Assert.Equal("Z", Assert.Single(registry.Read()).DriveLetter);
    }

    [Fact]
    public void ReplaceRefusesAnInvalidRecordInsteadOfSilentlyDroppingIt()
    {
        // Reconciliation (S8.8) writes through this. A dropped entry there is a
        // mount nobody can find again, so a caller that hands over nonsense is told
        // rather than accommodated.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        Result replaced = registry.Replace([Valid() with { Mode = "sideways" }]);

        Assert.False(replaced.Ok);
        Assert.Equal("E", Assert.Single(registry.Read()).DriveLetter);
    }

    [Theory]
    [InlineData("this is not JSON at all")]
    [InlineData("{\"version\": 1, \"mounts\": [")]
    [InlineData("[{\"id\":\"abcd1234\"}]")]
    [InlineData("{\"version\": \"one\"}")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    public void ADamagedRegistryIsReportedAndReadsAsEmptyRatherThanThrowing(string contents)
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, contents);

        Assert.Empty(registry.Read());

        // Reported, not swallowed. A registry that is quietly ignored looks exactly
        // like one that was empty, and the user's mounts have vanished either way.
        Assert.NotEmpty(output.Warnings);
        Assert.True(output.WarnedAbout("mount registry"));
    }

    [Fact]
    public void AFileTruncatedMidWriteIsReportedAndReadsAsEmpty()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        string whole = File.ReadAllText(registry.FilePath);
        File.WriteAllText(registry.FilePath, whole[..(whole.Length / 2)]);

        RecordingOutput output = new();

        Assert.Empty(new MountRegistry(directory.Path, output).Read());
        Assert.NotEmpty(output.Warnings);
    }

    [Fact]
    public void AZeroLengthFileIsReportedBecauseTheMountsThatWereInItAreGone()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, string.Empty);

        Assert.Empty(registry.Read());
        Assert.True(output.WarnedAbout("empty"));
    }

    [Fact]
    public void AFileFromAFutureVersionIsIgnoredAndSaysWhichVersionWroteIt()
    {
        // Not parsed hopefully. A later schema may mean anything by these fields,
        // and acting on a half-understood file is worse than ignoring it.
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, "{\"version\": 99, \"mounts\": []}");

        Assert.Empty(registry.Read());
        Assert.True(output.WarnedAbout("schema 99"));
    }

    [Fact]
    public void AMissingMountsPropertyReadsAsNoMountsRatherThanANullReference()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, "{\"version\": 1}");

        Assert.Empty(registry.Read());
        Assert.Empty(output.Warnings);
    }

    [Fact]
    public void OneBadEntryCostsTheUserThatEntryAndNothingElse()
    {
        // The point of validating per record rather than per file. Three mounts and
        // one hand-mangled line should leave the user with three mounts.
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, """
            {
              "version": 1,
              "mounts": [
                { "id": "aaaa1111", "sourcePath": "C:\\a.dmg", "vhdPath": "C:\\a.vhd",
                  "driveLetter": "E", "mode": "ro", "mountedAtUtc": "2026-09-10T12:00:00+00:00" },
                { "id": "bbbb2222", "sourcePath": "C:\\b.dmg", "vhdPath": "C:\\b.vhd",
                  "driveLetter": "F", "mode": "sideways", "mountedAtUtc": "2026-09-10T12:00:00+00:00" },
                { "id": "cccc3333", "sourcePath": "C:\\c.dmg", "vhdPath": "C:\\c.vhd",
                  "driveLetter": "G", "mode": "rw", "mountedAtUtc": "2026-09-10T12:00:00+00:00" },
                null
              ]
            }
            """);

        IReadOnlyList<MountRecord> mounts = registry.Read();

        Assert.Equal(["aaaa1111", "cccc3333"], mounts.Select(record => record.Id));
        Assert.Equal(2, output.Warnings.Count);
    }

    [Fact]
    public void AnIdThatAppearsTwiceKeepsTheFirstAndReportsTheSecond()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, """
            {
              "version": 1,
              "mounts": [
                { "id": "aaaa1111", "sourcePath": "C:\\first.dmg", "vhdPath": "C:\\a.vhd",
                  "driveLetter": "E", "mode": "ro", "mountedAtUtc": "2026-09-10T12:00:00+00:00" },
                { "id": "AAAA1111", "sourcePath": "C:\\second.dmg", "vhdPath": "C:\\b.vhd",
                  "driveLetter": "F", "mode": "ro", "mountedAtUtc": "2026-09-10T12:00:00+00:00" }
              ]
            }
            """);

        MountRecord kept = Assert.Single(registry.Read());

        Assert.Equal(@"C:\first.dmg", kept.SourcePath);
        Assert.True(output.WarnedAbout("appears twice"));
    }

    [Fact]
    public void AFileClaimingMoreMountsThanCouldExistIsRefusedWhole()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        string entries = string.Join(",", Enumerable.Range(0, MountRegistry.MaximumRecords + 1).Select(index =>
            $$"""{ "id": "{{index:x8}}", "sourcePath": "C:\\a.dmg", "vhdPath": "C:\\a.vhd", "driveLetter": "E", "mode": "ro", "mountedAtUtc": "2026-09-10T12:00:00+00:00" }"""));

        File.WriteAllText(registry.FilePath, $$"""{ "version": 1, "mounts": [{{entries}}] }""");

        Assert.Empty(registry.Read());
        Assert.True(output.WarnedAbout("more mounts than could possibly exist"));
    }

    [Fact]
    public void AFileFarLargerThanARegistryCouldBeIsNotEvenParsed()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        using (FileStream stream = File.Create(registry.FilePath))
        {
            stream.SetLength(MountRegistry.MaximumFileBytes + 1);
        }

        Assert.Empty(registry.Read());
        Assert.True(output.WarnedAbout("larger"));
    }

    [Fact]
    public void TheNextWriteAfterDamageStartsAFreshRegistry()
    {
        // The corollary of "a broken registry never breaks a command": the next
        // write replaces it. A registry nobody can read is worth less than an
        // accurate one starting now, and leaving it would mean it stays broken.
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);

        File.WriteAllText(registry.FilePath, "{ not json");

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        Assert.Equal("E", Assert.Single(new MountRegistry(directory.Path).Read()).DriveLetter);
    }

    [Fact]
    public void ARegistryWithNowhereToReportToStillDoesNotThrow()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        File.WriteAllText(registry.FilePath, "{ not json");

        Assert.Empty(registry.Read());
    }

    [Fact]
    public void TheLockFileSitsBesideTheRegistry()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Assert.Equal(Path.Combine(directory.Path, MountRegistry.FileName), registry.FilePath);
        Assert.Equal(Path.Combine(directory.Path, MountRegistry.LockFileName), registry.LockPath);
    }

    [Fact]
    public void AWriterThatCannotGetTheLockGivesUpRatherThanHanging()
    {
        // An update takes milliseconds, so a wait of any length means something has
        // gone wrong. Blocking a command line forever is not the honest answer.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path, output: null, lockTimeout: TimeSpan.FromMilliseconds(100));

        using FileStream holder = new(
            registry.LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Result<MountRecord> result = registry.Add(Valid());

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.MountFailed, result.Error.Code);
        Assert.Contains("Another dmg process", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingDoesNotWaitBehindSomebodyElsesMount()
    {
        // Readers deliberately take no lock: the write is a move into place, so a
        // reader sees one whole file or the other. Making `dmg list` block behind
        // another process's mount would buy nothing.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");

        using FileStream holder = new(
            registry.LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Equal("E", Assert.Single(registry.Read()).DriveLetter);
    }

    [Fact]
    public void TheLockIsReleasedSoTheNextWriteGoesStraightThrough()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path, output: null, lockTimeout: TimeSpan.Zero);

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        Store(registry, @"C:\images\b.dmg", @"C:\scratch\b.vhd", "F");

        Assert.Equal(2, registry.Read().Count);
    }

    [Fact]
    public void EightMountsStartedAtOnceAllSurvive()
    {
        // The failure the lock exists for: two mounts a second apart, each reading
        // the list as it was before the other added to it, and the first mount
        // silently disappearing from the file.
        using TemporaryDirectory directory = new();
        const int writers = 8;

        Parallel.For(0, writers, index =>
        {
            MountRegistry registry = new(directory.Path);

            Result<MountRecord> added = registry.Add(
                Valid() with
                {
                    Id = $"{index:x8}",
                    SourcePath = $@"C:\images\{index}.dmg",
                    VhdPath = $@"C:\scratch\{index}.vhd",
                });

            Assert.True(added.Ok, added.Ok ? string.Empty : added.Error.ToString());
        });

        Assert.Equal(writers, new MountRegistry(directory.Path).Read().Count);
    }

    [Fact]
    public void ReconciledReadKeepsAMountWhoseDiskIsStillAttached()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        MountRecord stored = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        virtualDiskService.AddDisk(stored.VhdPath).MarkAttached();

        Assert.Equal("E", Assert.Single(registry.Read(virtualDiskService)).DriveLetter);
    }

    [Fact]
    public void ReconciledReadDropsAMountWhoseDiskIsNoLongerAttached()
    {
        // The reboot case: the registry still says E: is ubuntu.dmg, but Windows
        // does not remember an attached virtual disk across a restart, so the
        // fake's disk comes back not attached - same as a real one would.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        virtualDiskService.AddDisk(@"C:\scratch\a.vhd");

        Assert.Empty(registry.Read(virtualDiskService));
    }

    [Fact]
    public void ReconciledReadDropsAMountWhoseVhdIsGoneEntirely()
    {
        // Nothing was even added to the fake for this path - Open() fails exactly
        // as it would for a scratch file that got deleted by hand.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\gone.vhd", "E");

        Assert.Empty(registry.Read(virtualDiskService));
    }

    [Fact]
    public void ReconciledReadPersistsThePruneSoAPlainReadAgreesAfterwards()
    {
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        Store(registry, @"C:\images\ghost.dmg", @"C:\scratch\ghost.vhd", "E");
        MountRecord live = Store(registry, @"C:\images\live.dmg", @"C:\scratch\live.vhd", "F");
        virtualDiskService.AddDisk(live.VhdPath).MarkAttached();

        registry.Read(virtualDiskService);

        Assert.Equal("F", Assert.Single(registry.Read()).DriveLetter);
    }

    [Fact]
    public void ReconciliationIsSilentByDefaultAndTracesWhenSomethingWasPruned()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);
        FakeVirtualDiskService virtualDiskService = new();

        Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        virtualDiskService.AddDisk(@"C:\scratch\a.vhd");

        registry.Read(virtualDiskService);

        // Nothing at Warning/Error - a ghost from a reboot is not a problem to
        // surface by default - but a trace is there for --verbose to show.
        Assert.Empty(output.Warnings);
        Assert.Empty(output.Errors);
        Assert.True(output.TracedAbout("no longer attached"));
    }

    [Fact]
    public void ReconciliationSaysNothingAtAllWhenEveryDiskIsStillAttached()
    {
        using TemporaryDirectory directory = new();
        RecordingOutput output = new();
        MountRegistry registry = new(directory.Path, output);
        FakeVirtualDiskService virtualDiskService = new();

        MountRecord stored = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E");
        virtualDiskService.AddDisk(stored.VhdPath).MarkAttached();

        registry.Read(virtualDiskService);

        Assert.Empty(output.Traces);
    }

    [Theory]
    [InlineData(VirtualDiskAccessMode.ReadOnly)]
    [InlineData(VirtualDiskAccessMode.ReadWrite)]
    public void ReconciliationOpensReadOnlyRegardlessOfTheRecordedMode(VirtualDiskAccessMode mode)
    {
        // A disk that has since become write-protected is still attached; asking
        // "is it there?" must not fail just because a read-write mount could no
        // longer be re-opened for writing.
        using TemporaryDirectory directory = new();
        MountRegistry registry = new(directory.Path);
        FakeVirtualDiskService virtualDiskService = new();

        MountRecord stored = Store(registry, @"C:\images\a.dmg", @"C:\scratch\a.vhd", "E", mode);
        FakeVirtualDisk disk = virtualDiskService.AddDisk(stored.VhdPath).MarkAttached();
        disk.IsWriteProtected = true;

        Assert.Single(registry.Read(virtualDiskService));
    }

    [Fact]
    public void TheRegistryForTheCurrentUserSitsInADmgFolderUnderLocalApplicationData()
    {
        // Path arithmetic only - this creates nothing, which is why it is safe to
        // run against the real folder.
        Result<MountRegistry> result = MountRegistry.ForCurrentUser();

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());

        MountRegistry registry = result.Value!;

        Assert.Equal(MountRegistry.ApplicationFolderName, Path.GetFileName(registry.DirectoryPath));
        Assert.Equal(MountRegistry.FileName, Path.GetFileName(registry.FilePath));
        Assert.Equal(MountRegistry.LockFileName, Path.GetFileName(registry.LockPath));
    }

    [Fact]
    public void ARegistryNeedsSomewhereToLive()
    {
        Assert.Throws<ArgumentException>(() => new MountRegistry("   "));
    }

    private static MountRecord Valid() => new(
        "0123abcd",
        @"C:\images\ubuntu.dmg",
        @"C:\scratch\ubuntu.vhd",
        "E",
        MountMode.ReadOnly,
        Noon);

    private static MountRecord Store(
        MountRegistry registry,
        string sourcePath,
        string vhdPath,
        string? driveLetter,
        VirtualDiskAccessMode mode = VirtualDiskAccessMode.ReadOnly,
        string? id = null)
    {
        Result<MountRecord> created = MountRecord.Create(sourcePath, vhdPath, driveLetter, mode, Noon, id);
        Assert.True(created.Ok, created.Ok ? string.Empty : created.Error.ToString());

        Result<MountRecord> added = registry.Add(created.Value!);
        Assert.True(added.Ok, added.Ok ? string.Empty : added.Error.ToString());

        return added.Value!;
    }
}
