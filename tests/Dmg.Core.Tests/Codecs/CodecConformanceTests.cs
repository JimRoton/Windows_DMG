using Dmg.Core.Codecs;
using Xunit.Abstractions;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// The codec conformance suite: every decoder measured against Apple's own output.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of a decoder in this repository checks it against bytes this
/// repository wrote. That proves the decoders are self-consistent and proves
/// nothing about whether they agree with <c>hdiutil</c>. This suite closes that
/// gap: <c>tools/make-fixtures.sh</c> asks hdiutil to build a corpus,
/// <c>tools/make-manifest.sh</c> asks hdiutil to decode each image and records the
/// SHA-256 of the sector stream it produced, and the tests below decode the same
/// images with <c>Dmg.Core</c> and compare. Apple is the oracle; we are the thing
/// under test.
/// </para>
/// <para>
/// <b>No hash is ever pinned here.</b> hdiutil bakes a fresh volume UUID and fresh
/// timestamps into every image, so the corpus and its hashes change on every
/// regeneration, on every machine. The fixtures and the manifest are therefore
/// generated together and the manifest from that run supplies the expectations -
/// this is a live differential test, not a recorded-value regression test. What is
/// asserted about hashes is only ever a relationship between them: equality with
/// what hdiutil said, and the grouping described in
/// <see cref="SevenFixturesDecodeToOneSharedStream"/>. See
/// <see cref="FixtureCorpus"/> for the drift check that keeps a stale manifest from
/// being believed.
/// </para>
/// <para>
/// <b>What is skipped, and why.</b> The encrypted fixtures cannot be decoded at all
/// until epic E4 builds the encrcdsa layer, so they are reported and stepped over
/// rather than failed. bzip2 is not skipped - it is asserted to be refused, by name.
/// And a machine with no corpus at all (a clean checkout; any CI runner that is not
/// a Mac) reports that and passes, because a missing fixture is not a defect in the
/// decoders.
/// </para>
/// </remarks>
public sealed class CodecConformanceTests
{
    private const string Bzip2Fixture = "bzip2.dmg";

    /// <summary>
    /// The seven fixtures hdiutil converts from one shared 12 MiB exFAT source
    /// image. Their decoded sector streams are identical by construction, whatever
    /// the codec or the encryption wrapper on the outside - which is what makes the
    /// grouping assertion below reproducible even though the hash itself is not.
    /// </summary>
    private static readonly string[] SharedSourceFixtures =
    [
        "adc.dmg",
        "bzip2.dmg",
        "exfat-enc128.dmg",
        "exfat-enc256.dmg",
        "exfat-raw.dmg",
        "exfat-udro.dmg",
        "exfat-zlib.dmg",
    ];

    /// <summary>
    /// The chunk types hdiutil actually writes into this corpus. Each one must be
    /// seen decoding a real Apple image, or the run did not verify what it claims.
    /// </summary>
    private static readonly uint[] CodecsThisCorpusCarries =
    [
        ChunkEntryTypeCodes.ZeroFill,
        ChunkEntryTypeCodes.Raw,
        ChunkEntryTypeCodes.Ignore,
        ChunkEntryTypeCodes.AppleAdc,
        ChunkEntryTypeCodes.Zlib,
    ];

    /// <summary>
    /// The fixture that carries genuine raw (<c>0x00000001</c>) chunks, and the one
    /// that carries genuine zero-fill (<c>0x00000000</c>) chunks. Named rather than
    /// discovered so that losing the recipe fails a test instead of silently
    /// shrinking what this suite proves.
    /// </summary>
    private const string RawChunkFixture = "exfat-udro.dmg";

    private const string ZeroFillFixture = "zerofill.dmg";

    private readonly ITestOutputHelper _output;

    public CodecConformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>One row per manifest record, or the no-corpus sentinel.</summary>
    public static TheoryData<string> Corpus => FixtureCorpus.Cases();

