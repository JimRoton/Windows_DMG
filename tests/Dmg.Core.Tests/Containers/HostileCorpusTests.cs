using Dmg.Core.Containers;
using Dmg.Core.Tests.Codecs;
using Xunit.Abstractions;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// S2.9 - the hostile-input hardening pass. Drives the whole container reader
/// over a corpus of deliberately malformed images and holds it to one contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract, for every single input:</b> a clean non-zero exit carrying a
/// specific <see cref="DmgExitCode"/> and a message worth showing a user. Three
/// properties are asserted on every case, not just the first:
/// </para>
/// <list type="number">
///   <item>no unhandled exception escapes the reader,</item>
///   <item>the work finishes inside a wall-clock budget, so a hang fails the test
///   rather than passing slowly,</item>
///   <item>the work stays inside a managed-allocation ceiling, so a four-gibibyte
///   buffer sized from an attacker-controlled field fails the test rather than
///   quietly succeeding on a big enough machine.</item>
/// </list>
/// <para>
/// "It didn't crash" is deliberately not the bar. A reader that answers
/// <see cref="DmgExitCode.InternalError"/> to a malformed image has still failed:
/// the whole point of the exit-code vocabulary is that the caller can tell
/// "this file is damaged" from "this program is broken".
/// </para>
/// <para>
/// The corpus and the expectations both come from
/// <c>tools/make-hostile-corpus.py</c>, which mutates a real <c>hdiutil</c>
/// fixture and writes <c>fixtures/hostile/cases.json</c>. Keeping the expected
/// exit code next to the mutation that motivates it is deliberate: a table of
/// expectations that lives away from the bytes drifts out of step with them.
/// </para>
/// </remarks>
public sealed class HostileCorpusTests
{
    /// <summary>
    /// The structural boundaries S2.9 requires the corpus to reach. Asserted as a
    /// set so that deleting a case from the generator fails a test instead of
    /// quietly shrinking the corpus.
    /// </summary>
    private static readonly string[] RequiredBoundaries =
    [
        "chunk",
        "chunk-table",
        "codec",
        "data-fork",
        "extent-map",
        "koly",
        "mish",
        "plist",
    ];

    /// <summary>Cases S2.9 names explicitly; all of them must be in the corpus.</summary>
    private static readonly string[] RequiredCases =
    [
        "blkx-data-oversized.dmg",
        "chunk-count-exhausts-memory.dmg",
        "chunk-sector-count-overflows-x512.dmg",
        "compressed-length-max.dmg",
        "compressed-offset-past-eof.dmg",
        "gap-between-extents.dmg",
        "koly-xml-length-max.dmg",
        "koly-xml-offset-outside-file.dmg",
        "overlapping-extents.dmg",
        "plist-billion-laughs.dmg",
        "truncated-mid-chunk-table.dmg",
        "truncated-mid-data-fork.dmg",
        "truncated-mid-mish.dmg",
        "truncated-mid-plist.dmg",
    ];

    private readonly ITestOutputHelper _output;

    public HostileCorpusTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> Corpus => HostileCorpus.Names();

    [Fact]
    public void TheHostileCorpusIsReported()
    {
        if (!Available())
        {
            return;
        }

        _output.WriteLine($"Hostile corpus: {HostileCorpus.Directory}");
        foreach (HostileCase item in HostileCorpus.Cases)
        {
            _output.WriteLine(
                $"  {item.Name,-40} {item.Boundary,-12} {item.Size,10:N0} bytes  "
                + $"-> {item.Expected}");
        }

        foreach (string problem in HostileCorpus.Unusable)
        {
            _output.WriteLine($"  UNUSABLE {problem}");
        }

        Assert.Empty(HostileCorpus.Unusable);
    }

    [Fact]
    public void TheCorpusReachesEveryStructuralBoundary()
    {
        if (!Available())
        {
            return;
        }

        string[] present = [.. HostileCorpus.Cases.Select(item => item.Boundary).Distinct().Order()];
        _output.WriteLine($"Boundaries covered: {string.Join(", ", present)}");
        Assert.Equal(RequiredBoundaries, present);

        string[] missing =
        [
            .. RequiredCases.Where(name => HostileCorpus.Find(name) is null),
        ];
        Assert.True(
            missing.Length == 0,
            $"The corpus is missing cases S2.9 requires: {string.Join(", ", missing)}. "
            + HostileCorpus.Regenerate);
    }

