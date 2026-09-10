using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// One deliberately malformed image, as described by
/// <c>fixtures/hostile/cases.json</c>.
/// </summary>
/// <param name="ExpectedCodes">
/// The exit codes this input is allowed to produce. Usually one: a case that
/// could honestly answer either way says so rather than letting the reader pick.
/// </param>
internal sealed record HostileCase(
    string Name,
    string Path,
    string Boundary,
    long Size,
    string Sha256,
    string Mutation,
    IReadOnlyList<DmgExitCode> ExpectedCodes,
    string Why,
    int BudgetMs,
    int MaxManagedMib)
{
    public TimeSpan Budget => TimeSpan.FromMilliseconds(BudgetMs);

    public long MaxManagedBytes => (long)MaxManagedMib * 1024 * 1024;

    public string Expected => string.Join(" or ", ExpectedCodes);

    public override string ToString() => $"{Name} -> {Expected}";
}

/// <summary>
/// Loads the hostile corpus produced by <c>tools/make-hostile-corpus.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is gitignored, exactly like <c>fixtures/generated/</c>, because it
/// is derived from it: every image is a mutation of a real <c>hdiutil</c>
/// fixture. When it is absent the tests that consume it report themselves as
/// skipped rather than failing - a machine without the corpus has not proved
/// anything, but neither has it found a bug.
/// </para>
/// <para>
/// Each record carries its own SHA-256 so a corpus left over from an older
/// generator run is detected as drift instead of silently testing the wrong
/// bytes.
/// </para>
/// </remarks>
internal static class HostileCorpus
{
    internal const string NoCorpusCase = "(no hostile corpus)";

    private static readonly Lazy<HostileCorpusState> LazyState = new(Load, isThreadSafe: true);

    internal static bool IsAvailable => LazyState.Value.Cases.Count > 0;

    internal static string? UnavailableReason => LazyState.Value.UnavailableReason;

    internal static IReadOnlyList<HostileCase> Cases => LazyState.Value.Cases;

    internal static IReadOnlyList<string> Unusable => LazyState.Value.Unusable;

    internal static string Directory => LazyState.Value.Directory;

    internal static string Regenerate =>
        "Run tools/make-fixtures.sh and then tools/make-hostile-corpus.py from the "
        + "repository root. The hostile corpus is built by mutating a real hdiutil "
        + "fixture, so the good corpus has to exist first, and both are regenerated "
        + "together: hdiutil bakes fresh UUIDs into every image it writes.";

