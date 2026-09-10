namespace Dmg.Core.Tests.Containers;

/// <summary>
/// Bytes lifted verbatim out of an image produced by <c>hdiutil</c> on macOS 15.
/// </summary>
/// <remarks>
/// <para>
/// The image was made with
/// <c>hdiutil create -srcfolder … -fs "MS-DOS FAT32" -format UDZO -volname TESTVOL</c>
/// and is 74,814 bytes: a protective MBR region of one sector plus a 67,646-sector
/// FAT32 region, 67,647 sectors of decoded disk in total.
/// </para>
/// <para>
/// These constants exist so the parsers are checked against a real Apple-written
/// container and not only against structures this test suite invented. The field
/// offsets in <c>docs/03-udif-format-reference.md</c> are marked unverified; where
/// the document and these bytes disagree, these bytes win.
/// </para>
/// </remarks>
internal static class RealImageSamples
{
    /// <summary>The total size of the image the samples were taken from.</summary>
    public const long FileLength = 74814;

    /// <summary>The 512-byte koly trailer, base64-encoded.</summary>
    public const string KolyBase64 =
        "a29seQAAAAQAAAIAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAEULgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAABFC4AAAAAAAAOEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAAg" +
        "veeInAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAABCD8AAAAA" +
        "AAAAAAAAAAA=";

    /// <summary>The koly trailer as bytes.</summary>
    public static byte[] Koly() => Convert.FromBase64String(KolyBase64);
}
