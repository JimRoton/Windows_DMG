using System.Security.Cryptography;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Tests.Crypto;

namespace Dmg.Core.Tests.Containers;

/// <summary>
/// The probe chain is the first thing every command does, and the last chance to
/// tell a user something useful about a file that is not what they thought it was.
/// These tests hold it to both halves of that: it must never mistake one format for
/// another, and it must never refuse a file without naming it.
/// </summary>
public sealed class ImageFormatProbeChainTests
{
    private const string EncrCdsa = "encrcdsa";

    [Fact]
    public void TheDefaultChainIsEncryptedThenUdifThenRawThenTerminal()
    {
        // The order is load-bearing, not incidental - see the class remarks on
        // ImageFormatProbeChain - so it is asserted rather than assumed.
        string[] names = [.. ImageFormatProbeChain.Default.Probes.Select(probe => probe.Name)];
        string[] expected = ["encrypted", "UDIF", "raw", "unknown"];

        Assert.Equal(expected, names);
        Assert.True(ImageFormatProbeChain.Default.Probes[^1].IsTerminal);
        Assert.All(
            ImageFormatProbeChain.Default.Probes.Take(3),
            probe => Assert.False(probe.IsTerminal));
    }

    [Fact]
    public void AChainWithNoTerminalIsAWiringBugAndThrows()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() =>
            new ImageFormatProbeChain([UdifImageProbe.Instance, RawImageProbe.Instance]));

        Assert.Contains("terminal", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyChainThrows() =>
        Assert.Throws<ArgumentException>(() => new ImageFormatProbeChain([]));

    [Fact]
    public void ATerminalThatIsNotLastThrows() =>
        Assert.Throws<ArgumentException>(() =>
            new ImageFormatProbeChain([UnknownImageProbe.Instance, RawImageProbe.Instance]));

    [Fact]
    public void TwoTerminalsThrow() =>
        Assert.Throws<ArgumentException>(() =>
            new ImageFormatProbeChain([UnknownImageProbe.Instance, UnknownImageProbe.Instance]));

    [Fact]
    public void ANullProbeInTheChainThrows() =>
        Assert.Throws<ArgumentNullException>(() =>
            new ImageFormatProbeChain([null!, UnknownImageProbe.Instance]));

    // ---------------------------------------------------------------- encrypted

    [Fact]
    public void AnEncryptedImageIsRefusedWithTheDecryptionExitCode()
    {
        DmgError error = Refused(WithHeader(EncrCdsa, 4096));

        Assert.Equal(DmgExitCode.DecryptionFailed, error.Code);
        Assert.Contains("encrypted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("passphrase", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncryptionIsCheckedBeforeUdifSoCiphertextIsNeverCalledCorrupt()
    {
        // The failure mode this ordering exists to prevent: an encrypted file whose
        // ciphertext happens to end in something the koly parser would take a view
        // on. Told "your image is damaged", a user goes looking for a bad download;
        // told "your image is encrypted", they go looking for the passphrase.
        byte[] file = UdifFile(4096);
        Ascii(EncrCdsa).CopyTo(file, 0);

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.DecryptionFailed, error.Code);
    }

    [Fact]
    public void TheVersionOneTrailingMagicIsRecognisedToo()
    {
        // Version 1 put its header at the end of the file. It is a museum piece, but
        // a museum piece with a name is better than an unrecognised file - and it is
        // UnsupportedFormat rather than DecryptionFailed: no passphrase this tool
        // could be handed would ever open a layout it has never parsed, so it must
        // not be told "try again".
        byte[] file = new byte[2048];
        Ascii("cdsaencr").CopyTo(file, 2048 - 256);

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("version 1", error.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncrcdsaAnywhereButOffsetZeroIsNotAnEncryptedImage()
    {
        // The magic is a header, not a substring. A raw disk that happens to contain
        // the word must not be refused as encrypted.
        byte[] file = new byte[2048];
        Ascii(EncrCdsa).CopyTo(file, 600);

        ImageFormatDetection detection = Identified(file);

        Assert.Equal(ImageFormat.Raw, detection.Format);
    }

    [Fact]
    public void TheEncryptedProbeRefusesToDescribeAFileItDidNotClaim()
    {
        // Calling Describe without Recognises is a caller bug, and it is reported as
        // one rather than producing a confident answer about a file nobody checked.
        ImageProbeContext image = ImageProbeContextTests.Context(new byte[1024]);

        Result<ImageFormatDetection> result = EncryptedImageProbe.Instance.Describe(image);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    // ---------------------------------------------------- passphrase-aware identify

    [Fact]
    public void ACorrectPassphraseOpensAnEncrcdsaImageEndToEnd()
    {
        // exfat-enc256.dmg and exfat-enc128.dmg are made exactly this way -
        // hdiutil convert -format UDRW -encryption - which wraps a flat sector
        // stream with no koly trailer at all. A correct passphrase has to reach the
        // raw probe on the plaintext, not fail because there is no UDIF inside.
        byte[] plaintext = Payload(4096);
        SyntheticImage image = SyntheticImage.Create("right-passphrase", plaintext: plaintext);

        using MemoryStream file = image.OpenRead();
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            file,
            "right-passphrase"u8);

        Assert.True(result.TryGetValue(out ImageFormatDetection? detection), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(ImageFormat.Raw, detection.Format);
        Assert.Equal((ulong)(plaintext.Length / 512), detection.SectorCount);
    }

    [Fact]
    public void ACorrectPassphraseOverAGenuineKolyTrailerIsConfirmedAsUdif()
    {
        // The authoritative half of S4.7: when the plaintext really is UDIF-shaped -
        // the general, documented case, just not the one tools/make-fixtures.sh
        // happens to use - the koly trailer is what confirms the passphrase was
        // right, by the same check KolyTrailer.Read always makes.
        byte[] plaintext = UdifFile(4096, koly => koly.SectorCount = 8);
        SyntheticImage image = SyntheticImage.Create("right-passphrase", plaintext: plaintext);

        using MemoryStream file = image.OpenRead();
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            file,
            "right-passphrase"u8);

        Assert.True(result.TryGetValue(out ImageFormatDetection? detection), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(ImageFormat.Udif, detection.Format);
        Assert.Equal(8UL, detection.SectorCount);
    }

    [Fact]
    public void AWrongPassphraseOverTheSameKolyShapedPlaintextIsStillDecryptionFailed()
    {
        // Same plaintext as the test above, wrong passphrase: the fast padding
        // check inside the key unwrap has to refuse this before the koly trailer is
        // ever in play, so it never has the chance to be misread as corruption.
        byte[] plaintext = UdifFile(4096, koly => koly.SectorCount = 8);
        SyntheticImage image = SyntheticImage.Create("right-passphrase", plaintext: plaintext);

        using MemoryStream file = image.OpenRead();
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            file,
            "wrong-passphrase"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
    }

    [Fact]
    public void AWrongPassphraseIsDecryptionFailedNeverCorruptImage()
    {
        // The fast check - PKCS#7 padding on the unwrapped key blob - catches almost
        // every wrong passphrase before a single payload block is ever touched. This
        // is the exit code a caller is meant to branch on; it must never surface as
        // 9, which would send them looking for a bad download instead of a typo.
        SyntheticImage image = SyntheticImage.Create("right-passphrase", plaintext: Payload(4096));

        using MemoryStream file = image.OpenRead();
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            file,
            "wrong-passphrase"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
    }

    [Fact]
    public void APassphraseIsIgnoredForAnOrdinaryUdifFile()
    {
        // The overload has to be safe to call unconditionally - a caller should not
        // need to know a file is encrypted before offering it a passphrase.
        byte[] file = UdifFile(4096, koly => koly.SectorCount = 8);

        using MemoryStream stream = new(file, writable: false);
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            stream,
            "this is never looked at"u8);

        Assert.True(result.TryGetValue(out ImageFormatDetection? detection), result.Ok ? "" : result.Error.ToString());
        Assert.Equal(ImageFormat.Udif, detection.Format);
    }

    [Fact]
    public void APassphraseCannotOpenTheLegacyVersionOneLayout()
    {
        // No passphrase reaches a v1 header at all - IsEncrcdsaV2 says no before any
        // key material is touched - so the refusal is exactly the unencrypted one:
        // named, and UnsupportedFormat, not DecryptionFailed.
        byte[] file = new byte[2048];
        Ascii("cdsaencr").CopyTo(file, 2048 - 256);

        using MemoryStream stream = new(file, writable: false);
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            stream,
            "irrelevant"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AKeyUnlockedByACertificateOrKeychainIsRefusedByNameNotAsAWrongPassphrase()
    {
        // S4.8's other refusal-by-name: a key-pointer entry whose type is not 1
        // (EncryptedDmgHeader.PassphraseKeyType) means the image is unlocked by a
        // certificate or a keychain entry, not a passphrase - already caught at
        // header-parse time since S4.1. This confirms it still reaches the caller
        // as UnsupportedFormat through the passphrase-aware entry point, and is
        // never misreported as a wrong passphrase just because one was offered.
        EncryptedHeaderBuilder builder = new() { KeyType = 2 };

        using MemoryStream stream = new(builder.ToFile(new byte[builder.DataSize]), writable: false);
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(
            stream,
            "any-passphrase-at-all"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    private static byte[] Payload(int length)
    {
        byte[] payload = new byte[length];
        RandomNumberGenerator.Fill(payload);
        return payload;
    }

    // --------------------------------------------------------------------- UDIF

    [Fact]
    public void AUdifImageIsIdentifiedFromItsKolyTrailer()
    {
        ImageFormatDetection detection = Identified(UdifFile(4096, koly => koly.SectorCount = 128));

        Assert.Equal(ImageFormat.Udif, detection.Format);
        Assert.Equal("UDIF", detection.ProbeName);
        Assert.Equal(128ul, detection.SectorCount);
        Assert.Equal(4096 + KolyTrailer.Size, detection.FileLength);
        Assert.NotNull(detection.Trailer);
        Assert.Equal(4u, detection.Trailer.Version);
        Assert.Contains("UDIF", detection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealHdiutilTrailerIsIdentified()
    {
        // The bytes hdiutil actually wrote, at the end of a file of the length it
        // actually produced. Everything else here is synthetic; this one is not.
        byte[] file = new byte[RealImageSamples.FileLength];
        RealImageSamples.Koly().CopyTo(file, file.Length - KolyTrailer.Size);

        ImageFormatDetection detection = Identified(file);

        Assert.Equal(ImageFormat.Udif, detection.Format);
        Assert.Equal(67647ul, detection.SectorCount);
        Assert.True(detection.DecodedLengthInBytes().TryGetValue(out ulong bytes));
        Assert.Equal(67647ul * 512, bytes);
    }

    [Fact]
    public void ADamagedKolyIsCorruptAndIsNeverQuietlyRereadAsRaw()
    {
        // The whole reason recognition and description are separate. This file is a
        // whole number of sectors, so the raw probe would take it - and a mounted
        // volume built from a broken image is far worse than an error.
        byte[] file = UdifFile(4096, koly => koly.HeaderSize = 64);

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.CorruptImage, error.Code);
        Assert.Contains("header size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKolyPointingItsXmlOutsideTheFileIsCorrupt()
    {
        byte[] file = UdifFile(4096, koly =>
        {
            koly.XmlOffset = 4096;
            koly.XmlLength = 8192;
        });

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.CorruptImage, error.Code);
    }

    [Fact]
    public void AKolyOfAnotherVersionIsUnsupportedAndSaysWhichVersion()
    {
        byte[] file = UdifFile(4096, koly => koly.Version = 5);

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("version 5", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileTooShortToHoldATrailerIsNotUdifHoweverItStarts()
    {
        byte[] file = Ascii("koly");

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.DoesNotContain("corrupt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATruncatedUdifLosesItsTrailerAndIsRefusedNotMisread()
    {
        // Cutting the last 100 bytes off an image takes the trailer with it. What is
        // left is no longer a whole number of sectors, which is exactly the clue the
        // terminal turns into "the download was cut short".
        byte[] whole = UdifFile(4096);
        byte[] truncated = whole[..(whole.Length - 100)];

        DmgError error = Refused(truncated);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("koly", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cut short", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------- raw

    [Fact]
    public void AWholeNumberOfSectorsWithNoContainerIsRaw()
    {
        ImageFormatDetection detection = Identified(new byte[512 * 20]);

        Assert.Equal(ImageFormat.Raw, detection.Format);
        Assert.Equal("raw", detection.ProbeName);
        Assert.Equal(20ul, detection.SectorCount);
        Assert.Null(detection.Trailer);
        Assert.Contains("Raw", detection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleSectorIsTheSmallestRawImage()
    {
        ImageFormatDetection detection = Identified(new byte[512]);

        Assert.Equal(ImageFormat.Raw, detection.Format);
        Assert.Equal(1ul, detection.SectorCount);
    }

    [Fact]
    public void OneByteShortOfASectorIsNotRaw()
    {
        DmgError error = Refused(new byte[511]);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("512", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRawProbeDeclinesAnythingTheSignatureTableCanName()
    {
        // A ZIP that happens to divide by 512 is the case that makes the raw probe
        // dangerous, so it is the case that is tested.
        byte[] zip = WithHeader("PK\u0003\u0004", 512 * 4);

        Assert.False(RawImageProbe.Instance.Recognises(ImageProbeContextTests.Context(zip)));

        DmgError error = Refused(zip);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("ZIP", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRawProbeRefusesAPartSectorFileEvenWhenCalledDirectly()
    {
        ImageProbeContext image = ImageProbeContextTests.Context(new byte[700]);

        Result<ImageFormatDetection> result = RawImageProbe.Instance.Describe(image);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    // ----------------------------------------------------------- naming the rest

    [Theory]
    [MemberData(nameof(NamedFormats))]
    public void TheTerminalNamesWhatItFound(byte[] header, string expected)
    {
        byte[] file = new byte[512 * 8];
        header.CopyTo(file, 0);

        DmgError error = Refused(file);

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<byte[], string> NamedFormats() => new()
    {
        { Ascii("xar!"), "installer package" },
        { Ascii("PK\u0003\u0004"), "ZIP" },
        { [0x1F, 0x8B, 0x08, 0x00], "gzip" },
        { Ascii("BZh9"), "bzip2" },
        { [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], "xz" },
        { [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], "7-Zip" },
        { Ascii("Rar!"), "RAR" },
        { Ascii("%PDF-1.7"), "PDF" },
        { [0x89, 0x50, 0x4E, 0x47], "PNG" },
        { [0xFF, 0xD8, 0xFF, 0xE0], "JPEG" },
        { Ascii("MZ"), "Windows executable" },
        { [0x7F, 0x45, 0x4C, 0x46], "ELF" },
        { Ascii("bplist00"), "property list" },
        { Ascii("<?xml version=\"1.0\"?>"), "XML" },
        { Ascii("sprs"), "sparse image" },
    };

    [Fact]
    public void AnNdifBlockMapIsNamedAsNdif()
    {
        // 'bcem' is the resource NDIF keeps its block map in, and finding it is the
        // only positive evidence an NDIF ever gives on a non-Mac filesystem.
        byte[] file = new byte[512 * 8];
        Ascii("bcem").CopyTo(file, 300);

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("NDIF", error.Message, StringComparison.Ordinal);
        Assert.Contains("hdiutil convert", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppleDoubleWrapperIsNamedAsAResourceForkCarryingAnOldImage()
    {
        byte[] file = new byte[512 * 8];
        new byte[] { 0x00, 0x05, 0x16, 0x07 }.CopyTo(file, 0);

        DmgError error = Refused(file);

        Assert.Contains("AppleDouble", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMacBinaryWrapperIsNamed()
    {
        byte[] file = new byte[512 * 8];
        file[1] = 12;              // Pascal-string name length, 1..63.
        file[122] = 129;           // MacBinary II version byte.

        DmgError error = Refused(file);

        Assert.Contains("MacBinary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiskCopyFourTwoImageIsNamed()
    {
        byte[] file = new byte[512 * 8];
        file[0] = 10;
        file[82] = 0x01;

        DmgError error = Refused(file);

        Assert.Contains("Disk Copy 4.2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATarArchiveIsNamedFromItsOffsetTwoFiftySevenMagic()
    {
        byte[] file = new byte[512 * 8];
        Ascii("ustar").CopyTo(file, 257);

        Assert.Contains("tar", Refused(file).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnIsoIsNamedFromItsVolumeDescriptorThirtyTwoKibibytesIn()
    {
        // The deepest signature this tool looks for, and the reason the header
        // window is 64 KiB rather than a few hundred bytes.
        byte[] file = new byte[64 * 1024];
        Ascii("CD001").CopyTo(file, 0x8001);

        DmgError error = Refused(file);

        Assert.Contains("ISO 9660", error.Message, StringComparison.Ordinal);
        Assert.Contains("Mount-DiskImage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileInsideASparseBundleIsNamedAsTheBundle()
    {
        byte[] file = Ascii("<?xml version=\"1.0\"?>");

        DmgError error = Refused(file, "/Users/jim/Images/backup.sparsebundle/Info.plist");

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("sparse bundle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backup.sparsebundle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASparseBundlePathBeatsAnyBytesInsideIt()
    {
        // A band file inside a bundle is raw sectors, and calling it a raw image
        // would be technically true and completely useless.
        byte[] band = new byte[512 * 8];

        DmgError error = Refused(band, @"D:\images\photos.sparsebundle\bands\0");

        Assert.Contains("sparse bundle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyFileSaysSo()
    {
        DmgError error = Refused([]);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnrecognisableFileStillCarriesItsFirstBytes()
    {
        byte[] file = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];

        DmgError error = Refused(file);

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("DE AD BE EF", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("7 bytes", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTerminalNeverSaysNotADmg()
    {
        // The banned sentence, asserted rather than merely intended. Every one of
        // these files is refused; none of the refusals may be that useless phrase.
        byte[][] files =
        [
            [],
            [0xDE, 0xAD],
            WithHeader("xar!", 512 * 4),
            WithHeader("PK\u0003\u0004", 512 * 4),
            new byte[511],
        ];

        foreach (byte[] file in files)
        {
            string message = Refused(file).Message;

            Assert.DoesNotContain("not a DMG", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not a dmg file", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // -------------------------------------------------------------- paths and IO

    [Fact]
    public void IdentifyReadsAFileFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dmg-probe-{Guid.NewGuid():N}.dmg");

        try
        {
            File.WriteAllBytes(path, UdifFile(1024, koly => koly.SectorCount = 8));

            Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(path);

            Assert.True(result.TryGetValue(out ImageFormatDetection? detection), result.Ok ? "" : result.Error.ToString());
            Assert.Equal(ImageFormat.Udif, detection.Format);
            Assert.Equal(8ul, detection.SectorCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileIsAUsageErrorNotAFormatError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dmg-missing-{Guid.NewGuid():N}.dmg");

        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(path);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
        Assert.Contains("no file at", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASparseBundleDirectoryIsIdentifiedWithoutOpeningAnything()
    {
        // The case that has no stream at all: a bundle is a folder, so the probes
        // never get to run and the chain has to answer for it directly.
        string path = Path.Combine(Path.GetTempPath(), $"dmg-{Guid.NewGuid():N}.sparsebundle");
        Directory.CreateDirectory(path);

        try
        {
            Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(path);

            Assert.False(result.Ok);
            Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
            Assert.Contains("sparse bundle", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public void APlainDirectoryIsAUsageError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dmg-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        try
        {
            Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(path);

            Assert.False(result.Ok);
            Assert.Equal(DmgExitCode.UsageError, result.Error.Code);
            Assert.Contains("folder", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public void AnEmptyPathIsARejectedArgument() =>
        Assert.Throws<ArgumentException>(() => ImageFormatProbeChain.Default.Identify("   "));

    [Fact]
    public void AStreamThatCannotSeekFailsBeforeAnyProbeRuns()
    {
        using MemoryStream inner = new(new byte[2048]);
        using UnreadableStream stream = new(inner);

        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs the default chain and asserts it succeeded.</summary>
    private static ImageFormatDetection Identified(byte[] file, string? path = null)
    {
        using MemoryStream stream = new(file, writable: false);
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(stream, path);

        Assert.True(result.TryGetValue(out ImageFormatDetection? detection), result.Ok ? "" : result.Error.ToString());

        return detection;
    }

    /// <summary>Runs the default chain and asserts it refused, returning the refusal.</summary>
    private static DmgError Refused(byte[] file, string? path = null)
    {
        using MemoryStream stream = new(file, writable: false);
        Result<ImageFormatDetection> result = ImageFormatProbeChain.Default.Identify(stream, path);

        Assert.False(result.Ok, result.Ok ? $"Expected a refusal, got {result.GetValueOrDefault()}" : "");

        return result.Error;
    }

    /// <summary>A file of <paramref name="length"/> bytes beginning with an ASCII magic.</summary>
    private static byte[] WithHeader(string magic, int length)
    {
        byte[] file = new byte[length];
        Ascii(magic).CopyTo(file, 0);
        return file;
    }

    private static byte[] Ascii(string text)
    {
        byte[] bytes = new byte[text.Length];

        for (int index = 0; index < text.Length; index++)
        {
            bytes[index] = (byte)text[index];
        }

        return bytes;
    }

    /// <summary>
    /// A payload of the given size with a valid koly trailer glued to the end of it.
    /// </summary>
    private static byte[] UdifFile(int payloadBytes, Action<UdifBuilder.Koly>? configure = null)
    {
        UdifBuilder.Koly koly = new()
        {
            SectorCount = 8,
            DataForkOffset = 0,
            DataForkLength = (ulong)payloadBytes,
            XmlOffset = (ulong)payloadBytes,
            XmlLength = 0,
        };

        configure?.Invoke(koly);

        byte[] file = new byte[payloadBytes + KolyTrailer.Size];
        koly.ToArray().CopyTo(file, payloadBytes);

        return file;
    }

    private sealed class UnreadableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
