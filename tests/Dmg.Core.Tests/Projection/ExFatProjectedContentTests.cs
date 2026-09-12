using System.Text;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Projection;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Projection;

/// <summary>
/// Path resolution over a real exFAT volume: the half of the projection that has
/// nothing to do with Windows.
/// </summary>
/// <remarks>
/// <para>
/// <a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a> puts
/// <see cref="ExFatProjectedContent"/> in <c>Dmg.Core</c> precisely so these can
/// run on a Mac. What ProjFS will ask for - list this directory, describe this
/// path, read this range - is answered here, and only the P/Invoke that asks is
/// Windows-bound.
/// </para>
/// <para>
/// The expected bytes are the generator's, restated independently: see
/// <see cref="Filesystems.ExFatReaderFixtureTests"/> for why that direction
/// matters.
/// </para>
/// </remarks>
public sealed class ExFatProjectedContentTests
{
    private static ReadOnlySpan<byte> Hello => "Hello, DMG fixture!\n"u8;

    [SkippableFact]
    public void TheRootListsTheKnownFiles()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        IReadOnlyList<ProjectedItem> items = Listed(fixture.Content, string.Empty);

        string[] names = [.. items.Select(item => item.Name).Order(StringComparer.Ordinal)];

        Assert.Contains("HELLO.TXT", names);
        Assert.Contains("README.TXT", names);
        Assert.Contains("DATA.BIN", names);
    }

    [SkippableFact]
    public void AFileIsFoundByNameAndReportsItsLength()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ProjectedItem item = Found(fixture.Content, "HELLO.TXT");

        Assert.False(item.IsDirectory);
        Assert.Equal(Hello.Length, item.Length);
        Assert.Equal("HELLO.TXT", item.Name);
    }

    [SkippableTheory]
    [InlineData("hello.txt")]
    [InlineData("HeLLo.TxT")]
    public void NamesMatchWithoutRegardToCase(string path)
    {
        // exFAT preserves case and matches without it, and so does Windows. Matching
        // case-sensitively would make a file Explorer can see impossible to open.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Assert.Equal(Hello.Length, Found(fixture.Content, path).Length);
    }

    [SkippableTheory]
    [InlineData("/HELLO.TXT")]
    [InlineData("\\HELLO.TXT")]
    [InlineData("HELLO.TXT/")]
    [InlineData("//HELLO.TXT")]
    public void EitherSeparatorAndStraySeparatorsResolveTheSameWay(string path)
    {
        // ProjFS hands back Windows separators; tests would rather write the other
        // one. Both work, and an empty segment is dropped rather than matched.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Assert.Equal(Hello.Length, Found(fixture.Content, path).Length);
    }

    [SkippableFact]
    public void APathThatIsNotThereComesBackNullRatherThanFailing()
    {
        // A projection is asked about paths that do not exist constantly - desktop.ini,
        // folder icons, whatever the shell is curious about. That is an ordinary
        // answer, not an error to report.
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Result<ProjectedItem?> found = fixture.Content.Find("NOT-THERE.TXT");

        Assert.True(found.Ok, found.Ok ? "" : found.Error.ToString());
        Assert.Null(found.Value);
    }

    [SkippableFact]
    public void APathBelowAFileIsNotThereRatherThanAFailure()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Result<ProjectedItem?> found = fixture.Content.Find("HELLO.TXT/inner.txt");

        Assert.True(found.Ok, found.Ok ? "" : found.Error.ToString());
        Assert.Null(found.Value);
    }

    [SkippableFact]
    public void TheRootItselfIsADirectory()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        ProjectedItem root = Found(fixture.Content, string.Empty);

        Assert.True(root.IsDirectory);
    }

    [SkippableFact]
    public void AFileReadsBackByteForByte()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Result<byte[]> read = fixture.Content.Read("HELLO.TXT", 0, Hello.Length);

        Assert.True(read.TryGetValue(out byte[]? bytes), read.Ok ? "" : read.Error.ToString());

        Assert.True(
            Hello.SequenceEqual(bytes),
            $"HELLO.TXT read back as '{Encoding.UTF8.GetString(bytes)}'.");
    }

    [SkippableFact]
    public void ReadingAPathThatIsNotThereIsRefusedByName()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Result<byte[]> read = fixture.Content.Read("NOT-THERE.TXT", 0, 16);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.UsageError, read.Error.Code);
        Assert.Contains("NOT-THERE.TXT", read.Error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ListingAFileIsRefused()
    {
        using ExFatFixture fixture = ExFatFixture.OpenPopulated();

        Result<IReadOnlyList<ProjectedItem>> listed = fixture.Content.List("HELLO.TXT");

        Assert.False(listed.Ok);
        Assert.Equal(DmgExitCode.UsageError, listed.Error.Code);
    }

    private static IReadOnlyList<ProjectedItem> Listed(IProjectedContent content, string path)
    {
        Result<IReadOnlyList<ProjectedItem>> listed = content.List(path);

        Assert.True(
            listed.TryGetValue(out IReadOnlyList<ProjectedItem>? items),
            listed.Ok ? "" : listed.Error.ToString());

        return items;
    }

    private static ProjectedItem Found(IProjectedContent content, string path)
    {
        Result<ProjectedItem?> found = content.Find(path);

        Assert.True(found.Ok, found.Ok ? "" : found.Error.ToString());
        Assert.True(found.Value is not null, $"'{path}' was not found in the volume.");

        return found.Value!;
    }
}

