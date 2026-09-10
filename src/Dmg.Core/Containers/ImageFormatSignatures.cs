using System.Globalization;

namespace Dmg.Core.Containers;

/// <summary>
/// The table of things a file might be when it is not one of ours, and the one
/// place that decides what to call them.
/// </summary>
/// <remarks>
/// <para>
/// Two callers, on purpose. <see cref="RawImageProbe"/> asks this table whether a
/// file is something recognisably foreign, and declines it if so - otherwise the
/// raw probe, which claims any file that is a whole number of sectors, would
/// happily "open" a ZIP as a disk and hand back garbage. The terminal
/// <see cref="UnknownImageProbe"/> asks the same table for the name to put in the
/// refusal.
/// </para>
/// <para>
/// Keeping both on one table is what makes the two answers agree. A signature
/// added here immediately stops the raw probe swallowing that format and starts
/// the terminal naming it, with no second list to remember.
/// </para>
/// <para>
/// <b>These are signatures, not validations.</b> Matching here means "the first
/// bytes look like X", nothing stronger. The messages are worded to match: they
/// say what the file appears to be, because a wrong guess should read as a wrong
/// guess and not as a verdict.
/// </para>
/// </remarks>
public static class ImageFormatSignatures
{
    /// <summary>
    /// Offset 82 of a Disk Copy 4.2 header holds <c>0x0100</c>, which is the only
    /// fixed value that format has.
    /// </summary>
    private const int DiskCopy42MagicOffset = 82;

    /// <summary>A tar archive puts <c>ustar</c> at offset 257 of its first block.</summary>
    private const int TarMagicOffset = 257;

    /// <summary>ISO 9660 puts <c>CD001</c> at the start of the volume descriptor, 32 KiB in.</summary>
    private const int Iso9660MagicOffset = 0x8001;

    /// <summary>MacBinary III writes its own tag at offset 102 of the 128-byte header.</summary>
    private const int MacBinary3TagOffset = 102;

    /// <summary>The MacBinary II version byte; 129 or more means a real MacBinary header.</summary>
    private const int MacBinaryVersionOffset = 122;

    /// <summary>The suffix that makes a directory a sparse bundle.</summary>
    public const string SparseBundleExtension = ".sparsebundle";

    /// <summary>
    /// Both path separators, always. A Windows path is a Windows path even when this
    /// portable assembly is running on macOS, which is where its tests run.
    /// </summary>
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Identifies <paramref name="header"/> as a format this tool knows by name, or
    /// returns null when nothing matches.
    /// </summary>
    /// <param name="header">
    /// The first bytes of the file, however many there were. A short header simply
    /// matches fewer signatures; it is never an error.
    /// </param>
    /// <param name="length">The real length of the file, for the messages.</param>
    /// <param name="sourcePath">
    /// The path the bytes came from, when there is one. Only the sparse-bundle check
    /// uses it - a bundle is a directory, so its evidence is not in any one file.
    /// </param>
    public static ImageFormatSignature? Identify(ReadOnlySpan<byte> header, long length, string? sourcePath)
    {
        ImageFormatSignature? bundle = IdentifySparseBundle(sourcePath);

        if (bundle is not null)
        {
            return bundle;
        }

        ImageFormatSignature? apple = IdentifyAppleFormat(header);

        if (apple is not null)
        {
            return apple;
        }

        return IdentifyForeignFormat(header, length);
    }

