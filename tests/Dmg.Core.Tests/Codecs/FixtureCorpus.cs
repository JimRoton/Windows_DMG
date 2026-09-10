using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// One record of <c>fixtures/manifest.json</c>: an image on disk and the SHA-256
/// of its fully decoded sector stream <b>as Apple's own decoder produced it</b>.
/// </summary>
/// <param name="Name">The file name, e.g. <c>exfat-zlib.dmg</c>.</param>
/// <param name="Path">The absolute path to the image, which may not exist.</param>
/// <param name="Format">hdiutil's format string - UDZO, UDRW, UDBZ, UDCO.</param>
/// <param name="Filesystem">The filesystem inside, for the test's own reporting.</param>
/// <param name="Encrypted">True for the encrcdsa-wrapped fixtures.</param>
/// <param name="ImageSize">Size of the .dmg container in bytes.</param>
/// <param name="ImageSha256">SHA-256 of the .dmg container, used to detect drift.</param>
/// <param name="DecodedSize">Length of the decoded sector stream in bytes.</param>
/// <param name="DecodedSha256">
/// Ground truth: SHA-256 of the decoded sector stream, from hdiutil. Never pinned
/// as a constant anywhere - see <see cref="FixtureCorpus"/>.
/// </param>
/// <param name="DecodedHashMethod">Which hdiutil recipe produced that hash.</param>
internal sealed record FixtureRecord(
    string Name,
    string Path,
    string Format,
    string Filesystem,
    bool Encrypted,
    long ImageSize,
    string ImageSha256,
    long DecodedSize,
    string DecodedSha256,
    string DecodedHashMethod);

/// <summary>
/// Loads the generated DMG corpus and the ground-truth manifest that goes with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why nothing here is a committed constant.</b> hdiutil bakes a fresh volume
/// UUID and fresh timestamps into every image it creates, so re-running
/// <c>tools/make-fixtures.sh</c> produces a corpus whose decoded sector streams
/// hash differently - every single time, on every machine. A golden hash checked
/// into this file would be wrong the first time anyone regenerated the fixtures,
/// and the natural "fix" - regenerating the constant from our own decoder - would
/// make the whole suite circular and worthless.
/// </para>
/// <para>
/// So the corpus and the manifest are generated together, by
/// <c>tools/make-fixtures.sh</c> followed by <c>tools/make-manifest.sh</c>, and the
/// conformance tests read that run's manifest as their expected values. The suite
/// is a live differential test against Apple's decoder, not a regression test
/// against a recorded number.
/// </para>
/// <para>
/// <b>Drift is detected, not tolerated.</b> Every record carries the SHA-256 of the
/// .dmg container itself. If the image on disk does not match it, the manifest was
/// written for a different corpus and its decoded hashes mean nothing here; that
/// fixture is reported as unusable with instructions rather than being compared
/// against a stale expectation.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> <c>fixtures/generated/</c> is gitignored and
/// hdiutil is macOS-only, so a clean checkout - and every CI runner that is not a
/// Mac - simply has no corpus. The tests report that and pass rather than going red
/// over something the machine could never have had.
/// </para>
/// </remarks>
internal static class FixtureCorpus
{
    /// <summary>The row a theory yields when there is no corpus to enumerate.</summary>
    internal const string NoCorpusCase = "(no fixture corpus)";

    private static readonly Lazy<FixtureCorpusState> LazyState = new(Load, isThreadSafe: true);

    /// <summary>True when a usable corpus and manifest were both found.</summary>
    internal static bool IsAvailable => LazyState.Value.Records.Count > 0;

    /// <summary>Why the corpus is unusable, or null when it is fine.</summary>
    internal static string? UnavailableReason => LazyState.Value.UnavailableReason;

    /// <summary>Every manifest record whose image is present and in step with it.</summary>
    internal static IReadOnlyList<FixtureRecord> Records => LazyState.Value.Records;

    /// <summary>
    /// Records whose image is missing or has drifted away from the manifest, with
    /// the reason. Reported by the tests rather than silently ignored.
    /// </summary>
    internal static IReadOnlyList<string> Unusable => LazyState.Value.Unusable;

    /// <summary>The path the corpus was looked for in, for error messages.</summary>
    internal static string FixtureDirectory => LazyState.Value.FixtureDirectory;