    /// <summary>
    /// The contract. One malformed image in, one named refusal out, inside both
    /// bounds, with nothing thrown.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryHostileImageIsRefusedCleanly(string caseName)
    {
        if (!TryGetCase(caseName, out HostileCase? item))
        {
            return;
        }

        HostileOutcome outcome = HostileRunner.Run(
            () => UdifImageDecoder.Decode(item.Path).Discard(),
            item.Budget);

        AssertBounds(item, outcome, "reading the container");

        Assert.False(
            outcome.Result.Ok,
            $"{item.Name}: the reader accepted it. {item.Mutation} {item.Why}");

        DmgError error = outcome.Result.Error;
        Assert.True(
            item.ExpectedCodes.Contains(error.Code),
            $"{item.Name}: expected {item.Expected} but got {error.Code}. "
            + $"{item.Mutation} {item.Why} The reader said: {error}");

        AssertUsefulMessage(item, error);

        _output.WriteLine(
            $"{item.Name}: {error.Code} in {outcome.Elapsed.TotalMilliseconds:N1} ms, "
            + $"{outcome.AllocatedMib} allocated - {error.Message}");
        if (error.Detail is not null)
        {
            _output.WriteLine($"  detail: {error.Detail}");
        }
    }

    /// <summary>
    /// The probe chain runs before anything else and on the same untrusted bytes,
    /// so it gets the same treatment. It is allowed to succeed - identifying a
    /// container is not the same as being able to read it - but it may never
    /// throw, hang, or answer <see cref="DmgExitCode.InternalError"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheProbeChainSurvivesEveryHostileImage(string caseName)
    {
        if (!TryGetCase(caseName, out HostileCase? item))
        {
            return;
        }

        HostileOutcome outcome = HostileRunner.Run(
            () => ImageFormatProbeChain.Default.Identify(item.Path).Discard(),
            item.Budget);

        AssertBounds(item, outcome, "identifying the format");

        if (outcome.Result.Ok)
        {
            _output.WriteLine(
                $"{item.Name}: identified as a container (the damage is deeper than the "
                + "probe looks), in "
                + $"{outcome.Elapsed.TotalMilliseconds:N1} ms, {outcome.AllocatedMib}");
            return;
        }

        DmgError error = outcome.Result.Error;
        Assert.True(
            error.Code is DmgExitCode.UnsupportedFormat
                or DmgExitCode.CorruptImage
                or DmgExitCode.UsageError,
            $"{item.Name}: the probe chain answered {error.Code}, which tells a user "
            + $"nothing actionable about a damaged file. It said: {error}");

        AssertUsefulMessage(item, error);
        _output.WriteLine(
            $"{item.Name}: probe says {error.Code} - {error.Message}");
    }

    /// <summary>
    /// The billion-laughs case, checked against the real implementation rather
    /// than against the intent.
    /// </summary>
    /// <remarks>
    /// The plist reader cannot simply set <c>DtdProcessing.Prohibit</c> and stop
    /// thinking: <c>hdiutil</c> writes a
    /// <c>&lt;!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" …&gt;</c>
    /// declaration into every image it makes, and Prohibit would reject those
    /// too. So the reader strips the declaration out of the prolog first - and
    /// that strip is precisely the hole an entity bomb would come through. The
    /// mitigation is that any doctype carrying an internal <c>[…]</c> subset is
    /// refused outright, and this test exists to prove that refusal is real and
    /// happens before any expansion, not that it was intended.
    /// </remarks>
    [Fact]
    public void BillionLaughsFailsClosedOnTheDoctype()
    {
        if (!TryGetCase("plist-billion-laughs.dmg", out HostileCase? item))
        {
            return;
        }

        HostileOutcome outcome = HostileRunner.Run(
            () => UdifImageDecoder.Decode(item.Path).Discard(),
            item.Budget);

        AssertBounds(item, outcome, "reading a billion-laughs property list");
        Assert.False(outcome.Result.Ok, "The entity bomb was accepted.");

        DmgError error = outcome.Result.Error;
        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("internal DTD subset", error.Message, StringComparison.Ordinal);

        // Fails closed on the declaration, so the expansion never starts: the
        // whole refusal costs less than the 31 KB image it came from.
        Assert.True(
            outcome.AllocatedBytes < 8L * 1024 * 1024,
            $"The bomb was refused, but only after allocating {outcome.AllocatedMib}. "
            + "That is expansion happening before the refusal.");

        _output.WriteLine(
            $"Refused in {outcome.Elapsed.TotalMilliseconds:N1} ms and "
            + $"{outcome.AllocatedMib}: {error}");

        // And the sibling case proves the refusal is on the doctype and not on
        // the nesting depth that billion-laughs happens to also have.
        if (!TryGetCase("plist-quadratic-blowup.dmg", out HostileCase? quadratic))
        {
            return;
        }

        HostileOutcome flat = HostileRunner.Run(
            () => UdifImageDecoder.Decode(quadratic.Path).Discard(),
            quadratic.Budget);

        AssertBounds(quadratic, flat, "reading a quadratic-blowup property list");
        Assert.False(flat.Result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, flat.Result.Error.Code);
        Assert.Contains("internal DTD subset", flat.Result.Error.Message, StringComparison.Ordinal);
        _output.WriteLine($"Quadratic blowup refused the same way: {flat.Result.Error}");
    }

