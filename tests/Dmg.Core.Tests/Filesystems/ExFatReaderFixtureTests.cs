using System.Buffers.Binary;
using System.Text;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Filesystems;

/// <summary>
/// The exFAT reader measured against volumes <c>hdiutil</c> actually wrote, with
/// files whose bytes <c>tools/make-fixtures.sh</c> chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the fixtures and not a synthetic volume.</b> <see cref="VolumeMapTests"/>
/// builds a minimal exFAT boot sector in memory, which is the right tool for
/// probing but has no directories and no files in it. Everything this reader does
/// beyond the boot sector - assembling a name out of <c>0xC1</c> fragments,
/// following a cluster chain, honouring <c>NoFatChain</c>, reading a range that
/// starts part-way through a cluster - only exists on a real volume. So these read
/// the generated corpus.
/// </para>
/// <para>
/// <b>The expected bytes come from the generator, not from this reader.</b>
/// <c>make-fixtures.sh</c> writes <c>HELLO.TXT</c>, <c>README.TXT</c> and
/// <c>DATA.BIN</c> with fixed content; the assertions below restate that content
/// independently. If the reader and the generator ever disagree, the reader is
/// wrong - which is the only arrangement worth testing.
/// </para>
/// <para>
/// <b>Absence is a skip.</b> <c>fixtures/generated/</c> is gitignored and only a
/// Mac can fill it, so a machine without the corpus reports skipped rather than
/// green.
/// </para>
/// </remarks>
public sealed class ExFatReaderFixtureTests
{
    /// <summary>A fixture whose exFAT volume carries all three known files.</summary>
    private const string PopulatedFixture = "exfat-zlib.dmg";

    /// <summary><c>HELLO.TXT</c> as the generator writes it: 20 bytes.</summary>
    private static ReadOnlySpan<byte> Hello => "Hello, DMG fixture!\n"u8;

    /// <summary><c>README.TXT</c> as the generator writes it: 35 bytes.</summary>
    private static ReadOnlySpan<byte> Readme => "Windows_DMG test corpus\nfixture v1\n"u8;

    /// <summary>How many 4-byte counter values <c>DATA.BIN</c> holds.</summary>
    private const int DataBinCounters = 16384;