    /// <summary>
    /// Decides whether a path names something inside - or is - a sparse bundle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.sparsebundle</c> is a directory holding <c>Info.plist</c> and a
    /// <c>bands</c> folder, so the only way to recognise one is by looking at the
    /// path rather than at bytes. Every ancestor is checked, not just the immediate
    /// parent: shell completion walks users into <c>image.sparsebundle/bands/0</c>
    /// as readily as into <c>image.sparsebundle/Info.plist</c>, and a band file is a
    /// perfectly plausible-looking stream of sectors that the raw probe would
    /// otherwise open.
    /// </para>
    /// <para>
    /// Both separators are treated as separators whichever operating system this is
    /// running on. The path came from a user, this assembly is portable, and its
    /// tests run on macOS against Windows paths.
    /// </para>
    /// </remarks>
    public static ImageFormatSignature? IdentifySparseBundle(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        string trimmed = sourcePath.TrimEnd(Separators);

        for (int end = trimmed.Length; end > 0;)
        {
            string ancestor = trimmed[..end];

            if (ancestor.EndsWith(SparseBundleExtension, StringComparison.OrdinalIgnoreCase))
            {
                return SparseBundle(
                    ancestor,
                    ancestor.Length == trimmed.Length
                        ? $"'{ancestor}' ends in {SparseBundleExtension}."
                        : $"'{trimmed}' sits inside the bundle '{ancestor}'.");
            }

            int separator = trimmed.LastIndexOfAny(Separators, end - 1);

            if (separator < 0)
            {
                break;
            }

            end = separator;
        }

        return null;
    }

    /// <summary>
    /// The Apple formats that are disk images but are not UDIF: sparse images,
    /// and the resource-fork-carrying wrappers that an NDIF arrives in.
    /// </summary>
    private static ImageFormatSignature? IdentifyAppleFormat(ReadOnlySpan<byte> header)
    {
        if (Matches(header, 0, "sprs"u8))
        {
            return new ImageFormatSignature(
                ImageFormat.SparseImage,
                "Apple sparse image (.sparseimage)",
                "This is an Apple sparse image, not a UDIF disk image. dmg reads UDIF (.dmg) "
                + "and raw sector images. Convert it first with "
                + "'hdiutil convert image.sparseimage -format UDZO -o image.dmg'.",
                "'sprs' at offset 0.");
        }

        // NDIF keeps its block map in a resource fork, so an NDIF that has travelled
        // across a non-Mac filesystem is always inside a wrapper. Recognising the
        // wrapper is the whole game: the payload underneath is not something this
        // tool will ever read, and saying "MacBinary" is what tells the user why
        // their twenty-year-old .img will not open.
        if (Matches(header, 0, [0x00, 0x05, 0x16, 0x07]))
        {
            return Ndif(
                "AppleDouble resource fork",
                "an AppleDouble resource fork",
                "AppleDouble magic 0x00051607 at offset 0.",
                header);
        }

        if (Matches(header, 0, [0x00, 0x05, 0x16, 0x00]))
        {
            return Ndif(
                "AppleSingle container",
                "an AppleSingle container",
                "AppleSingle magic 0x00051600 at offset 0.",
                header);
        }

        if (LooksLikeMacBinary(header))
        {
            return Ndif(
                "MacBinary-encoded Mac file",
                "a MacBinary-encoded Mac file",
                $"MacBinary header: name length {header[1]}, version byte "
                + $"{ByteAt(header, MacBinaryVersionOffset)}.",
                header);
        }

        if (Matches(header, DiskCopy42MagicOffset, [0x01, 0x00]) && header.Length > 0 && header[0] <= 63)
        {
            return new ImageFormatSignature(
                ImageFormat.Ndif,
                "Disk Copy 4.2 image",
                "This looks like a Disk Copy 4.2 image, the floppy-era format that came before "
                + "NDIF and UDIF. dmg does not read it.",
                $"0x0100 at offset {DiskCopy42MagicOffset}, disk name length {header[0]}.");
        }

        if (IndexOf(header, "bcem"u8) >= 0)
        {
            return new ImageFormatSignature(
                ImageFormat.Ndif,
                "NDIF image (Disk Copy 6)",
                "This looks like an NDIF image - the Disk Copy 6 format that came before UDIF. "
                + "dmg reads UDIF (.dmg) only. Convert it on a Mac with "
                + "'hdiutil convert old.img -format UDZO -o new.dmg'.",
                "'bcem' block map resource found in the file header.");
        }

        return null;
    }

