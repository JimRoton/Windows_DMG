using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Imaging;

namespace Dmg.Cli.Imaging;

/// <summary>
/// An image opened for reading its decoded bytes - the step <c>extract</c> and
/// <c>verify</c> both start from, before either has anything to report.
/// </summary>
/// <remarks>
/// <para>
/// This is the same passphrase-and-probe sequence
/// <see cref="Dmg.Cli.Info.ImageInspector"/> runs, with one difference that
/// matters: <c>info</c> is content to describe a locked image and stop there, so it
/// treats "encrypted, no passphrase" as a complete answer. Extract and verify have
/// no plaintext to work with in that case and therefore nothing to do, so here it
/// is the ordinary <see cref="DmgExitCode.DecryptionFailed"/> a caller already
/// knows how to report - the same failure a wrong passphrase gets, because both
/// leave the plaintext equally out of reach.
/// </para>
/// <para>
/// <see cref="Disk"/> is the decoded disk from byte zero to its
/// <see cref="Stream.Length"/>: a <see cref="DmgBlockStream"/> over a UDIF
/// container, or the plaintext stream itself for a raw one, which has no
/// container to decode. It is deliberately just a <see cref="Stream"/>, for the
/// same reason <see cref="DmgBlockStream"/> is: extract's writers and verify's
/// decode pass already speak <see cref="Stream"/> and need not know which kind of
/// image is underneath.
/// </para>
/// </remarks>
public sealed class OpenedImage : IDisposable
{
    private OpenedImage(Stream disk, ImageFormatDetection detection, DmgImage? image)
    {
        Disk = disk;
        Detection = detection;
        Image = image;
    }

    /// <summary>The decoded disk, from byte zero to <see cref="Stream.Length"/>.</summary>
    public Stream Disk { get; }

    /// <summary>What the probe chain found: format, size, summary.</summary>
    public ImageFormatDetection Detection { get; }

    /// <summary>
    /// The parsed UDIF container, for a caller that wants the chunk table - null for
    /// a raw image, which has no chunks to walk.
    /// </summary>
    public DmgImage? Image { get; }

    /// <summary>
    /// Opens <paramref name="path"/> and returns a stream over its decoded bytes.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="passphrase">
    /// A passphrase to unlock the image with, when it is encrypted. Null when none
    /// was given, in which case a locked image fails with
    /// <see cref="DmgExitCode.DecryptionFailed"/> rather than being described and
    /// left shut - unlike <c>info</c>, there is no answer to give here without the
    /// plaintext.
    /// </param>
    /// <param name="chain">The format probes. Defaults to the shipping chain.</param>
    public static Result<OpenedImage> Open(string path, Passphrase? passphrase, ImageFormatProbeChain? chain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        chain ??= ImageFormatProbeChain.Default;

        Result<FileStream> opened = OpenFile(path);

        if (!opened.TryGetValue(out FileStream? file))
        {
            return opened.CastFailure<OpenedImage>();
        }

        Result<ImageProbeContext> probed = ImageProbeContext.Create(file, path);

        if (!probed.TryGetValue(out ImageProbeContext? probe))
        {
            file.Dispose();
            return probed.CastFailure<OpenedImage>();
        }

        Stream payload;
        Result<ImageFormatDetection> identified;

        if (EncryptedImageProbe.IsEncrcdsaV2(probe))
        {
            if (passphrase is null)
            {
                // The probe's own verdict on the ciphertext already names the file
                // and the remedy - reusing it means this class does not invent a
                // second message for the same refusal.
                Result<ImageFormatDetection> locked = chain.Identify(probe);
                file.Dispose();
                return locked.CastFailure<OpenedImage>();
            }

            Result<EncryptedBlockStream> unlocked = EncryptedBlockStream.Open(
                file,
                passphrase.Bytes,
                leaveOpen: false);

            if (!unlocked.TryGetValue(out EncryptedBlockStream? plaintext))
            {
                file.Dispose();
                return unlocked.CastFailure<OpenedImage>();
            }

            payload = plaintext;

            // The plaintext has not been probed yet - the earlier context read the
            // ciphertext, which tells us nothing about what is inside it.
            identified = chain.Identify(payload, path);
        }
        else
        {
            payload = file;
            identified = chain.Identify(probe);
        }

        if (!identified.TryGetValue(out ImageFormatDetection? detection))
        {
            payload.Dispose();
            return identified.CastFailure<OpenedImage>();
        }

        if (detection.Format == ImageFormat.Raw)
        {
            return Result<OpenedImage>.Success(new OpenedImage(payload, detection, image: null));
        }

        if (detection.Format != ImageFormat.Udif)
        {
            // ImageFormatDetection is only ever produced for Udif and Raw - see its
            // own remarks. Reaching here would mean this class disagrees with the
            // chain it just asked, which is a bug in this code, not anything an
            // image can cause.
            payload.Dispose();
            return Result<OpenedImage>.Failure(DmgError.Internal(
                "The probe chain identified a format this build has no reader for.",
                $"Format={detection.Format}."));
        }

        Result<DmgImage> parsed = DmgImage.Open(payload);

        if (!parsed.TryGetValue(out DmgImage? image))
        {
            payload.Dispose();
            return parsed.CastFailure<OpenedImage>();
        }

        Result<DmgBlockStream> disk = DmgBlockStream.Create(payload, image, leaveOpen: false);

        if (!disk.TryGetValue(out DmgBlockStream? block))
        {
            payload.Dispose();
            return disk.CastFailure<OpenedImage>();
        }

        return Result<OpenedImage>.Success(new OpenedImage(block, detection, image));
    }

    /// <summary>Disposes the decoded stream, and with it everything underneath.</summary>
    public void Dispose() => Disk.Dispose();

    private static Result<FileStream> OpenFile(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Result<FileStream>.Failure(DmgError.Usage(
                    $"'{path}' is a folder, not a disk image file.",
                    "A .sparsebundle is a folder; this build cannot read one."));
            }

            if (!File.Exists(path))
            {
                return Result<FileStream>.Failure(DmgError.Usage($"There is no file at '{path}'."));
            }

            return Result<FileStream>.Success(new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return Result<FileStream>.Failure(DmgError.Usage(
                $"Could not open '{path}'.",
                exception.Message));
        }
    }
}