    internal static HostileCase? Find(string name) =>
        Cases.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));

    internal static TheoryData<string> Names()
    {
        TheoryData<string> data = [];
        if (!IsAvailable)
        {
            data.Add(NoCorpusCase);
            return data;
        }

        foreach (HostileCase item in Cases)
        {
            data.Add(item.Name);
        }

        return data;
    }

    private static HostileCorpusState Load()
    {
        string? root = FindRepositoryRoot();
        if (root is null)
        {
            return HostileCorpusState.Unavailable(
                "unknown",
                $"Could not locate Windows_DMG.sln above '{AppContext.BaseDirectory}'.");
        }

        string directory = System.IO.Path.Combine(root, "fixtures", "hostile");
        string indexPath = System.IO.Path.Combine(directory, "cases.json");
        if (!File.Exists(indexPath))
        {
            return HostileCorpusState.Unavailable(
                directory,
                $"There is no fixtures/hostile/cases.json. {Regenerate}");
        }

        List<HostileCase> cases = [];
        List<string> unusable = [];
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(indexPath)))
        {
            if (!document.RootElement.TryGetProperty("cases", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return HostileCorpusState.Unavailable(
                    directory,
                    $"fixtures/hostile/cases.json carries no cases array. {Regenerate}");
            }

            foreach (JsonElement element in items.EnumerateArray())
            {
                string name = Text(element, "name");
                string path = System.IO.Path.Combine(directory, name);
                Result<IReadOnlyList<DmgExitCode>> codes = ReadCodes(element, name);
                if (!codes.TryGetValue(out IReadOnlyList<DmgExitCode>? expected))
                {
                    unusable.Add($"{name}: {codes.Error.Message}");
                    continue;
                }

                HostileCase item = new(
                    name,
                    path,
                    Text(element, "boundary"),
                    Number(element, "size"),
                    Text(element, "sha256"),
                    Text(element, "mutation"),
                    expected,
                    Text(element, "why"),
                    (int)Number(element, "budget_ms"),
                    (int)Number(element, "max_managed_mib"));

                if (!File.Exists(path))
                {
                    unusable.Add(
                        $"{name}: listed in cases.json but not present in fixtures/hostile/.");
                    continue;
                }

                if (new FileInfo(path).Length != item.Size)
                {
                    unusable.Add(
                        $"{name}: on disk it is {new FileInfo(path).Length} bytes, cases.json "
                        + $"says {item.Size}. {Regenerate}");
                    continue;
                }

                cases.Add(item);
            }
        }

        return cases.Count == 0
            ? HostileCorpusState.Unavailable(
                directory,
                $"cases.json lists no usable case. {Regenerate}")
            : new HostileCorpusState(directory, null, cases, unusable);
    }

    private static Result<IReadOnlyList<DmgExitCode>> ReadCodes(JsonElement element, string name)
    {
        if (!element.TryGetProperty("expect_exit_codes", out JsonElement codes)
            || codes.ValueKind != JsonValueKind.Array)
        {
            return Result<IReadOnlyList<DmgExitCode>>.Failure(DmgError.Corrupt(
                $"the record for {name} declares no expect_exit_codes array."));
        }

        List<DmgExitCode> parsed = [];
        foreach (JsonElement code in codes.EnumerateArray())
        {
            if (code.ValueKind != JsonValueKind.String
                || !Enum.TryParse(code.GetString(), out DmgExitCode value)
                || value == DmgExitCode.Success)
            {
                return Result<IReadOnlyList<DmgExitCode>>.Failure(DmgError.Corrupt(
                    $"the record for {name} names '{code}', which is not a failing "
                    + "DmgExitCode."));
            }

            parsed.Add(value);
        }

        return parsed.Count == 0
            ? Result<IReadOnlyList<DmgExitCode>>.Failure(DmgError.Corrupt(
                $"the record for {name} expects no exit code at all."))
            : Result<IReadOnlyList<DmgExitCode>>.Success(parsed);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
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

    private sealed record HostileCorpusState(
        string Directory,
        string? UnavailableReason,
        IReadOnlyList<HostileCase> Cases,
        IReadOnlyList<string> Unusable)
    {
        internal static HostileCorpusState Unavailable(string directory, string reason) =>
            new(directory, reason, [], []);
    }
}

/// <summary>What one run of the reader over one hostile input actually did.</summary>
internal sealed record HostileOutcome(
    Result Result,
    Exception? Failure,
    long AllocatedBytes,
    TimeSpan Elapsed,
    bool TimedOut)
{
    public string AllocatedMib =>
        (AllocatedBytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>
/// Runs one piece of reader work under a wall-clock bound and a managed-allocation
/// bound, catching anything it throws.
/// </summary>
/// <remarks>
/// <para>
/// The work runs on its own background thread rather than inline for one reason:
/// a hang has to be a test failure, and a <c>Stopwatch</c> assertion after the
/// call never runs if the call never returns. <see cref="Thread.Join(TimeSpan)"/>
/// gives up on it and the thread is left to die with the process.
/// </para>
/// <para>
/// Allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> from inside that thread, so
/// it is this work's own allocation and not the test host's. It is a cumulative
/// total rather than a peak, which is the conservative direction: a
/// buffer-then-discard loop counts every buffer, and the multi-gigabyte
/// single-allocation spike these cases are about cannot hide in it.
/// </para>
/// </remarks>
internal static class HostileRunner
{
    internal static HostileOutcome Run(Func<Result> work, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(work);

        Result result = Result.Failure(DmgError.Internal("The work never ran."));
        Exception? failure = null;
        long allocated = 0;

        Thread thread = new(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                result = work();
            }
#pragma warning disable CA1031 // Catching everything is the point: an unhandled
            catch (Exception exception) // exception IS the failure this asserts on.
#pragma warning restore CA1031
            {
                failure = exception;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        })
        {
            IsBackground = true,
            Name = "hostile-input",
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        thread.Start();
        bool finished = thread.Join(budget);
        stopwatch.Stop();

        return new HostileOutcome(result, failure, allocated, stopwatch.Elapsed, !finished);
    }
}