    /// <summary>
    /// A control. If the reader refused everything it would pass every case above
    /// while being useless, so the unmutated source the corpus was cut from has to
    /// still read.
    /// </summary>
    [Fact]
    public void TheUnmutatedSourceStillReads()
    {
        if (!Available())
        {
            return;
        }

        string source = System.IO.Path.Combine(
            HostileCorpus.Directory, "..", "generated", "exfat-zlib.dmg");
        if (!File.Exists(source))
        {
            _output.WriteLine(
                $"SKIPPED - the source fixture is not on disk. {HostileCorpus.Regenerate}");
            return;
        }

        Result<DecodedImage> decoded = UdifImageDecoder.Decode(source);
        Assert.True(
            decoded.Ok,
            "The image every hostile case was mutated from does not itself read, so the "
            + $"corpus proves nothing: {(decoded.Ok ? string.Empty : decoded.Error.ToString())}");
        _output.WriteLine(
            $"Control: the unmutated source decodes to {decoded.Value!.Length:N0} bytes.");
    }

    private static void AssertUsefulMessage(HostileCase item, DmgError error)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(error.Message),
            $"{item.Name}: refused with an empty message.");
        Assert.True(
            error.Message.Length >= 16,
            $"{item.Name}: '{error.Message}' is too terse to help anyone.");
        Assert.True(
            error.Message.EndsWith('.'),
            $"{item.Name}: '{error.Message}' is not a sentence.");
        Assert.DoesNotContain("Exception", error.Message, StringComparison.Ordinal);
    }

    private void AssertBounds(HostileCase item, HostileOutcome outcome, string what)
    {
        Assert.False(
            outcome.TimedOut,
            $"{item.Name}: {what} did not finish within {item.BudgetMs} ms. "
            + $"{item.Mutation} A hang is a denial of service, not a slow answer.");

        if (outcome.Failure is not null)
        {
            Assert.Fail(
                $"{item.Name}: {what} threw {outcome.Failure.GetType().Name}: "
                + $"{outcome.Failure.Message}. {item.Mutation} {item.Why}"
                + Environment.NewLine + outcome.Failure);
        }

        Assert.True(
            outcome.AllocatedBytes <= item.MaxManagedBytes,
            $"{item.Name}: {what} allocated {outcome.AllocatedMib}, past the "
            + $"{item.MaxManagedMib} MiB ceiling this case sets. {item.Mutation} {item.Why}");
    }

    private bool Available()
    {
        if (HostileCorpus.IsAvailable)
        {
            return true;
        }

        _output.WriteLine($"SKIPPED - no hostile corpus: {HostileCorpus.UnavailableReason}");
        return false;
    }

    private bool TryGetCase(string caseName, out HostileCase item)
    {
        item = null!;
        if (string.Equals(caseName, HostileCorpus.NoCorpusCase, StringComparison.Ordinal)
            || !HostileCorpus.IsAvailable)
        {
            _output.WriteLine($"SKIPPED - no hostile corpus: {HostileCorpus.UnavailableReason}");
            return false;
        }

        HostileCase? found = HostileCorpus.Find(caseName);
        if (found is null)
        {
            _output.WriteLine($"SKIPPED {caseName}: not in this run's cases.json.");
            return false;
        }

        item = found;
        return true;
    }
}