    [SkippableFact]
    public void TheRootDirectoryListsTheFilesTheGeneratorPutThere()
    {
        using FixtureVolume volume = OpenPopulated();

        IReadOnlyList<ExFatEntry> entries = Listed(volume.Reader);

        string[] names = [.. entries.Select(entry => entry.Name).Order(StringComparer.Ordinal)];

        Assert.Contains("HELLO.TXT", names);
        Assert.Contains("README.TXT", names);
        Assert.Contains("DATA.BIN", names);

        // Names come out of 0xC1 fragments fifteen characters at a time. A name
        // assembled one fragment short, or one character long, would still "contain"
        // the right letters somewhere - so the lengths are asserted exactly.
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));
        Assert.Equal("DATA.BIN".Length, Find(entries, "DATA.BIN").Name.Length);
    }

    [SkippableFact]
    public void TheKnownFilesReportTheLengthsTheGeneratorWrote()
    {
        using FixtureVolume volume = OpenPopulated();

        IReadOnlyList<ExFatEntry> entries = Listed(volume.Reader);

        Assert.Equal(Hello.Length, Find(entries, "HELLO.TXT").Length);
        Assert.Equal(Readme.Length, Find(entries, "README.TXT").Length);
        Assert.Equal(DataBinCounters * 4, Find(entries, "DATA.BIN").Length);

        Assert.False(Find(entries, "HELLO.TXT").IsDirectory);
    }

    [SkippableFact]
    public void ASmallFileReadsBackByteForByte()
    {
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry hello = Find(Listed(volume.Reader), "HELLO.TXT");

        byte[] content = Read(volume.Reader, hello, 0, (int)hello.Length);

        Assert.True(
            Hello.SequenceEqual(content),
            $"HELLO.TXT read back as '{Encoding.UTF8.GetString(content)}'.");
    }

    [SkippableFact]
    public void AMultiLineFileReadsBackByteForByte()
    {
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry readme = Find(Listed(volume.Reader), "README.TXT");

        byte[] content = Read(volume.Reader, readme, 0, (int)readme.Length);

        Assert.True(
            Readme.SequenceEqual(content),
            $"README.TXT read back as '{Encoding.UTF8.GetString(content)}'.");
    }

    [SkippableFact]
    public void TheCounterFileMatchesTheGeneratorsPattern()
    {
        // 64 KiB spans several clusters on any sane exFAT layout, so this is the
        // test that actually exercises walking from one cluster to the next.
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry data = Find(Listed(volume.Reader), "DATA.BIN");

        byte[] content = Read(volume.Reader, data, 0, (int)data.Length);

        Assert.Equal(DataBinCounters * 4, content.Length);

        for (int index = 0; index < DataBinCounters; index++)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(index * 4, 4));

            if (value != (uint)index)
            {
                Assert.Fail(
                    $"DATA.BIN counter {index} read back as {value}. The first wrong value is "
                    + $"at byte {index * 4}, which is where the cluster walk went astray.");
            }
        }
    }

    [SkippableFact]
    public void ARangedReadMatchesTheSameBytesOfTheWholeFile()
    {
        // The case a whole-file read cannot catch: a read that starts part-way into
        // a cluster and ends part-way into a later one. Getting the within-cluster
        // arithmetic wrong shifts the bytes without changing how many come back.
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry data = Find(Listed(volume.Reader), "DATA.BIN");

        byte[] whole = Read(volume.Reader, data, 0, (int)data.Length);

        foreach ((long offset, int count) in new[] { (1L, 7), (511L, 1026), (4095L, 4098), (65000L, 536) })
        {
            byte[] slice = Read(volume.Reader, data, offset, count);

            Assert.True(
                whole.AsSpan((int)offset, slice.Length).SequenceEqual(slice),
                $"A {count}-byte read at {offset} differs from the same range of the whole file.");
        }
    }

    [SkippableFact]
    public void AReadPastTheEndOfAFileComesBackEmptyRatherThanFailing()
    {
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry hello = Find(Listed(volume.Reader), "HELLO.TXT");

        Assert.Empty(Read(volume.Reader, hello, hello.Length, 16));

        // And a read that starts inside the file but asks for more than is left is
        // clipped, not refused - and what comes back is the file's last byte, not
        // merely the right number of bytes.
        byte[] tail = Read(volume.Reader, hello, hello.Length - 1, 4096);

        Assert.Single(tail);
        Assert.Equal(Hello[^1], tail[0]);
    }

    [SkippableFact]
    public void ReadingADirectoryAsAFileIsRefused()
    {
        using FixtureVolume volume = OpenPopulated();

        ExFatEntry directory = new(
            "SOMEWHERE",
            IsDirectory: true,
            Length: 4096,
            FirstCluster: 2,
            IsContiguous: false,
            Created: null,
            Modified: null,
            IsReadOnly: false,
            IsHidden: false);

        Result<byte[]> read = volume.Reader.ReadFile(directory, 0, 16);

        Assert.False(read.Ok);
        Assert.Equal(DmgExitCode.UsageError, read.Error.Code);
    }

    private static IReadOnlyList<ExFatEntry> Listed(ExFatReader reader)
    {
        Result<IReadOnlyList<ExFatEntry>> listed = reader.ReadRootDirectory();

        Assert.True(
            listed.TryGetValue(out IReadOnlyList<ExFatEntry>? entries),
            listed.Ok ? "" : listed.Error.ToString());

        return entries;
    }

    private static ExFatEntry Find(IReadOnlyList<ExFatEntry> entries, string name)
    {
        ExFatEntry? found = entries.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            found is not null,
            $"'{name}' is not in the root directory. Found: {string.Join(", ", entries.Select(e => e.Name))}");

        return found!;
    }

    private static byte[] Read(ExFatReader reader, ExFatEntry entry, long offset, int count)
    {
        Result<byte[]> read = reader.ReadFile(entry, offset, count);

        Assert.True(read.TryGetValue(out byte[]? bytes), read.Ok ? "" : read.Error.ToString());

        return bytes;
    }

    /// <summary>Opens the populated fixture's exFAT volume, or skips.</summary>
    private static FixtureVolume OpenPopulated()
    {
        Skip.If(
            !FixtureCorpus.IsAvailable,
            $"no usable fixture corpus: {FixtureCorpus.UnavailableReason} {FixtureCorpus.Regenerate}");

        FixtureRecord? record = FixtureCorpus.Find(PopulatedFixture);

        Skip.If(record is null, $"'{PopulatedFixture}' is not in the corpus. {FixtureCorpus.Regenerate}");

        FileStream file = File.OpenRead(record!.Path);
        DmgBlockStream disk;

        try
        {
            disk = DmgBlockStreamFixtureTests.Open(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        try
        {
            Result<VolumeMap> read = VolumeMap.Read(disk);

            Assert.True(read.TryGetValue(out VolumeMap? map), read.Ok ? "" : read.Error.ToString());

            // Chosen by filesystem rather than by Select(null): these images carry a
            // GPT with an EFI partition beside the exFAT one, and which of them a
            // bare Select picks is a question about selection, not about this reader.
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

            return new FixtureVolume(file, disk, reader);
        }
        catch
        {
            disk.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>The open handles behind one fixture volume, disposed together.</summary>
    private sealed class FixtureVolume(FileStream file, DmgBlockStream disk, ExFatReader reader) : IDisposable
    {
        internal ExFatReader Reader { get; } = reader;

        public void Dispose()
        {
            disk.Dispose();
            file.Dispose();
        }
    }
}
