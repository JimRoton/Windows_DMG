using System.Text;
using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// Walking the property list to the block map, and turning each entry's wrapped
/// base64 into bytes without letting a few kilobytes of XML name a gigabyte of
/// allocation.
/// </summary>
public sealed class BlkxReaderTests
{
    [Fact]
    public void ExtractsBothRegionsOfAnHdiutilImage()
    {
        IReadOnlyList<BlkxEntry> entries = ExtractFrom(RealImageSamples.PlistText());

        Assert.Equal(2, entries.Count);
        Assert.Equal("Master Boot Record (MBR : 0)", entries[0].DisplayName);
        Assert.Equal("-1", entries[0].Id);
        Assert.Equal("0x0050", entries[0].Attributes);
        Assert.Equal(" (DOS_FAT_32 : 1)", entries[1].DisplayName);
        Assert.Equal("0", entries[1].Id);
    }

    [Fact]
    public void EachDecodedPayloadIsAMishBlock()
    {
        // The point of the exercise: the wrapped, tab-indented base64 in a real
        // plist decodes to something starting with 'mish'.
        foreach (BlkxEntry entry in ExtractFrom(RealImageSamples.PlistText()))
        {
            Assert.True(entry.Data.Length > 204);
            Assert.Equal("mish"u8.ToArray(), entry.Data[..4].ToArray());
        }
    }

    [Fact]
    public void TheDecodedSizesMatchTheImageTheySayTheyDescribe()
    {
        IReadOnlyList<BlkxEntry> entries = ExtractFrom(RealImageSamples.PlistText());

        // 204-byte header plus 40 bytes per chunk: 2 chunks then 4 chunks.
        Assert.Equal(204 + (2 * 40), entries[0].Data.Length);
        Assert.Equal(204 + (4 * 40), entries[1].Data.Length);
    }

    [Fact]
    public void WhitespaceIsStrippedBeforeDecoding()
    {
        Result<byte[]> result = Base64Payload.Decode("  aGVs\n\t bG8=\r\n ", 1024, "payload");

        Assert.True(result.TryGetValue(out byte[]? bytes));
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void AnEmptyPayloadDecodesToNothingRatherThanFailing()
    {
        Result<byte[]> result = Base64Payload.Decode(" \n\t ", 1024, "payload");

        Assert.True(result.TryGetValue(out byte[]? bytes));
        Assert.Empty(bytes);
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("aGVsbG8")]      // truncated padding
    [InlineData("aGVsbG8==")]    // over-padded
    [InlineData("=")]
    public void InvalidBase64IsAFailureNotAFormatException(string text)
    {
        Result<byte[]> result = Base64Payload.Decode(text, 1024, "payload");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void TheDecodedSizeIsBoundedBeforeAnythingIsAllocated()
    {
        string text = new('A', 8_000_000); // ~6 MB decoded

        Result<byte[]> result = Base64Payload.Decode(text, maxBytes: 1024, "payload");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("implausibly large", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCeilingIsMeasuredAfterWhitespaceIsStripped()
    {
        // 8 significant characters, six bytes of payload, buried in 4 KB of newlines.
        string text = string.Join("\n", "aGVsbG8h".ToCharArray()).PadRight(4096, '\n');

        Result<byte[]> result = Base64Payload.Decode(text, maxBytes: 16, "payload");

        Assert.True(result.TryGetValue(out byte[]? bytes));
        Assert.Equal("hello!", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void AnEntryLargerThanTheCeilingStopsTheWholeExtraction()
    {
        string xml = Wrap("<dict><key>Data</key><data>" + new string('A', 200_000) + "</data></dict>");

        Result<IReadOnlyList<BlkxEntry>> result = Extract(xml, maxEntryBytes: 1024);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Theory]
    [InlineData("<plist><array/></plist>", "not a dictionary")]
    [InlineData("<plist><dict><key>other</key><string>x</string></dict></plist>", "resource-fork")]
    [InlineData("<plist><dict><key>resource-fork</key><string>x</string></dict></plist>", "resource-fork")]
    [InlineData("<plist><dict><key>resource-fork</key><dict><key>plst</key><array/></dict></dict></plist>", "blkx")]
    [InlineData("<plist><dict><key>resource-fork</key><dict><key>blkx</key><string>x</string></dict></dict></plist>", "blkx")]
    public void AMissingOrMistypedPathIsReportedByName(string xml, string expectedFragment)
    {
        Result<IReadOnlyList<BlkxEntry>> result = Extract(xml);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains(expectedFragment, result.Error.Message + result.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBlockMapIsCorruptionNotAnEmptyDisk()
    {
        Result<IReadOnlyList<BlkxEntry>> result = Extract(Wrap(string.Empty));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("no regions", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithoutDataIsRejectedAndSaysWhichOne()
    {
        string xml = Wrap(
            "<dict><key>Data</key><data>bWlzaA==</data></dict>" +
            "<dict><key>CFName</key><string>no payload</string></dict>");

        Result<IReadOnlyList<BlkxEntry>> result = Extract(xml);

        Assert.False(result.Ok);
        Assert.Contains("Region 1", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryThatIsNotADictionaryIsRejected()
    {
        Result<IReadOnlyList<BlkxEntry>> result = Extract(Wrap("<string>not an entry</string>"));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void MissingNamesAreEmptyNotNull()
    {
        IReadOnlyList<BlkxEntry> entries = ExtractFrom(
            Wrap("<dict><key>Data</key><data>bWlzaA==</data></dict>"));

        Assert.Equal(string.Empty, entries[0].Name);
        Assert.Equal(string.Empty, entries[0].CfName);
        Assert.Equal("blkx[0]", entries[0].DisplayName);
    }

    [Fact]
    public void EntriesKeepTheirOrderAndIndex()
    {
        string xml = Wrap(string.Concat(Enumerable.Range(0, 5).Select(index =>
            $"<dict><key>CFName</key><string>region {index}</string>" +
            "<key>Data</key><data>bWlzaA==</data></dict>")));

        IReadOnlyList<BlkxEntry> entries = ExtractFrom(xml);

        Assert.Equal(5, entries.Count);

        for (int index = 0; index < entries.Count; index++)
        {
            Assert.Equal(index, entries[index].Index);
            Assert.Equal($"region {index}", entries[index].CfName);
        }
    }

    private static string Wrap(string entries) =>
        "<plist version=\"1.0\"><dict><key>resource-fork</key><dict>" +
        "<key>blkx</key><array>" + entries + "</array>" +
        "<key>plst</key><array/>" +
        "</dict></dict></plist>";

    private static Result<IReadOnlyList<BlkxEntry>> Extract(
        string xml,
        long maxEntryBytes = BlkxReader.DefaultMaxEntryBytes)
    {
        Result<PlistValue> parsed = PlistReader.Parse(xml);
        Assert.True(parsed.TryGetValue(out PlistValue? root), parsed.Ok ? "" : parsed.Error.ToString());
        return BlkxReader.Extract(root, maxEntryBytes);
    }

    private static IReadOnlyList<BlkxEntry> ExtractFrom(string xml)
    {
        Result<IReadOnlyList<BlkxEntry>> result = Extract(xml);
        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        return result.Value!;
    }
}
