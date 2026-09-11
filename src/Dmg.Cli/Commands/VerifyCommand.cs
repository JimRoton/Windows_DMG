using Dmg.Cli.Imaging;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Containers;
using Dmg.Core.Crypto;
using Dmg.Core.Imaging;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg verify IMAGE</c> - decode every chunk and say whether the image is intact.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "verify" means here.</b> <c>info</c> reads at most the handful of chunks
/// that hold sector 0 and each volume's superblock; this decodes all of them,
/// chunk by chunk, in extent order. That is the only way to know a chunk table
/// entry near the end of the image is not lying about what its compressed bytes
/// decode to - a truncated data fork, a codec's output falling short of its
/// declared length, a chunk that runs past the file - none of those show up until
/// the bytes are actually produced.
/// </para>
/// <para>
/// <b>Two outcomes.</b> Every chunk decoded cleanly (<see cref="DmgExitCode.Success"/>),
/// or one did not, in which case the message names the first chunk that failed - its
/// ordinal in extent order and its <c>EntryType</c>. A codec this build has no
/// decoder for is reported as <see cref="DmgExitCode.UnsupportedFormat"/> - the
/// same code <c>info</c> and <c>extract</c> use for it - because it says nothing
/// about whether the image itself is intact; every other decode failure, a chunk
/// whose bytes do not decode to what its table entry declares, is genuinely
/// <see cref="DmgExitCode.CorruptImage"/>. Opening the image can still fail with
/// any of the usual codes - no such file, wrong passphrase, a container this build
/// has never parsed - before the decode pass ever starts.
/// </para>
/// <para>
/// <b>A raw image has no chunk table</b> - there is nothing to decode, because the
/// bytes on disk are already the payload - so verify reads it start to end once and
/// treats reaching the end as the only thing worth confirming.
/// </para>
/// </remarks>
public sealed class VerifyCommand : ICliCommand
{
    private const int BufferSize = 1024 * 1024;

    private readonly PassphraseReader _passphrases;

    /// <summary>Builds the verb over the shipping passphrase reader.</summary>
    public VerifyCommand()
        : this(new PassphraseReader())
    {
    }

    /// <summary>Builds the verb over a substitute passphrase reader - the constructor a test uses.</summary>
    public VerifyCommand(PassphraseReader passphrases)
    {
        ArgumentNullException.ThrowIfNull(passphrases);

        _passphrases = passphrases;
    }

    /// <inheritdoc />
    public string Verb => "verify";