    /// <summary>
    /// Says out loud what the suite is working with, so a passing run cannot be
    /// mistaken for a thorough one when the corpus is absent or partial.
    /// </summary>
    [SkippableFact]
    public void TheCorpusIsReported()
    {
        Skip.If(!FixtureCorpus.IsAvailable, $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");

        _output.WriteLine($"Corpus: {FixtureCorpus.FixtureDirectory}");

        foreach (FixtureRecord record in FixtureCorpus.Records)
        {
            _output.WriteLine(
                $"  {record.Name,-20} {record.Format,-6} {record.Filesystem,-8} "
                + $"decoded {ConformanceFormat.Bytes(record.DecodedSize)}"
                + (record.Encrypted ? "  [encrypted]" : string.Empty));
        }

        foreach (string problem in FixtureCorpus.Unusable)
        {
            _output.WriteLine($"  UNUSABLE {problem}");
        }

        Assert.All(FixtureCorpus.Records, record => Assert.True(
            File.Exists(record.Path),
            $"{record.Name} passed the drift check but is not on disk."));
    }

    /// <summary>
    /// The whole point of the story. For every fixture in the corpus, decode it with
    /// our codecs and compare the sector stream with the one hdiutil produced.
    /// </summary>
    /// <remarks>
    /// An image whose chunk table contains a codec this build does not implement is
    /// routed to the refusal assertion instead: the contract for those is that the
    /// decode fails cleanly and names the codec, not that it produces bytes. The
    /// routing reads the chunk table rather than the fixture's name, so a corpus
    /// that grows a new unsupported-codec fixture is handled without editing this.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(Corpus))]
    public void DecodesToApplesOwnOutput(string fixtureName)
    {
        FixtureRecord record = GetRunnable(fixtureName);

        if (IsFlat(record))
        {
            AssertFlatImage(record);
            return;
        }

        Result<IReadOnlyList<uint>> survey = UdifImageDecoder.SurveyEntryTypes(record.Path);

        if (!survey.TryGetValue(out IReadOnlyList<uint>? entryTypes))
        {
            Assert.Fail($"{record.Name}: could not read the chunk table: {survey.Error}");
            return;
        }

        IReadOnlyList<ChunkCodecInfo> codecs = ChunkDecoderRegistry.Default.Survey(entryTypes);

        _output.WriteLine($"{record.Name}: chunk codecs {string.Join(", ", codecs)}");

        if (codecs.Any(codec => codec.IsUnsupportedPayload))
        {
            AssertRefusedByName(record, codecs);
            return;
        }

        Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path, record.DecodedSize);

        if (!decoded.TryGetValue(out DecodedImage? image))
        {
            Assert.Fail($"{record.Name}: decode failed: {decoded.Error}");
            return;
        }

        _output.WriteLine(
            $"{record.Name}: decoded {ConformanceFormat.Bytes(image.Length)}, "
            + $"ground truth from '{record.DecodedHashMethod}'");

        AssertGroundTruthLength(record, image);

        Assert.True(
            string.Equals(record.DecodedSha256, image.Sha256, StringComparison.OrdinalIgnoreCase),
            $"{record.Name}: our decoded sector stream does not match hdiutil's. "
            + $"hdiutil {record.DecodedSha256}, ours {image.Sha256}. "
            + $"Codecs in this image: {string.Join(", ", codecs)}.");
    }

    /// <summary>
    /// bzip2 is refused, not decoded - and the refusal says "bzip2" so the user
    /// learns why their image will not open instead of reading a hex number.
    /// </summary>
    [SkippableFact]
    public void Bzip2IsRefusedAndNamed()
    {
        FixtureRecord record = GetRunnable(Bzip2Fixture);

        Result<IReadOnlyList<uint>> survey = UdifImageDecoder.SurveyEntryTypes(record.Path);

        if (!survey.TryGetValue(out IReadOnlyList<uint>? entryTypes))
        {
            Assert.Fail($"{record.Name}: could not read the chunk table: {survey.Error}");
            return;
        }

        // Describing an image we cannot open must work without touching the data
        // fork: this is exactly what `dmg info` will do with a UDBZ image.
        Assert.Contains(ChunkEntryTypeCodes.Bzip2, entryTypes);

        Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

        if (decoded.Ok)
        {
            Assert.Fail($"{record.Name} decoded, but this build has no bzip2 decoder.");
            return;
        }

        Assert.Equal(DmgExitCode.UnsupportedFormat, decoded.Error.Code);
        Assert.Contains("bzip2", decoded.Error.Message, StringComparison.OrdinalIgnoreCase);

        _output.WriteLine($"{record.Name}: correctly refused - {decoded.Error.Message}");
    }

