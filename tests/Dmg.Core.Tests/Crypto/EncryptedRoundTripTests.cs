using System.Text;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Tests.Codecs;
using Dmg.Core.Tests.Imaging;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// S4.9: the whole of E4's decryption, exercised against real <c>hdiutil</c>
/// output rather than synthetic bytes. The other Crypto test files prove the
/// arithmetic; this one proves the arithmetic was applied to a real
/// <c>encrcdsa</c> file and produced what Apple's own decryptor produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ground truth comes from <c>hdiutil attach -stdinpass</c>, never from this
/// codebase.</b> <c>tools/make-manifest.sh</c> unlocks each encrypted fixture
/// itself and <c>dd</c>s the raw device, so <see cref="FixtureRecord.DecodedSha256"/>
/// for <c>exfat-enc256.dmg</c> and <c>exfat-enc128.dmg</c> is Apple's answer, not
/// this reader's. Comparing our decrypt against it is a differential test, not a
/// pinned constant - see <see cref="FixtureCorpus"/> for why nothing here is a
/// committed hash.
/// </para>
/// <para>
/// <b>There is no UDIF container to parse inside these two.</b> That would be the
/// tidy story - decrypt, then hand the plaintext to <see cref="Imaging.DmgImage"/> -
/// but it is not what <c>tools/make-fixtures.sh</c> actually produced.
/// <c>hdiutil convert -format UDRW -encryption</c> wraps a flat sector stream with
/// no <c>koly</c> trailer at all (see <see cref="EncryptedBlockStreamTests.TheDecoratorIsInvisibleToTheContainerReaderAboveIt"/>,
/// merged with S4.5), so the decrypted payload for these two fixtures already
/// <em>is</em> the decoded disk - the same bytes <c>exfat-raw.dmg</c>,
/// <c>exfat-zlib.dmg</c> and <c>exfat-udro.dmg</c> decode to from their own
/// containers. The round trip here is therefore "decrypt, then hash", and the
/// cross-fixture equality below is what stands in for "parse the UDIF container
/// inside it" for a payload that never had one.
/// </para>
/// <para>
/// <b>Absence is not failure.</b> <c>fixtures/generated/</c> is gitignored and
/// <c>hdiutil</c> is macOS-only, so a clean checkout and every non-Mac CI runner
/// simply has no corpus. Those runs report that and pass, rather than going red
/// over something the machine could never have had.
/// </para>
/// </remarks>
public sealed class EncryptedRoundTripTests
{
    [Theory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void ADecryptedFixtureMatchesApplesGroundTruth(string name)
    {
        if (EncryptedFixtures.Skip(out string? why, name))
        {
            Assert.True(true, why);
            return;
        }

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

    [Fact]
    public void Enc256DecodesToTheSameBytesAsPlainZlib()
    {
        // The manifest's own claim, restated as an assertion: the encryption
        // wrapper and the zlib codec are two different skins over one shared
        // source image, so Apple's decoder must produce the same bytes from both.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg", "exfat-zlib.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        FixtureRecord encrypted = EncryptedFixtures.Record("exfat-enc256.dmg");
        FixtureRecord zlib = EncryptedFixtures.Record("exfat-zlib.dmg");

        Assert.Equal(zlib.DecodedSha256, encrypted.DecodedSha256);
    }

    [Fact]
    public void Enc128DecodesToTheSameBytesAsPlainZlib()
    {
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc128.dmg", "exfat-zlib.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        FixtureRecord encrypted = EncryptedFixtures.Record("exfat-enc128.dmg");
        FixtureRecord zlib = EncryptedFixtures.Record("exfat-zlib.dmg");

        Assert.Equal(zlib.DecodedSha256, encrypted.DecodedSha256);
    }

    [Theory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void AWrongPassphraseExitsFourAtTheLowLevelApi(string name)
    {
        if (EncryptedFixtures.Skip(out string? why, name))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path(name));
        Result<EncryptedBlockStream> opened = EncryptedBlockStream.Open(
            file,
            "definitely-not-the-passphrase"u8);

        Assert.False(opened.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, opened.Error.Code);
    }

    [Theory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void AWrongPassphraseExitsFourThroughTheProbeChainToo(string name)
    {
        // The same guarantee, from the entry point a caller who does not know the
        // image is encrypted actually uses: ImageFormatProbeChain.Identify.
        if (EncryptedFixtures.Skip(out string? why, name))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path(name));
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            "definitely-not-the-passphrase"u8,
            name);

        Assert.False(identified.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, identified.Error.Code);
    }

    [Theory]
    [InlineData("exfat-enc256.dmg")]
    [InlineData("exfat-enc128.dmg")]
    public void ACorrectPassphraseOpensEndToEndThroughTheProbeChain(string name)
    {
        // S4.7/S4.8's "also": the probe chain now opens an encrcdsa image with a
        // correct passphrase, rather than always refusing it as E4-not-yet-done.
        if (EncryptedFixtures.Skip(out string? why, name))
        {
            Assert.True(true, why);
            return;
        }

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

    [Fact]
    public void TheTypedPassphraseWorksTooViaTheTrailingNewlineRetry()
    {
        // EncryptedFixtures.Passphrase already carries the newline hdiutil -stdinpass
        // keyed the image off; this proves the retry path a real typed passphrase
        // takes - see EncryptedDmgKeys.Unwrap - reaches the same success end to end.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Result<ImageFormatDetection> identified = ImageFormatProbeChain.Default.Identify(
            file,
            Encoding.UTF8.GetBytes(EncryptedFixtures.TypedPassphrase));

        Assert.True(identified.Ok, identified.Ok ? "" : identified.Error.ToString());
    }
}