    /// <inheritdoc />
    public string Summary => "Decode every chunk and confirm the image is intact.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "verify",
        [
            CacheOption.Spec,
            new OptionSpec(
                "password-stdin",
                "Read the passphrase for an encrypted image from stdin."),
            new OptionSpec(
                "password-env",
                "Read the passphrase from an environment variable.",
                ValueName: "VAR"),
        ],
        ["IMAGE"],
        [
            "Decodes every chunk of the image, in order - not just the handful info reads. "
            + "That is the only way to catch a chunk near the end of the file whose compressed "
            + "bytes do not decode to what its table entry declares.",
            "Exits 0 when every chunk decoded cleanly. A decode failure is reported with the "
            + "failing chunk's index and EntryType: a codec this build cannot run exits 3, the "
            + "same code 'info' and 'extract' use for it, since that says nothing about whether "
            + "the image is intact; a chunk whose bytes do not decode to its declared length - a "
            + "truncated data fork or similar - is genuinely corrupt and exits 9.",
            "Opening the image can still fail first, with the exit code that failure already "
            + "has: no such file (2), a container this build has never parsed (3), a passphrase "
            + "that did not work (4).",
            "Progress goes to stderr as the decode proceeds; the result line goes to stdout.",
        ]);

    /// <inheritdoc />
    public DmgExitCode Execute(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, context.Arguments);

        if (!parsed.TryGetValue(out ParsedArguments? arguments))
        {
            context.Output.Error(parsed.Error);

            return parsed.Error.Code;
        }

        if (arguments.Positionals.Count != 1)
        {
            context.Output.Error(DmgError.Usage(
                "'verify' needs an image to decode.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        string imagePath = arguments.Positionals[0];

        Result<long?> cache = CacheOption.BytesFor(arguments);

        if (!cache.TryGetValue(out long? cacheCapacityBytes))
        {
            context.Output.Error(cache.Error);

            return cache.Error.Code;
        }

        Result<PassphraseOptions> options = PassphraseOptionsOf(arguments);

        if (!options.TryGetValue(out PassphraseOptions? passphraseOptions))
        {
            context.Output.Error(options.Error);

            return options.Error.Code;
        }

        Passphrase? passphrase = null;

        try
        {
            if (passphraseOptions.Source != PassphraseSource.Unspecified)
            {
                Result<Passphrase> read = _passphrases.Read(passphraseOptions);

                if (!read.TryGetValue(out passphrase))
                {
                    context.Output.Error(read.Error);

                    return read.Error.Code;
                }
            }

            context.Output.Trace($"Reading {imagePath}");

            Result<OpenedImage> opened = OpenedImage.Open(imagePath, passphrase, cacheCapacityBytes: cacheCapacityBytes);

            if (!opened.TryGetValue(out OpenedImage? image))
            {
                context.Output.Error(opened.Error);

                return opened.Error.Code;
            }

            using (image)
            {
                return image.Image is DmgImage dmgImage
                    ? VerifyChunks(context, image.Disk, dmgImage)
                    : VerifyRaw(context, image.Disk);
            }
        }
        finally
        {
            passphrase?.Dispose();
        }
    }

    /// <summary>
    /// Decodes a UDIF image one extent at a time, in extent order, so a failure can
    /// be attributed to the chunk that caused it.
    /// </summary>
    private static DmgExitCode VerifyChunks(CliContext context, Stream disk, DmgImage image)
    {
        IReadOnlyList<Extent> extents = image.Index.Extents;

        context.Output.Progress(
            $"Verifying {extents.Count} chunk(s), {ByteSize.Format((ulong)image.Length)}...");

        byte[] buffer = new byte[BufferSize];
        ProgressThrottle progress = new(context.Output);
        long verified = 0;

        for (int ordinal = 0; ordinal < extents.Count; ordinal++)
        {
            Extent extent = extents[ordinal];

            long start = checked((long)extent.StartSector * DmgBlockStream.BytesPerSector);
            long length = checked((long)extent.SectorCount * DmgBlockStream.BytesPerSector);

            try
            {
                disk.Position = start;

                long remaining = length;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);

                    disk.ReadExactly(buffer, 0, want);

                    remaining -= want;
                    verified += want;
                    progress.Report(verified, image.Length);
                }
            }
            catch (DmgStreamException stream)
            {
                return Fail(context, ordinal, extent.EntryType, stream.Error.Code, stream.Error.ToString());
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException)
            {
                return Fail(context, ordinal, extent.EntryType, DmgExitCode.CorruptImage, exception.Message);
            }
        }

        context.Output.WriteLine(
            $"OK: {extents.Count} chunk(s), {ByteSize.Format((ulong)image.Length)} decoded without error.");

        return DmgExitCode.Success;
    }

    /// <summary>
    /// A raw image has no chunk table to walk - the bytes on disk already are the
    /// payload - so verifying it means reading it start to end and confirming
    /// nothing goes wrong on the way.
    /// </summary>
    private static DmgExitCode VerifyRaw(CliContext context, Stream disk)
    {
        long total = disk.Length;

        context.Output.Progress($"Verifying {ByteSize.Format((ulong)total)} (raw)...");

        disk.Position = 0;

        byte[] buffer = new byte[BufferSize];
        ProgressThrottle progress = new(context.Output);
        long read = 0;

        try
        {
            while (read < total)
            {
                int want = (int)Math.Min(buffer.Length, total - read);

                disk.ReadExactly(buffer, 0, want);

                read += want;
                progress.Report(read, total);
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            context.Output.Error(DmgError.Corrupt(
                "The image ended before its declared length.",
                exception.Message));

            return DmgExitCode.CorruptImage;
        }

        context.Output.WriteLine($"OK: {ByteSize.Format((ulong)total)} (raw) read without error.");

        return DmgExitCode.Success;
    }

    /// <summary>
    /// Reports the first chunk that failed to decode, as the story requires. An
    /// unsupported codec is reported and coded exactly like <c>info</c> and
    /// <c>extract</c> report it - <see cref="DmgExitCode.UnsupportedFormat"/>, not
    /// corrupt - because the image itself may be perfectly intact; every other
    /// decode failure is genuinely <see cref="DmgExitCode.CorruptImage"/>.
    /// </summary>
    private static DmgExitCode Fail(
        CliContext context,
        int ordinal,
        ChunkEntryType entryType,
        DmgExitCode code,
        string detail)
    {
        DmgError error = code == DmgExitCode.UnsupportedFormat
            ? DmgError.Unsupported(
                $"Chunk {ordinal} ({entryType}) uses a codec this build has no decoder for.",
                detail)
            : DmgError.Corrupt(
                $"Chunk {ordinal} ({entryType}) failed to decode.",
                detail);

        context.Output.Error(error);

        return error.Code;
    }

    /// <summary>
    /// Turns the parsed switches into the options <see cref="PassphraseReader"/>
    /// takes. The parser has already rejected an unknown option and caught a
    /// repeat, so what is left is the one rule it cannot know: the two sources are
    /// alternatives.
    /// </summary>
    private static Result<PassphraseOptions> PassphraseOptionsOf(ParsedArguments arguments)
    {
        bool stdin = arguments.Has("password-stdin");
        bool environment = arguments.TryGetValue("password-env", out string? variable);

        if (stdin && environment)
        {
            return Result<PassphraseOptions>.Failure(DmgError.Usage(
                $"{PassphraseOptions.StandardInputOption} and {PassphraseOptions.EnvironmentOption} "
                + "are alternatives. Give one or neither.",
                "A passphrase comes from exactly one place, so that it is obvious which."));
        }

        PassphraseSource source = stdin
            ? PassphraseSource.StandardInput
            : environment ? PassphraseSource.Environment : PassphraseSource.Unspecified;

        return Result<PassphraseOptions>.Success(
            new PassphraseOptions(source, variable, arguments.Positionals));
    }
}