    /// <summary>
    /// The encrypted fixtures are out of scope until epic E4 implements the encrcdsa
    /// wrapper. They are named here rather than quietly dropped, so the gap is
    /// visible in the test output and this test starts failing usefully the day the
    /// wrapper lands.
    /// </summary>
    [SkippableFact]
    public void EncryptedFixturesAreSkippedUntilTheEncryptionEpic()
    {
        Skip.If(!FixtureCorpus.IsAvailable, $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");

        FixtureRecord[] encrypted = [.. FixtureCorpus.Records.Where(record => record.Encrypted)];

        Assert.NotEmpty(encrypted);

        foreach (FixtureRecord record in encrypted)
        {
            Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

            if (decoded.Ok)
            {
                Assert.Fail(
                    $"{record.Name} decoded without a decryption layer, which cannot be right - "
                    + "an encrcdsa image is not a UDIF container until it has been unwrapped.");
                return;
            }

            _output.WriteLine(
                $"SKIPPED {record.Name}: encrypted ({record.Format}); decoding needs the encrcdsa "
                + $"layer from epic E4, which is not built. Our reader says: {decoded.Error.Message}");
        }
    }

    /// <summary>
    /// The one thing about these hashes that is reproducible across regenerations:
    /// seven fixtures come from a single source image, so their decoded sector
    /// streams are byte-identical to each other - whatever the codec, and whether or
    /// not they are encrypted. The value changes every time the corpus is rebuilt;
    /// the grouping does not, which is why the grouping is what gets asserted.
    /// </summary>
    [SkippableFact]
    public void SevenFixturesDecodeToOneSharedStream()
    {
        Skip.If(!FixtureCorpus.IsAvailable, $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");

        string[] missing =
        [
            .. SharedSourceFixtures.Where(name => FixtureCorpus.Find(name) is null),
        ];

        Skip.If(
            missing.Length > 0,
            $"the shared-source group is incomplete: {string.Join(", ", missing)} absent. "
            + FixtureCorpus.Regenerate);

        List<IGrouping<string, FixtureRecord>> shared =
        [
            .. FixtureCorpus.Records
                .GroupBy(record => record.DecodedSha256, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1),
        ];

        Assert.Single(shared);

        string[] members = [.. shared[0].Select(record => record.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(SharedSourceFixtures, members);

        _output.WriteLine(
            $"One shared decoded stream, {ConformanceFormat.Bytes(shared[0].First().DecodedSize)}, "
            + $"across: {string.Join(", ", members)}");

        // And the part of that group we can actually read really does come out
        // identical by four different routes: the flat UDRW image read straight off
        // disk, the same disk through zlib, the same disk through ADC, and the same
        // disk through raw UDIF chunks. Same sectors, three codecs plus the flat
        // control, one hash - which is what isolates the codec from everything else
        // in the pipeline.
        List<string> ours = [];

        foreach (FixtureRecord record in shared[0].Where(record => !record.Encrypted))
        {
            if (IsFlat(record))
            {
                ours.Add(FixtureCorpus.HashFile(record.Path));
                _output.WriteLine($"  {record.Name}: flat {record.Format} image, hashed as-is");
                continue;
            }

            Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

            if (!decoded.TryGetValue(out DecodedImage? image))
            {
                // bzip2 lives in this group and is refused by design.
                _output.WriteLine($"  {record.Name}: refused ({decoded.Error.Message})");
                continue;
            }

            ours.Add(image.Sha256);
            _output.WriteLine($"  {record.Name}: decoded by us, {record.Format}");
        }

        Assert.Equal(4, ours.Count);
        Assert.Single(ours.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            shared[0].First().DecodedSha256,
            ours[0],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guards against a green suite that proved nothing: the decoders this corpus
    /// can reach must actually have been used on a real Apple image, and any
    /// decoder the corpus cannot reach must be one of the two we already know it
    /// cannot reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test used to carry an asserted gap: raw and zero-fill were declared
    /// unreachable, because every <c>convert</c> recipe hands data chunks to a codec
    /// and marks free space <c>ignore</c>, and because UDRW - the format whose name
    /// says "raw" - is not a UDIF container at all but a flat sector image with no
    /// chunk table. S3.9 closed the gap with two recipes rather than one:
    /// <c>convert -format UDRO</c> for raw chunks, and
    /// <c>create -srcfolder … -format UDZO</c> for zero-fill, which is the only
    /// hdiutil path found that emits type <c>0x00000000</c> at all.
    /// </para>
    /// <para>
    /// So there is no gap left to assert. Every decoder this build registers must
    /// now be exercised against a real Apple image, and a decoder that stops being
    /// covered fails this test instead of being quietly excused by a list.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void EveryDecoderTheCorpusCanReachIsExercised()
    {
        Skip.If(!FixtureCorpus.IsAvailable, $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");

        // Coverage is a statement about the whole corpus, so a partial one cannot
        // support it either way: the missing fixture may well be the one that
        // carried the codec.
        Skip.If(
            FixtureCorpus.Unusable.Count > 0,
            "the corpus is incomplete, so codec coverage cannot be judged: "
            + string.Join(" ", FixtureCorpus.Unusable));

        Dictionary<uint, List<string>> coverage = [];

        foreach (FixtureRecord record in FixtureCorpus.Records.Where(record => !record.Encrypted))
        {
            Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

            if (!decoded.Ok)
            {
                continue;
            }

            foreach (KeyValuePair<uint, long> pair in decoded.Value!.SectorsByEntryType)
            {
                if (!coverage.TryGetValue(pair.Key, out List<string>? images))
                {
                    images = [];
                    coverage[pair.Key] = images;
                }

                images.Add($"{record.Name} ({pair.Value} sectors)");
            }
        }

        foreach (uint entryType in coverage.Keys.Order())
        {
            _output.WriteLine(
                $"{ChunkEntryTypeCodes.NameOf(entryType),-10} <- {string.Join(", ", coverage[entryType])}");
        }

        foreach (uint entryType in CodecsThisCorpusCarries)
        {
            Assert.True(
                coverage.ContainsKey(entryType),
                $"No fixture in the corpus exercised the {ChunkEntryTypeCodes.NameOf(entryType)} decoder "
                + "against hdiutil's output, so this run did not actually verify it.");
        }

        uint[] uncovered =
        [
            .. ChunkDecoderRegistry.Default.SupportedEntryTypes
                .Where(entryType => !coverage.ContainsKey(entryType))
                .Order(),
        ];

        Assert.True(
            uncovered.Length == 0,
            "Every decoder this build registers is supposed to be covered by a real hdiutil "
            + "image, and these are not: "
            + string.Join(", ", uncovered.Select(ChunkEntryTypeCodes.NameOf))
            + ". " + FixtureCorpus.Regenerate);
    }

    /// <summary>
    /// Raw and zero-fill specifically, against Apple's ground truth - the two
    /// decoders that had no hdiutil coverage at all before S3.9.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic coverage test above proves a chunk of each type was seen. This
    /// one proves the bytes those decoders produced are the bytes hdiutil produced,
    /// which is a different and stronger claim: a raw decoder that returned the
    /// wrong slice of the data fork, or a zero-fill decoder that emitted the right
    /// number of the wrong sectors, would satisfy coverage and fail here.
    /// </para>
    /// <para>
    /// Neither fixture's hash is pinned. <c>exfat-udro.dmg</c> is compared against
    /// its own manifest record and, separately, against the six other fixtures cut
    /// from the same source image; <c>zerofill.dmg</c> is compared against its own
    /// manifest record. Both records are written by the same generation run that
    /// wrote the images.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void RawAndZeroFillAreVerifiedAgainstAppleGroundTruth()
    {
        Skip.If(!FixtureCorpus.IsAvailable, $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");

        AssertCarriesAndMatches(RawChunkFixture, ChunkEntryTypeCodes.Raw);
        AssertCarriesAndMatches(ZeroFillFixture, ChunkEntryTypeCodes.ZeroFill);
    }

    /// <summary>
    /// Asserts that <paramref name="fixtureName"/> really contains chunks of
    /// <paramref name="entryType"/>, and that decoding it reproduces hdiutil's
    /// sector stream exactly.
    /// </summary>
    private void AssertCarriesAndMatches(string fixtureName, uint entryType)
    {
        string codec = ChunkEntryTypeCodes.NameOf(entryType);
        FixtureRecord? record = FixtureCorpus.Find(fixtureName);

        Assert.True(
            record is not null,
            $"{fixtureName} is the corpus's only source of {codec} chunks and it is not in "
            + $"this run's manifest, so the {codec} decoder has no Apple ground truth at all. "
            + FixtureCorpus.Regenerate);

        Result<IReadOnlyList<uint>> survey = UdifImageDecoder.SurveyEntryTypes(record!.Path);

        if (!survey.TryGetValue(out IReadOnlyList<uint>? entryTypes))
        {
            Assert.Fail($"{fixtureName}: could not read the chunk table: {survey.Error}");
            return;
        }

        Assert.True(
            entryTypes.Contains(entryType),
            $"{fixtureName} was added to carry {codec} chunks and its chunk table has none. "
            + $"It holds: {string.Join(", ", entryTypes.Distinct().Order().Select(ChunkEntryTypeCodes.NameOf))}. "
            + "The hdiutil recipe has changed behaviour; see the notes in tools/make-fixtures.sh.");

        Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

        if (!decoded.TryGetValue(out DecodedImage? image))
        {
            Assert.Fail($"{fixtureName}: we could not decode it: {decoded.Error}");
            return;
        }

        long sectors = image.SectorsByEntryType.TryGetValue(entryType, out long count) ? count : 0;

        Assert.True(
            sectors > 0,
            $"{fixtureName}: the chunk table declares {codec} chunks but decoding it produced "
            + $"no {codec} sectors, so the decoder was never actually called.");

        AssertGroundTruthLength(record, image);

        Assert.Equal(record.DecodedSha256, image.Sha256, StringComparer.OrdinalIgnoreCase);

        _output.WriteLine(
            $"{fixtureName}: {sectors:N0} {codec} sector(s) among "
            + string.Join(", ", image.SectorsByEntryType.OrderBy(pair => pair.Key)
                .Select(pair => $"{ChunkEntryTypeCodes.NameOf(pair.Key)}={pair.Value:N0}"))
            + $"; {ConformanceFormat.Bytes(image.HashedLength)} decoded, and the SHA-256 is "
            + "hdiutil's.");
    }

    /// <summary>
    /// Reports and steps over the rows a theory cannot run: the no-corpus sentinel,
    /// and the encrypted fixtures. Throws <see cref="SkipException"/> - reported by
    /// the test runner as skipped, not passed - for every row that verifies
    /// nothing; returns the runnable record otherwise.
    /// </summary>
    private static FixtureRecord GetRunnable(string fixtureName)
    {
        if (string.Equals(fixtureName, FixtureCorpus.NoCorpusCase, StringComparison.Ordinal)
            || !FixtureCorpus.IsAvailable)
        {
            throw new SkipException(
                $"no usable fixture corpus: {FixtureCorpus.UnavailableReason}");
        }

        FixtureRecord? found = FixtureCorpus.Find(fixtureName);

        if (found is null)
        {
            throw new SkipException($"{fixtureName}: not in this run's manifest.");
        }

        if (found.Encrypted)
        {
            throw new SkipException(
                $"{fixtureName}: encrypted. Decoding it needs the encrcdsa layer from epic E4, "
                + "which is not built. See EncryptedFixturesAreSkippedUntilTheEncryptionEpic.");
        }

        return found;
    }

    private void AssertRefusedByName(FixtureRecord record, IReadOnlyList<ChunkCodecInfo> codecs)
    {
        ChunkCodecInfo unsupported = codecs.First(codec => codec.IsUnsupportedPayload);

        Result<DecodedImage> decoded = UdifImageDecoder.Decode(record.Path);

        if (decoded.Ok)
        {
            Assert.Fail(
                $"{record.Name} uses {unsupported.Name}, which this build has no decoder for, "
                + "yet it decoded.");
            return;
        }

        Assert.Equal(DmgExitCode.UnsupportedFormat, decoded.Error.Code);
        Assert.Contains(unsupported.Name, decoded.Error.Message, StringComparison.OrdinalIgnoreCase);

        _output.WriteLine($"{record.Name}: correctly refused - {decoded.Error.Message}");
    }

    /// <summary>
    /// Reconciles the length of our sector stream with the length of hdiutil's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They are usually identical. They are not for <c>multipart.dmg</c>, and the
    /// reason is worth writing down because it looks like a bug and is not.
    /// <c>hdiutil convert -format UDTO</c> - the recipe the manifest uses for every
    /// unencrypted fixture - writes a CD/DVD master and stops at the last non-zero
    /// sector, so an image with trailing free space yields a file shorter than the
    /// disk. Measured on this corpus: the .cdr is 40 sectors short, and attaching
    /// the same image and reading <c>/dev/rdiskN</c> gives back the full
    /// 163,840-sector disk whose first 163,800 sectors are byte-identical to the
    /// .cdr and whose last 40 are zeros.
    /// </para>
    /// <para>
    /// Our stream is the whole disk, because the koly trailer says the disk is that
    /// long and the blkx regions tile all of it. So the comparison is: the prefix
    /// hdiutil did write must match ours byte for byte, and everything past it must
    /// be zeros. Truncating our own output to make the numbers agree would throw
    /// away the check that the tail is zeros, which is the only part of this that
    /// could ever catch a real defect.
    /// </para>
    /// </remarks>
    private void AssertGroundTruthLength(FixtureRecord record, DecodedImage image)
    {
        Assert.Equal(record.DecodedSize, image.HashedLength);

        if (image.Length == record.DecodedSize)
        {
            return;
        }

        Assert.True(
            image.Length > record.DecodedSize,
            $"{record.Name}: we decoded {ConformanceFormat.Bytes(image.Length)} but hdiutil "
            + $"produced {ConformanceFormat.Bytes(record.DecodedSize)}. A stream shorter than "
            + "Apple's is missing sectors, not trailing free space.");

        Assert.True(
            image.TailIsAllZero,
            $"{record.Name}: we decoded {ConformanceFormat.Bytes(image.Length)} against hdiutil's "
            + $"{ConformanceFormat.Bytes(record.DecodedSize)}, and the extra tail is not all zeros. "
            + "That is a real disagreement, not the UDTO master's trailing-zero truncation.");

        _output.WriteLine(
            $"{record.Name}: hdiutil's UDTO master stops "
            + $"{(image.Length - record.DecodedSize) / 512} sector(s) early; the disk really is "
            + $"{ConformanceFormat.Bytes(image.Length)} and the tail we decoded past it is all zeros.");
    }

    /// <summary>
    /// True when the fixture is a flat sector image rather than a UDIF container.
    /// </summary>
    /// <remarks>
    /// hdiutil's UDRW output is the disk itself with nothing wrapped round it: no
    /// koly trailer, no property list, no chunk table. The manifest shows it as an
    /// image whose container and decoded stream are the same length, which is the
    /// property tested here - a compressed UDIF is smaller than its disk and an
    /// uncompressed one is larger, so equality means flat.
    /// </remarks>
    private static bool IsFlat(FixtureRecord record) =>
        record.ImageSize == record.DecodedSize;

    /// <summary>
    /// A flat image has no codec to verify, so what is asserted is the thing that
    /// makes it flat: our reader finds no UDIF container in it, and the file itself
    /// already is the sector stream hdiutil decoded.
    /// </summary>
    private void AssertFlatImage(FixtureRecord record)
    {
        Result<IReadOnlyList<uint>> survey = UdifImageDecoder.SurveyEntryTypes(record.Path);

        if (survey.Ok)
        {
            Assert.Fail(
                $"{record.Name} is the same size decoded as on disk, yet a UDIF container was "
                + "found in it. The corpus has changed shape; this test's assumption needs revisiting.");
            return;
        }

        Assert.Equal(DmgExitCode.UnsupportedFormat, survey.Error.Code);

        Assert.Equal(
            record.DecodedSha256,
            FixtureCorpus.HashFile(record.Path),
            StringComparer.OrdinalIgnoreCase);

        _output.WriteLine(
            $"{record.Name}: flat {record.Format} image - no UDIF container, so no chunk codec to "
            + $"verify. It is its own decoded sector stream, {ConformanceFormat.Bytes(record.DecodedSize)}, "
            + $"and it matches hdiutil's. Our reader says: {survey.Error.Message}");
    }
}
