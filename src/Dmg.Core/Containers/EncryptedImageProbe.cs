namespace Dmg.Core.Containers;

/// <summary>
/// First in the chain: recognises an encrypted image and stops, because there is
/// nothing underneath the encryption anyone can look at.
/// </summary>
/// <remarks>
/// <para>
/// It has to come first. An encrypted image is a wrapper - the UDIF container the
/// user made is inside the ciphertext - and every byte outside the wrapper's own
/// header is indistinguishable from noise. Left later in the chain, a file whose
/// last 512 bytes of ciphertext happened to begin <c>koly</c> would be handed to
/// the UDIF parser, which would then report a corrupt image. "Your image is
/// damaged" is a much worse answer than "your image is encrypted", and it is
/// wrong.
/// </para>
/// <para>
/// <b>This probe recognises; it does not decrypt.</b> <see cref="Describe"/> always
/// refuses a v2 <c>encrcdsa</c> file - it has no passphrase to try. Decryption
/// itself lives in <c>Dmg.Core.Crypto</c>, and the passphrase-aware path is
/// <see cref="ImageFormatProbeChain.Identify(Stream, ReadOnlySpan{byte}, string?)"/>,
/// which recognises the same header via <see cref="IsEncrcdsaV2"/>, decrypts, and
/// re-runs the chain over the plaintext - so a correct passphrase reaches
/// <see cref="UdifImageProbe"/> or <see cref="RawImageProbe"/> exactly as an
/// unencrypted file would, and this probe never has to know what came out the
/// other side.
/// </para>
/// <para>
/// <b>Version 1 is not the same refusal as version 2.</b> A v2 header just needs a
/// passphrase this build cannot yet supply on its own - <see cref="DmgExitCode.DecryptionFailed"/>,
/// the code a caller with a passphrase branches on. A v1 header is a layout this
/// build has never parsed, passphrase or not - <see cref="DmgExitCode.UnsupportedFormat"/>,
/// the same code every other format this tool declines to read gets. Conflating the
/// two would tell someone holding the right passphrase for a v1 image that a retry
/// might work; it never will.
/// </para>
/// </remarks>
public sealed class EncryptedImageProbe : IImageFormatProbe
{
    /// <summary>The shared instance. The probe is stateless.</summary>
    public static EncryptedImageProbe Instance { get; } = new();

    private EncryptedImageProbe()
    {
    }

    /// <inheritdoc />
    public ImageFormat Format => ImageFormat.Encrypted;

    /// <inheritdoc />
    public string Name => "encrypted";

    /// <inheritdoc />
    public bool Recognises(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return IsVersion2(image) || IsVersion1(image);
    }

    /// <inheritdoc />
    public Result<ImageFormatDetection> Describe(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        bool version2 = IsVersion2(image);
        bool version1 = !version2 && IsVersion1(image);

        if (!version2 && !version1)
        {
            return Result<ImageFormatDetection>.Failure(DmgError.Internal(
                "The encrypted-image probe was asked to describe a file it does not recognise.",
                ImageFormatSignatures.DescribeLeadingBytes(image.Header)));
        }

        if (version1)
        {
            // Named and refused outright. Nobody's passphrase will ever change this
            // answer, so it must never be DecryptionFailed - that code tells a
            // caller "try again with a passphrase", and there is no build of this
            // tool that reads a v1 layout no matter what passphrase arrives.
            return Result<ImageFormatDetection>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"{image.Describe()} is a legacy cdsaencr (version 1) encrypted disk image, "
                + "from the FileVault era. Its header sits at the end of the file in a "
                + "different layout from version 2's, and this build has never parsed it.",
                $"Encrypted image header cdsaencr (version 1) in a {image.Length}-byte file.");
        }

        return Result<ImageFormatDetection>.Failure(
            DmgExitCode.DecryptionFailed,
            $"{image.Describe()} is an encrypted disk image and needs a passphrase to open. "
            + "Use --password-stdin, --password-env VAR, or answer the prompt.",
            $"Encrypted image header encrcdsa (version 2) in a {image.Length}-byte file.");
    }

    /// <summary>
    /// True when <paramref name="image"/> begins with the version-2 <c>encrcdsa</c>
    /// signature - the only encrypted layout this build can actually decrypt.
    /// </summary>
    /// <remarks>
    /// Exposed so <see cref="ImageFormatProbeChain"/>'s passphrase-aware overload
    /// knows when trying a passphrase makes sense - a v1 header, or no header at
    /// all, is never worth the PBKDF2 pass - without duplicating the signature
    /// check <see cref="Describe"/> already makes.
    /// </remarks>
    public static bool IsEncrcdsaV2(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return IsVersion2(image);
    }

    /// <summary>
    /// Version 2, and the only case that matters in practice: the eight bytes
    /// <c>encrcdsa</c> at offset zero, written by every <c>hdiutil -encryption</c>
    /// since Mac OS X 10.5.
    /// </summary>
    private static bool IsVersion2(ImageProbeContext image) =>
        image.Header.Length >= 8 && image.Header[..8].SequenceEqual("encrcdsa"u8);

    /// <summary>
    /// Version 1, the AES-128 format from the FileVault era, which puts its header
    /// at the end of the file instead of the front.
    /// </summary>
    /// <remarks>
    /// The exact offset of that trailing header is not fixed - it depends on the
    /// wrapped key sizes - so the tag is looked for anywhere in the last 512 bytes
    /// rather than at one place. A scan of a bounded window is cheap and cannot run
    /// off anything; the alternative, a hardcoded offset guessed from one sample,
    /// would fail silently on the next sample. Version 1 images are twenty years old
    /// and vanishingly rare, so this is here to give them a name, not to be relied
    /// on.
    /// </remarks>
    private static bool IsVersion1(ImageProbeContext image) =>
        image.Trailer.IndexOf("cdsaencr"u8) >= 0;
}
