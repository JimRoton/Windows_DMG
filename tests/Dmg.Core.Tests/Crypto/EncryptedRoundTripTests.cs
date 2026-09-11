using System.Text;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Imaging;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// S4.9 and S4.10: the whole of E4's decryption, exercised against real
/// <c>hdiutil</c> output rather than synthetic bytes. The other Crypto test files
/// prove the arithmetic; this one proves the arithmetic was applied to a real
/// <c>encrcdsa</c> file and produced what Apple's own decryptor produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ground truth comes from <c>hdiutil attach -stdinpass</c>, never from this
/// codebase.</b> <c>tools/make-manifest.sh</c> unlocks each encrypted fixture
/// itself and <c>dd</c>s the raw device, so <see cref="FixtureRecord.DecodedSha256"/>
/// is Apple's answer, not this reader's. Comparing our decrypt against it is a
/// differential test, not a pinned constant - see <see cref="FixtureCorpus"/> for
/// why nothing here is a committed hash.
/// </para>
/// <para>
/// <b>Two shapes of encrypted image, and the difference decides the assertion.</b>
/// <c>hdiutil convert -format UDRW -encryption</c> wraps a flat sector stream with
/// no <c>koly</c> trailer at all (see <see cref="EncryptedBlockStreamTests.TheDecoratorIsInvisibleToTheContainerReaderAboveIt"/>,
/// merged with S4.5), so for <c>exfat-enc256.dmg</c> and <c>exfat-enc128.dmg</c>
/// the decrypted payload already <em>is</em> the decoded disk: the round trip is
/// "decrypt, then hash". <c>exfat-enc-udzo.dmg</c> is the other shape - the
/// decrypted payload is a genuine UDIF container whose chunks still have to be
/// inflated, so it is the only fixture that exercises
/// <c>EncryptedBlockStream</c> and <c>DmgBlockStream</c> stacked, which is the
/// arrangement docs/04 section 1 describes. Mixing the two shapes into one theory
/// would assert the wrong thing for whichever one it was not written for.
/// </para>
/// <para>
/// <b>Absence is a skip, not a pass.</b> <c>fixtures/generated/</c> is gitignored
/// and <c>hdiutil</c> is macOS-only, so a clean checkout and every non-Mac CI
/// runner simply has no corpus. Those runs report as skipped rather than green,
/// so a machine that decrypted nothing cannot be mistaken for one that did.
/// </para>
/// </remarks>
public sealed class EncryptedRoundTripTests
{
    [SkippableTheory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void ADecryptedFixtureMatchesApplesGroundTruth(string name)
    {
        // Only the two flat UDRW fixtures belong here: their decrypted bytes are
        // the decoded disk. exfat-enc-udzo's are a container - see the class
        // remarks, and TheEncryptedUdzoFixtureDecodesThroughBothLayers below.
        EncryptedFixtures.SkipUnless(name);

        FixtureRecord record = EncryptedFixtures.Record(name);

        using FileStream file = File.OpenRead(record.Path);
        Result<EncryptedBlockStream> opened = EncryptedBlockStream.Open(
            file,
            EncryptedFixtures.PassphraseBytes(),
            leaveOpen: true);

        Assert.True(opened.TryGetValue(out EncryptedBlockStream? stream), opened.Ok ? "" : opened.Error.ToString());

        using (stream)
        {
            Assert.True(
                stream.Length >= record.DecodedSize,
                $"{name}: decrypted to {stream.Length} bytes, but the manifest's ground truth "
                + $"covers {record.DecodedSize}.");

            (string hash, bool tailIsZero) = DmgBlockStreamFixtureTests.HashPrefix(stream, record.DecodedSize);

            Assert.True(
                tailIsZero,
                $"{name}: bytes past the manifest's decoded_size are not all zero, so the "
                + "ground-truth hash cannot cover the rest of the disk.");

            Assert.Equal(record.DecodedSha256, hash);
        }
    }

    [SkippableFact]
    public void Enc256DecodesToTheSameBytesAsPlainZlib()
    {
        // The manifest's own claim, restated as an assertion: the encryption
        // wrapper and the zlib codec are two different skins over one shared
        // source image, so Apple's decoder must produce the same bytes from both.
        EncryptedFixtures.SkipUnless("exfat-enc256.dmg", "exfat-zlib.dmg");

        FixtureRecord encrypted = EncryptedFixtures.Record("exfat-enc256.dmg");
        FixtureRecord zlib = EncryptedFixtures.Record("exfat-zlib.dmg");

        Assert.Equal(zlib.DecodedSha256, encrypted.DecodedSha256);
    }

    [SkippableFact]
    public void Enc128DecodesToTheSameBytesAsPlainZlib()
    {
        EncryptedFixtures.SkipUnless("exfat-enc128.dmg", "exfat-zlib.dmg");

        FixtureRecord encrypted = EncryptedFixtures.Record("exfat-enc128.dmg");
        FixtureRecord zlib = EncryptedFixtures.Record("exfat-zlib.dmg");

        Assert.Equal(zlib.DecodedSha256, encrypted.DecodedSha256);
    }

    [SkippableTheory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    [InlineData("exfat-enc-udzo.dmg")]
    public void AWrongPassphraseExitsFourAtTheLowLevelApi(string name)
    {
        // Payload shape is irrelevant here: a wrong passphrase fails in the key
        // unwrap, long before anything looks for a container.
        EncryptedFixtures.SkipUnless(name);

        using FileStream file = File.OpenRead(EncryptedFixtures.Path(name));
        Result<EncryptedBlockStream> opened = EncryptedBlockStream.Open(
            file,
            "definitely-not-the-passphrase"u8);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, opened.Error.Code);
    }

    [SkippableTheory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    [InlineData("exfat-enc-udzo.dmg")]
    public void AWrongPassphraseExitsFourThroughTheProbeChainToo(string name)
    {
        // The same guarantee, from the entry point a caller who does not know the
        // image is encrypted actually uses: ImageFormatProbeChain.Identify.
        EncryptedFixtures.SkipUnless(name);

        using FileStream file = File.OpenRead(EncryptedFixtures.Path(name));
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            "definitely-not-the-passphrase"u8,
            name);

        Assert.False(identified.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, identified.Error.Code);
    }

    [SkippableTheory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void ACorrectPassphraseOpensEndToEndThroughTheProbeChain(string name)
    {
        // S4.7/S4.8's "also": the probe chain now opens an encrcdsa image with a
        // correct passphrase, rather than always refusing it as E4-not-yet-done.
        EncryptedFixtures.SkipUnless(name);

        FixtureRecord record = EncryptedFixtures.Record(name);

        using FileStream file = File.OpenRead(record.Path);
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            EncryptedFixtures.PassphraseBytes(),
            name);

        Assert.True(
            identified.TryGetValue(out ImageFormatDetection? detection),
            identified.Ok ? "" : identified.Error.ToString());

        // These two fixtures decrypt to a flat sector stream - see the class
        // remarks - so "opens end to end" lands on the raw probe, not UDIF.
        Assert.Equal(ImageFormat.Raw, detection.Format);
    }

    [SkippableFact]
    public void TheEncryptedUdzoFixtureDecryptsToARealUdifContainer()
    {
        // S4.10. Everything above wraps a flat stream, so until this fixture
        // existed nothing had ever decrypted its way *into* a container. The probe
        // chain has to see past the encryption and find a koly trailer, which is
        // the case the two fixtures above cannot produce.
        EncryptedFixtures.SkipUnless("exfat-enc-udzo.dmg");

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc-udzo.dmg"));
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            EncryptedFixtures.PassphraseBytes(),
            "exfat-enc-udzo.dmg");

        Assert.True(
            identified.TryGetValue(out ImageFormatDetection? detection),
            identified.Ok ? "" : identified.Error.ToString());

        Assert.Equal(ImageFormat.Udif, detection.Format);
    }