    /// <summary>The one instruction that fixes every "no corpus" condition.</summary>
    internal static string Regenerate =>
        "Run tools/make-fixtures.sh and then tools/make-manifest.sh from the repository root "
        + "(macOS only - they drive hdiutil). Both must be run in the same session: the manifest "
        + "describes the corpus currently on this machine, not a universal golden value.";

    /// <summary>Looks a record up by file name.</summary>
    internal static FixtureRecord? Find(string name) =>
        Records.FirstOrDefault(record =>
            string.Equals(record.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The theory rows for the whole corpus: one per record, or the
    /// <see cref="NoCorpusCase"/> sentinel when there is nothing to enumerate.
    /// </summary>
    /// <remarks>
    /// xunit v2 has no dynamic skip and fails a theory that yields no rows at all,
    /// so absence is carried as a row the test body recognises and reports.
    /// </remarks>
    internal static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];

        if (!IsAvailable)
        {
            data.Add(NoCorpusCase);
            return data;
        }

        foreach (FixtureRecord record in Records)
        {
            data.Add(record.Name);
        }

        return data;
    }

    /// <summary>SHA-256 of a file, lower-case hex - the same shape shasum prints.</summary>
    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static FixtureCorpusState Load()
    {
        string? root = FindRepositoryRoot();

        if (root is null)
        {
            return FixtureCorpusState.Unavailable(
                "unknown",
                $"Could not locate Windows_DMG.sln above '{AppContext.BaseDirectory}'.");
        }

        string manifestPath = System.IO.Path.Combine(root, "fixtures", "manifest.json");
        string generated = System.IO.Path.Combine(root, "fixtures", "generated");

        if (!File.Exists(manifestPath))
        {
            return FixtureCorpusState.Unavailable(
                generated,
                $"There is no fixtures/manifest.json. {Regenerate}");
        }

        if (!Directory.Exists(generated))
        {
            return FixtureCorpusState.Unavailable(
                generated,
                $"There is no fixtures/generated/ directory. {Regenerate}");
        }

        List<FixtureRecord> records = [];
        List<string> unusable = [];

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
        {
            if (!document.RootElement.TryGetProperty("fixtures", out JsonElement fixtures)
                || fixtures.ValueKind != JsonValueKind.Array)
            {
                return FixtureCorpusState.Unavailable(
                    generated,
                    $"fixtures/manifest.json carries no fixtures array. {Regenerate}");
            }

            foreach (JsonElement element in fixtures.EnumerateArray())
            {
                string name = Text(element, "name");
                string path = System.IO.Path.Combine(generated, name);

                FixtureRecord record = new(
                    name,
                    path,
                    Text(element, "format"),
                    Text(element, "filesystem"),
                    element.TryGetProperty("encrypted", out JsonElement encrypted)
                        && encrypted.ValueKind == JsonValueKind.True,
                    Number(element, "image_size"),
                    Text(element, "image_sha256"),
                    Number(element, "decoded_size"),
                    Text(element, "decoded_sha256"),
                    Text(element, "decoded_hash_method"));

                if (!File.Exists(path))
                {
                    unusable.Add($"{name}: listed in the manifest but not present in fixtures/generated/.");
                    continue;
                }

                if (new FileInfo(path).Length != record.ImageSize
                    || !string.Equals(HashFile(path), record.ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    unusable.Add(
                        $"{name}: the image on disk does not match the manifest's image_sha256, so the "
                        + $"manifest was written for a different corpus. {Regenerate}");
                    continue;
                }

                records.Add(record);
            }
        }

        return records.Count == 0
            ? FixtureCorpusState.Unavailable(
                generated,
                $"No manifest record has a matching image in fixtures/generated/. {Regenerate}")
            : new FixtureCorpusState(generated, null, records, unusable);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : -1;

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Windows_DMG.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed record FixtureCorpusState(
        string FixtureDirectory,
        string? UnavailableReason,
        IReadOnlyList<FixtureRecord> Records,
        IReadOnlyList<string> Unusable)
    {
        internal static FixtureCorpusState Unavailable(string directory, string reason) =>
            new(directory, reason, [], []);
    }
}

/// <summary>
/// Formatting helpers shared by the conformance tests' reporting.
/// </summary>
internal static class ConformanceFormat
{
    /// <summary>A byte count with thousands separators, for readable output.</summary>
    internal static string Bytes(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
}
