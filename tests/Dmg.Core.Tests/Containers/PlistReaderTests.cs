using System.Text;
using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The plist reader is the one place a UDIF image gets to hand us a parser, so it
/// is the one place an entity-expansion attack could land. These tests cover the
/// happy path against Apple's own output and the hostile paths against documents
/// no tool would write.
/// </summary>
public sealed class PlistReaderTests
{
    [Fact]
    public void ParsesThePropertyListHdiutilWrote()
    {
        Result<PlistValue> result = PlistReader.Parse(RealImageSamples.PlistUtf8());

        Assert.True(result.TryGetValue(out PlistValue? root), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(PlistKind.Dictionary, root.Kind);

        Assert.True(root.TryGetEntry("resource-fork", PlistKind.Dictionary, out PlistValue? resourceFork));
        Assert.True(resourceFork.TryGetEntry("blkx", PlistKind.Array, out PlistValue? blkx));
        Assert.Equal(2, blkx.Items.Count);

        PlistValue first = blkx.Items[0];
        Assert.True(first.TryGetString("CFName", out string? name));
        Assert.Equal("Master Boot Record (MBR : 0)", name);
        Assert.True(first.TryGetString("ID", out string? id));
        Assert.Equal("-1", id);

        Assert.True(first.TryGetEntry("Data", PlistKind.Data, out PlistValue? data));
        Assert.Contains("\n", data.Text, StringComparison.Ordinal);
        Assert.StartsWith("bWlzaA", data.Text.Trim(), StringComparison.Ordinal);

        // plst sits alongside blkx and must not confuse the walk.
        Assert.True(resourceFork.TryGetEntry("plst", PlistKind.Array, out _));
    }

    [Fact]
    public void TheRealPropertyListCarriesADoctypeDeclaration()
    {
        // This is why DtdProcessing.Prohibit alone is not enough: prohibit throws
        // on the declaration itself, so it has to be removed from the prolog first.
        Assert.Contains("<!DOCTYPE plist PUBLIC", RealImageSamples.PlistText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADoctypeWithoutAnInternalSubsetIsStrippedAndTheDocumentParses()
    {
        const string Xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>a</key><string>b</string></dict></plist>
            """;

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.True(result.TryGetValue(out PlistValue? root));
        Assert.True(root.TryGetString("a", out string? value));
        Assert.Equal("b", value);
    }

    [Fact]
    public void EntityExpansionFailsClosed()
    {
        // The billion-laughs shape. It must be refused at the doctype, not expanded
        // and then found to be too big.
        const string Xml = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            ]>
            <plist version="1.0"><dict><key>a</key><string>&lol3;</string></dict></plist>
            """;

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains("internal DTD subset", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExternalEntityIsRefusedRatherThanFetched()
    {
        const string Xml = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <plist version="1.0"><dict><key>a</key><string>&secret;</string></dict></plist>
            """;

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AnUndefinedEntityIsAParseFailureNotASilentEmptyString()
    {
        const string Xml = "<plist version=\"1.0\"><dict><key>a</key><string>&mystery;</string></dict></plist>";

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnUnterminatedDoctypeIsRejected()
    {
        Result<PlistValue> result = PlistReader.Parse("<!DOCTYPE plist PUBLIC \"x\"");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void OnlyTheProlugIsSearchedForADoctype()
    {
        // The literal characters "<!DOCTYPE" inside content must survive untouched;
        // stripping them would silently rewrite the document being parsed.
        const string Xml = "<plist><dict><key>a</key><string><![CDATA[<!DOCTYPE evil>]]></string></dict></plist>";

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.True(result.TryGetValue(out PlistValue? root));
        Assert.True(root.TryGetString("a", out string? value));
        Assert.Equal("<!DOCTYPE evil>", value);
    }

    [Fact]
    public void ReadsEveryElementKindTheContainerUses()
    {
        const string Xml = """
            <plist version="1.0">
            <dict>
              <key>name</key><string>disk</string>
              <key>size</key><integer>67647</integer>
              <key>negative</key><integer>-1</integer>
              <key>payload</key><data>aGVsbG8=</data>
              <key>list</key><array><string>one</string><integer>2</integer><dict/></array>
              <key>empty</key><dict/>
            </dict>
            </plist>
            """;

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.True(result.TryGetValue(out PlistValue? root));
        Assert.Equal(6, root.Entries.Count);
        Assert.Equal(67647, root.Entries["size"].Integer);
        Assert.Equal(-1, root.Entries["negative"].Integer);
        Assert.Equal(PlistKind.Data, root.Entries["payload"].Kind);
        Assert.Equal("aGVsbG8=", root.Entries["payload"].Text);
        Assert.Equal(3, root.Entries["list"].Items.Count);
        Assert.Empty(root.Entries["empty"].Entries);
    }

    [Fact]
    public void AnUnsupportedElementIsDroppedWithoutSwallowingItsSibling()
    {
        // <true/> and <real> are legal plist and useless here. Skipping them must
        // not consume the key/value pair that follows.
        const string Xml = """
            <plist version="1.0">
            <dict>
              <key>flag</key><true/>
              <key>ratio</key><real>1.5</real>
              <key>kept</key><string>still here</string>
            </dict>
            </plist>
            """;

        Result<PlistValue> result = PlistReader.Parse(Xml);

        Assert.True(result.TryGetValue(out PlistValue? root));
        Assert.True(root.TryGetString("kept", out string? kept));
        Assert.Equal("still here", kept);
        Assert.False(root.TryGetEntry("flag", out _));
        Assert.False(root.TryGetEntry("ratio", out _));
    }

    [Fact]
    public void NestingIsBounded()
    {
        var builder = new StringBuilder("<plist version=\"1.0\">");

        for (int index = 0; index < PlistReader.MaxDepth + 10; index++)
        {
            builder.Append("<array>");
        }

        Result<PlistValue> result = PlistReader.Parse(builder.ToString());

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("nested too deeply", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<plist><dict><key>a</key></dict></plist>", "key")]
    [InlineData("<plist><dict><string>orphan</string></dict></plist>", "no <key>")]
    [InlineData("<plist><dict><key>a</key><key>b</key><string>x</string></dict></plist>", "two <key>")]
    [InlineData("<plist><dict><key>a</key><string>x</string><key>a</key><string>y</string></dict></plist>", "two <key>")]
    [InlineData("<plist><key>loose</key></plist>", "outside")]
    public void MalformedStructureIsReportedNotGuessedAt(string xml, string expectedFragment)
    {
        Result<PlistValue> result = PlistReader.Parse(xml);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains(expectedFragment, result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<plist><dict><key>a</key><string>unclosed</dict></plist>")]
    [InlineData("<plist/>")]
    [InlineData("<plist version=\"1.0\"></plist>")]
    [InlineData("<plist><dict><key>a</key><integer>twelve</integer></dict></plist>")]
    [InlineData("<plist><dict><key>a</key><integer></integer></dict></plist>")]
    [InlineData("<plist><dict><key>a</key><string><nested/></string></dict></plist>")]
    public void GarbageIsAFailureAndNeverAnException(string xml)
    {
        Result<PlistValue> result = PlistReader.Parse(xml);

        Assert.False(result.Ok);
        Assert.NotEqual(DmgExitCode.Success, result.Error.Code);
    }

    [Fact]
    public void ARealPropertyListTruncatedAnywhereFailsCleanly()
    {
        string xml = RealImageSamples.PlistText();

        for (int keep = 1; keep < xml.Length; keep += 97)
        {
            Result<PlistValue> result = PlistReader.Parse(xml[..keep]);
            Assert.False(result.Ok, $"Truncating to {keep} characters was accepted.");
        }
    }

    [Fact]
    public void AByteOrderMarkIsNotContent()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. RealImageSamples.PlistUtf8()];

        Assert.True(PlistReader.Parse(bytes).Ok);
    }

    [Fact]
    public void ReadsThePropertyListFromWhereTheTrailerSaysItIs()
    {
        byte[] xml = RealImageSamples.PlistUtf8();
        byte[] image = new byte[1024 + xml.Length + 512];
        xml.CopyTo(image, 1024);

        using var stream = new MemoryStream(image);
        Result<PlistValue> result = PlistReader.Read(stream, 1024, (ulong)xml.Length);

        Assert.True(result.TryGetValue(out PlistValue? root));
        Assert.True(root.TryGetEntry("resource-fork", out _));
    }

    [Fact]
    public void AnXmlLengthOfZeroIsUnsupportedNotAnEmptyDocument()
    {
        using var stream = new MemoryStream(new byte[4096]);

        Result<PlistValue> result = PlistReader.Read(stream, 0, 0);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AnImplausibleXmlLengthIsRefusedBeforeAnythingIsAllocated()
    {
        using var stream = new MemoryStream(new byte[4096]);

        Result<PlistValue> result = PlistReader.Read(stream, 0, long.MaxValue);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("implausibly large", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4000ul, 200ul)]
    [InlineData(5000ul, 1ul)]
    [InlineData(ulong.MaxValue, 1ul)]
    public void AnXmlRangeOutsideTheFileIsRefused(ulong offset, ulong length)
    {
        using var stream = new MemoryStream(new byte[4096]);

        Result<PlistValue> result = PlistReader.Read(stream, offset, length);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("outside the image", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCeilingIsCallerControlled()
    {
        byte[] xml = RealImageSamples.PlistUtf8();
        byte[] image = new byte[xml.Length];
        xml.CopyTo(image, 0);

        using var stream = new MemoryStream(image);

        Assert.False(PlistReader.Read(stream, 0, (ulong)xml.Length, maxLength: 100).Ok);
        Assert.True(PlistReader.Read(stream, 0, (ulong)xml.Length, maxLength: xml.Length).Ok);
    }
}
