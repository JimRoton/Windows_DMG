using System.Text.Json;
using Dmg.Core.Diagnostics;

namespace Dmg.Core.Tests;

public sealed class ConsoleOutputTests
{
    /// <summary>
    /// A sink over two in-memory writers, so every assertion below is about real
    /// bytes on real streams rather than a mock's recollection of a call.
    /// </summary>
    private sealed class Sink : IDisposable
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        internal Sink(Verbosity verbosity = Verbosity.Normal, bool isJson = false) =>
            Output = new ConsoleOutput(_out, _err, verbosity, isJson);

        internal ConsoleOutput Output { get; }

        internal string Stdout => _out.ToString();

        internal string Stderr => _err.ToString();

        public void Dispose()
        {
            _out.Dispose();
            _err.Dispose();
        }
    }

    // ---- the stream split ----------------------------------------------

    [Fact]
    public void HumanReadableOutputGoesToStdout()
    {
        using Sink sink = new();

        sink.Output.WriteLine("Mounted installer.dmg -> E:\\");

        Assert.Contains("Mounted installer.dmg", sink.Stdout, StringComparison.Ordinal);
        Assert.Empty(sink.Stderr);
    }

    [Fact]
    public void WriteDoesNotAddANewline()
    {
        using Sink sink = new();

        sink.Output.Write("abc");
        sink.Output.Write("def");

        Assert.Equal("abcdef", sink.Stdout);
    }

    [Theory]
    [InlineData(Verbosity.Normal)]
    [InlineData(Verbosity.Verbose)]
    public void DiagnosticsAndProgressGoToStderrNeverStdout(Verbosity verbosity)
    {
        using Sink sink = new(verbosity);

        sink.Output.Progress("Decoding chunk 12 of 340...");
        sink.Output.Warning("Checksum field is zero; skipping verification.");
        sink.Output.Trace("blkx entry 3: 2048 sectors, zlib");
        sink.Output.Error("something went wrong");
        sink.Output.Error(new DmgError(DmgExitCode.CorruptImage, "Bad koly magic.", "offset 0x1F40"));

        Assert.Empty(sink.Stdout);
        Assert.Contains("Decoding chunk 12", sink.Stderr, StringComparison.Ordinal);
        Assert.Contains("Checksum field is zero", sink.Stderr, StringComparison.Ordinal);
        Assert.Contains("Bad koly magic.", sink.Stderr, StringComparison.Ordinal);
    }

    // ---- verbosity filtering -------------------------------------------

    [Fact]
    public void QuietSuppressesEverythingExceptErrors()
    {
        using Sink sink = new(Verbosity.Quiet);

        sink.Output.WriteLine("Mounted.");
        sink.Output.Write("partial");
        sink.Output.Progress("Decoding...");
        sink.Output.Warning("heads up");
        sink.Output.Trace("blkx entry 3");

        Assert.Empty(sink.Stdout);
        Assert.Empty(sink.Stderr);

        sink.Output.Error("this one always shows");

        Assert.Contains("this one always shows", sink.Stderr, StringComparison.Ordinal);
        Assert.Empty(sink.Stdout);
    }

    [Fact]
    public void QuietStillReportsAStructuredError()
    {
        using Sink sink = new(Verbosity.Quiet);

        sink.Output.Error(new DmgError(DmgExitCode.ElevationRequired, "Run this from an elevated shell."));

        Assert.Contains("Run this from an elevated shell.", sink.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalShowsResultsWarningsAndProgressButNotTrace()
    {
        using Sink sink = new(Verbosity.Normal);

        sink.Output.WriteLine("Mounted.");
        sink.Output.Progress("Decoding...");
        sink.Output.Warning("heads up");
        sink.Output.Trace("blkx entry 3");

        Assert.Contains("Mounted.", sink.Stdout, StringComparison.Ordinal);
        Assert.Contains("Decoding...", sink.Stderr, StringComparison.Ordinal);
        Assert.Contains("warning: heads up", sink.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("blkx entry 3", sink.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void VerboseAddsTheTrace()
    {
        using Sink sink = new(Verbosity.Verbose);

        sink.Output.Trace("blkx entry 3: 2048 sectors, zlib");

        Assert.Contains("blkx entry 3: 2048 sectors, zlib", sink.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorDetailAppearsOnlyAtVerbose()
    {
        DmgError error = new(DmgExitCode.CorruptImage, "Bad koly magic.", "expected 'koly' at offset 0x1F40");

        using Sink normal = new(Verbosity.Normal);
        normal.Output.Error(error);
        Assert.DoesNotContain("0x1F40", normal.Stderr, StringComparison.Ordinal);

        using Sink verbose = new(Verbosity.Verbose);
        verbose.Output.Error(error);
        Assert.Contains("0x1F40", verbose.Stderr, StringComparison.Ordinal);
    }

    // ---- the JSON invariant --------------------------------------------

    /// <summary>
    /// The invariant that matters most: in JSON mode stdout holds the JSON document
    /// and absolutely nothing else, no matter how chatty the verbosity is. A caller
    /// pipes stdout into a parser without filtering it first.
    /// </summary>
    [Theory]
    [InlineData(Verbosity.Quiet)]
    [InlineData(Verbosity.Normal)]
    [InlineData(Verbosity.Verbose)]
    public void InJsonModeStdoutHoldsTheJsonDocumentAndNothingElse(Verbosity verbosity)
    {
        const string Document = """{"image":"installer.dmg","filesystem":"exFAT","bytes":2576980377}""";

        using Sink sink = new(verbosity, isJson: true);

        sink.Output.WriteLine("Mounted installer.dmg -> E:\\");
        sink.Output.Write("no newline either");
        sink.Output.Progress("Decoding chunk 12 of 340...");
        sink.Output.Warning("Checksum field is zero.");
        sink.Output.Trace("blkx entry 3: 2048 sectors, zlib");
        sink.Output.Error("even an error stays off stdout");
        sink.Output.WriteJson(Document);

        Assert.Equal(Document, sink.Stdout.Trim());

        // And it really is a single parseable document, not a document plus noise.
        using JsonDocument parsed = JsonDocument.Parse(sink.Stdout);
        Assert.Equal("installer.dmg", parsed.RootElement.GetProperty("image").GetString());
    }

    [Fact]
    public void JsonModeStillEmitsDiagnosticsOnStderr()
    {
        using Sink sink = new(Verbosity.Verbose, isJson: true);

        sink.Output.Progress("Decoding...");
        sink.Output.Trace("blkx entry 3");
        sink.Output.Error(new DmgError(DmgExitCode.MountFailed, "AttachVirtualDisk failed.", "0x80070005"));
        sink.Output.WriteJson("""{"ok":false}""");

        Assert.Contains("Decoding...", sink.Stderr, StringComparison.Ordinal);
        Assert.Contains("blkx entry 3", sink.Stderr, StringComparison.Ordinal);
        Assert.Contains("AttachVirtualDisk failed.", sink.Stderr, StringComparison.Ordinal);
        Assert.Equal("""{"ok":false}""", sink.Stdout.Trim());
    }

    [Fact]
    public void OnlyOneJsonDocumentMayBeWritten()
    {
        using Sink sink = new(isJson: true);

        sink.Output.WriteJson("""{"first":true}""");

        Assert.Throws<InvalidOperationException>(() => sink.Output.WriteJson("""{"second":true}"""));
        Assert.Equal("""{"first":true}""", sink.Stdout.Trim());
    }

    // ---- WriteJson outside JSON mode -----------------------------------

    /// <summary>
    /// The mirror image of the invariant above. In JSON mode stdout holds only the
    /// document; outside JSON mode stdout holds only human-readable output, and a
    /// JSON document written into it would leave stdout parseable as neither. The
    /// call is a programming error, so it throws instead of writing.
    /// </summary>
    [Theory]
    [InlineData(Verbosity.Quiet)]
    [InlineData(Verbosity.Normal)]
    [InlineData(Verbosity.Verbose)]
    public void WriteJsonOutsideJsonModeThrowsAndWritesNothing(Verbosity verbosity)
    {
        using Sink sink = new(verbosity, isJson: false);

        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => sink.Output.WriteJson("""{"ok":true}"""));

        Assert.Contains("IsJson", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Stdout);
        Assert.Empty(sink.Stderr);
    }

    /// <summary>
    /// The real-world shape of the bug: human output already on stdout, then a
    /// JSON document. The throw has to come before the write, or stdout is
    /// corrupted whether or not the caller catches it.
    /// </summary>
    [Fact]
    public void WriteJsonDoesNotInterleaveWithHumanOutput()
    {
        using Sink sink = new(Verbosity.Normal);

        sink.Output.WriteLine("Mounted installer.dmg -> E:\\");

        Assert.Throws<InvalidOperationException>(() => sink.Output.WriteJson("""{"image":"installer.dmg"}"""));

        Assert.DoesNotContain("{", sink.Stdout, StringComparison.Ordinal);
        Assert.Equal("Mounted installer.dmg -> E:\\", sink.Stdout.Trim());
    }

    /// <summary>
    /// The refusal must not consume the one-document budget: the sink is still
    /// usable afterwards, and a later legitimate call on a JSON-mode sink is
    /// unaffected. (A refused call that had set the "already written" flag would
    /// turn one bug into two.)
    /// </summary>
    [Fact]
    public void ARefusedWriteJsonLeavesTheSinkUsable()
    {
        using Sink human = new(Verbosity.Normal);

        Assert.Throws<InvalidOperationException>(() => human.Output.WriteJson("""{"nope":true}"""));

        human.Output.WriteLine("still working");
        human.Output.Warning("and so is stderr");

        Assert.Equal("still working", human.Stdout.Trim());
        Assert.Contains("warning: and so is stderr", human.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A null document outside JSON mode is still reported as the mode error: the
    /// argument is beside the point when the call should not have happened at all.
    /// </summary>
    [Fact]
    public void ModeIsCheckedBeforeTheArgument()
    {
        using Sink sink = new();

        Assert.Throws<InvalidOperationException>(() => sink.Output.WriteJson(null!));
    }

    [Fact]
    public void JsonModeIsAdvertisedOnTheInterface()
    {
        using Sink json = new(Verbosity.Normal, isJson: true);
        using Sink human = new(Verbosity.Verbose);

        Assert.True(json.Output.IsJson);
        Assert.Equal(Verbosity.Normal, json.Output.Verbosity);

        Assert.False(human.Output.IsJson);
        Assert.Equal(Verbosity.Verbose, human.Output.Verbosity);
    }

    // ---- construction ---------------------------------------------------

    [Fact]
    public void WritersAreRequired()
    {
        using StringWriter writer = new();

        Assert.Throws<ArgumentNullException>(() => new ConsoleOutput(null!, writer));
        Assert.Throws<ArgumentNullException>(() => new ConsoleOutput(writer, null!));
    }

    [Fact]
    public void DefaultsToNormalAndHumanReadable()
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        ConsoleOutput output = new(stdout, stderr);

        Assert.Equal(Verbosity.Normal, output.Verbosity);
        Assert.False(output.IsJson);
    }
}