    [SkippableFact]
    public void TheEncryptedUdzoFixtureDecodesThroughBothLayersToApplesGroundTruth()
    {
        // S4.10, and the assertion the story exists for: decrypt *and* inflate.
        // EncryptedBlockStream underneath, DmgBlockStream stacked on top of it,
        // hashed against what hdiutil itself produced from the same image. Either
        // layer being wrong breaks the hash, and no other fixture can catch a bug
        // that only appears when the two are composed.
        EncryptedFixtures.SkipUnless("exfat-enc-udzo.dmg");

        FixtureRecord record = EncryptedFixtures.Record("exfat-enc-udzo.dmg");

        using FileStream file = File.OpenRead(record.Path);
        Result<EncryptedBlockStream> opened = EncryptedBlockStream.Open(
            file,
            EncryptedFixtures.PassphraseBytes(),
            leaveOpen: true);

        Assert.True(opened.TryGetValue(out EncryptedBlockStream? decrypted), opened.Ok ? "" : opened.Error.ToString());

        using (decrypted)
        {
            using DmgBlockStream stream = DmgBlockStreamFixtureTests.Open(decrypted);

            Assert.True(
                stream.Length >= record.DecodedSize,
                $"exfat-enc-udzo.dmg: decoded to {stream.Length} bytes, but the manifest's "
                + $"ground truth covers {record.DecodedSize}.");

            (string hash, bool tailIsZero) = DmgBlockStreamFixtureTests.HashPrefix(stream, record.DecodedSize);

            Assert.True(
                tailIsZero,
                "exfat-enc-udzo.dmg: bytes past the manifest's decoded_size are not all zero, "
                + "so the ground-truth hash cannot cover the rest of the disk.");

            Assert.Equal(record.DecodedSha256, hash);
        }
    }

    [SkippableFact]
    public void TheEncryptedUdzoFixtureDecodesToTheSameBytesAsPlainZlib()
    {
        // The cross-fixture equality, extended to the stacked case: encryption over
        // zlib must land on the same sectors as zlib alone.
        EncryptedFixtures.SkipUnless("exfat-enc-udzo.dmg", "exfat-zlib.dmg");

        FixtureRecord encrypted = EncryptedFixtures.Record("exfat-enc-udzo.dmg");
        FixtureRecord zlib = EncryptedFixtures.Record("exfat-zlib.dmg");

        Assert.Equal(zlib.DecodedSha256, encrypted.DecodedSha256);
    }

    [SkippableFact]
    public void TheTypedPassphraseWorksTooViaTheTrailingNewlineRetry()
    {
        // EncryptedFixtures.Passphrase already carries the newline hdiutil -stdinpass
        // keyed the image off; this proves the retry path a real typed passphrase
        // takes - see EncryptedDmgKeys.Unwrap - reaches the same success end to end.
        EncryptedFixtures.SkipUnless("exfat-enc256.dmg");

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            Encoding.UTF8.GetBytes(EncryptedFixtures.TypedPassphrase));

        Assert.True(identified.Ok, identified.Ok ? "" : identified.Error.ToString());
    }
}