    /// <summary>
    /// The formats that are not disk images at all. Every one of these is something
    /// a browser has been seen to hand a user in place of the .dmg they wanted.
    /// </summary>
    private static ImageFormatSignature? IdentifyForeignFormat(ReadOnlySpan<byte> header, long length)
    {
        if (Matches(header, 0, "xar!"u8))
        {
            return Foreign(
                "Apple installer package (xar archive)",
                "This is a xar archive - an Apple installer package (.pkg or .xip), not a disk "
                + "image. There is nothing here to mount.",
                "'xar!' at offset 0.");
        }

        if (Matches(header, 0, "PK"u8) && header.Length > 3 && header[2] is 0x03 or 0x05 or 0x07)
        {
            return Foreign(
                "ZIP archive",
                "This is a ZIP archive, not a disk image. Unzip it - the .dmg may be inside.",
                $"'PK' and 0x{ByteAt(header, 2):X2} at offset 0.");
        }

        if (Matches(header, 0, [0x1F, 0x8B]))
        {
            return Foreign(
                "gzip stream",
                "This is a gzip stream, not a disk image. Decompress it first; a .dmg.gz "
                + "unpacks to the .dmg dmg can read.",
                "gzip magic 0x1F8B at offset 0.");
        }

        if (Matches(header, 0, "BZh"u8) && header.Length > 3 && header[3] is >= (byte)'1' and <= (byte)'9')
        {
            return Foreign("bzip2 stream", "This is a bzip2 stream, not a disk image.", "'BZh' at offset 0.");
        }

        if (Matches(header, 0, [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00]))
        {
            return Foreign("xz stream", "This is an xz stream, not a disk image.", "xz magic at offset 0.");
        }

        if (Matches(header, 0, [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return Foreign("7-Zip archive", "This is a 7-Zip archive, not a disk image.", "'7z' magic at offset 0.");
        }

        if (Matches(header, 0, "Rar!"u8))
        {
            return Foreign("RAR archive", "This is a RAR archive, not a disk image.", "'Rar!' at offset 0.");
        }

        if (Matches(header, TarMagicOffset, "ustar"u8))
        {
            return Foreign(
                "tar archive",
                "This is a tar archive, not a disk image.",
                $"'ustar' at offset {TarMagicOffset}.");
        }

        if (Matches(header, Iso9660MagicOffset, "CD001"u8))
        {
            return Foreign(
                "ISO 9660 image",
                "This is an ISO 9660 optical disc image. Windows mounts .iso files natively - "
                + "right-click it and choose Mount, or use Mount-DiskImage in PowerShell.",
                $"'CD001' at offset 0x{Iso9660MagicOffset:X}.");
        }

        if (Matches(header, 0, "%PDF"u8))
        {
            return Foreign("PDF document", "This is a PDF document, not a disk image.", "'%PDF' at offset 0.");
        }

        if (Matches(header, 0, [0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            return Foreign("PNG image", "This is a PNG picture, not a disk image.", "PNG magic at offset 0.");
        }

        if (Matches(header, 0, [0xFF, 0xD8, 0xFF]))
        {
            return Foreign("JPEG image", "This is a JPEG picture, not a disk image.", "JPEG magic at offset 0.");
        }

        if (Matches(header, 0, "MZ"u8))
        {
            return Foreign(
                "Windows executable",
                "This is a Windows executable, not a disk image.",
                "'MZ' at offset 0.");
        }

        if (Matches(header, 0, [0x7F, (byte)'E', (byte)'L', (byte)'F']))
        {
            return Foreign("ELF binary", "This is an ELF binary, not a disk image.", "ELF magic at offset 0.");
        }

        if (Matches(header, 0, "bplist00"u8))
        {
            return Foreign(
                "binary property list",
                "This is a binary property list, not a disk image.",
                "'bplist00' at offset 0.");
        }

        if (Matches(header, 0, "<?xml"u8))
        {
            return Foreign(
                "XML document",
                "This is an XML document, not a disk image. A UDIF image contains XML, but it "
                + "does not start with it.",
                $"'<?xml' at offset 0, in a {length}-byte file.");
        }

        return null;
    }

    /// <summary>
    /// The MacBinary header check, kept honest: it is four weak conditions that are
    /// only convincing together, which is exactly how MacBinary itself is detected.
    /// </summary>
    /// <remarks>
    /// Byte 0 and byte 74 are reserved zeroes, byte 1 is a Pascal string length that
    /// must fit a 63-character HFS name, and MacBinary II onwards writes a version
    /// byte of 129 or more at offset 122 or the tag <c>mBIN</c> at offset 102. Any
    /// one of those alone would fire on half the files in the world.
    /// </remarks>
    private static bool LooksLikeMacBinary(ReadOnlySpan<byte> header)
    {
        if (header.Length <= MacBinaryVersionOffset)
        {
            return false;
        }

        if (header[0] != 0 || header[1] is 0 or > 63 || header[74] != 0)
        {
            return false;
        }

        return header[MacBinaryVersionOffset] >= 129 || Matches(header, MacBinary3TagOffset, "mBIN"u8);
    }

    private static ImageFormatSignature SparseBundle(string bundlePath, string detail) =>
        new(
            ImageFormat.SparseBundle,
            "sparse bundle (.sparsebundle)",
            $"'{bundlePath}' is a sparse bundle: a folder of band files rather than a single "
            + "image. dmg reads single-file UDIF and raw images. Convert it on a Mac with "
            + "'hdiutil convert bundle.sparsebundle -format UDZO -o image.dmg'.",
            detail);

    private static ImageFormatSignature Ndif(
        string name,
        string phrase,
        string detail,
        ReadOnlySpan<byte> header) =>
        new(
            ImageFormat.Ndif,
            name,
            $"This looks like {phrase}, not a disk image dmg can read. Files like this normally "
            + "carry an old Disk Copy or NDIF image in their resource fork"
            + (IndexOf(header, "bcem"u8) >= 0 ? ", and this one does." : ".")
            + " Convert it on a Mac with 'hdiutil convert old.img -format UDZO -o new.dmg'.",
            detail);

    private static ImageFormatSignature Foreign(string name, string message, string detail) =>
        new(ImageFormat.Foreign, name, message, detail);

    /// <summary>
    /// True when <paramref name="magic"/> sits at <paramref name="offset"/>. A header
    /// too short to hold it is a non-match, never an exception: the header is however
    /// much of the file there was.
    /// </summary>
    private static bool Matches(ReadOnlySpan<byte> header, int offset, ReadOnlySpan<byte> magic) =>
        BigEndian.TrySlice(header, offset, magic.Length, out ReadOnlySpan<byte> window)
        && window.SequenceEqual(magic);

    /// <summary>The first occurrence of <paramref name="needle"/>, or -1.</summary>
    private static int IndexOf(ReadOnlySpan<byte> header, ReadOnlySpan<byte> needle) =>
        header.IndexOf(needle);

    private static int ByteAt(ReadOnlySpan<byte> header, int offset) =>
        offset >= 0 && offset < header.Length ? header[offset] : 0;

    /// <summary>
    /// Renders the first bytes of a file as hex and printable ASCII, for the detail
    /// line of a refusal nobody could name.
    /// </summary>
    /// <param name="header">The bytes read from the front of the file.</param>
    /// <param name="count">How many to show. Clamped to what is there.</param>
    public static string DescribeLeadingBytes(ReadOnlySpan<byte> header, int count = 8)
    {
        if (header.IsEmpty || count <= 0)
        {
            return "the file is empty";
        }

        int shown = Math.Min(count, header.Length);
        string[] hex = new string[shown];
        char[] ascii = new char[shown];

        for (int index = 0; index < shown; index++)
        {
            byte value = header[index];
            hex[index] = value.ToString("X2", CultureInfo.InvariantCulture);
            ascii[index] = value is >= (byte)' ' and <= (byte)'~' ? (char)value : '.';
        }

        return $"first {shown} bytes: {string.Join(' ', hex)} '{new string(ascii)}'";
    }
}