/// <summary>
/// Opens the populated exFAT fixture and keeps its handles alive together.
/// </summary>
/// <remarks>
/// Shared by the projection tests so the fixture-opening dance - decode the image,
/// find the exFAT volume among the GPT partitions, open a reader over it - is
/// written once. <see cref="Filesystems.ExFatReaderFixtureTests"/> still has its own
/// copy; migrating it here is a tidy-up, not a fix.
/// </remarks>
internal sealed class ExFatFixture : IDisposable
{
    /// <summary>A fixture whose exFAT volume carries the three known files.</summary>
    internal const string PopulatedFixture = "exfat-zlib.dmg";

    private readonly FileStream _file;
    private readonly DmgBlockStream _disk;

    private ExFatFixture(FileStream file, DmgBlockStream disk, ExFatReader reader)
    {
        _file = file;
        _disk = disk;
        Reader = reader;
        Content = new ExFatProjectedContent(reader);
    }

    /// <summary>The reader over the fixture's exFAT volume.</summary>
    internal ExFatReader Reader { get; }

    /// <summary>The same volume, addressed by path.</summary>
    internal ExFatProjectedContent Content { get; }

    /// <summary>Opens the populated fixture, or skips the calling test.</summary>
    internal static ExFatFixture OpenPopulated()
    {
        Skip.If(
            !FixtureCorpus.IsAvailable,
            $"no usable fixture corpus: {FixtureCorpus.UnavailableReason} {FixtureCorpus.Regenerate}");

        FixtureRecord? record = FixtureCorpus.Find(PopulatedFixture);

        Skip.If(record is null, $"'{PopulatedFixture}' is not in the corpus. {FixtureCorpus.Regenerate}");

        FileStream file = File.OpenRead(record!.Path);
        DmgBlockStream? disk = null;

        try
        {
            disk = DmgBlockStreamFixtureTests.Open(file);

            Result<VolumeMap> read = VolumeMap.Read(disk);

            Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

            DiskVolume? volume = map.Volumes.FirstOrDefault(
                candidate => candidate.Filesystem.Kind == FilesystemKind.ExFat);

            Assert.True(
                volume is not null,
                $"{PopulatedFixture} has no exFAT volume: "
                + string.Join(", ", map.Volumes.Select(v => v.Filesystem.Name)));

            Result<ExFatReader> opened = ExFatReader.Open(disk, volume!.ByteOffset, volume.ByteLength);

            Assert.True(
                opened.TryGetValue(out ExFatReader? reader),
                opened.Ok ? "" : opened.Error.ToString());

            return new ExFatFixture(file, disk, reader);
        }
        catch
        {
            disk?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _disk.Dispose();
        _file.Dispose();
    }
}
