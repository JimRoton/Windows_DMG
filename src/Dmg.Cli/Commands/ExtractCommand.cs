using Dmg.Cli.Imaging;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Crypto;
using Dmg.Core.Imaging;
using Dmg.Core.Vhd;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg extract IMAGE OUTPUT</c> - write the decoded image to a plain file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point of this verb is images that cannot be mounted.</b> A bzip2 image,
/// an HFS+ volume Windows has no driver for, a machine that cannot run elevated
/// right now - <c>extract</c> gets a decoded copy out of all of them, because
/// decoding is all it does. It never attaches anything and never asks for
/// elevation.
/// </para>
/// <para>
/// <b>Two formats, one writer each.</b> <c>--format vhd</c> (the default) runs the
/// decoded disk through <see cref="VhdWriter"/> - a fixed VHD, byte for byte what
/// <c>dmg mount</c> would attach. <c>--format raw</c> writes the sectors with no
/// container at all, for a caller that wants to <c>dd</c> them somewhere else or
/// hand them to a tool that has never heard of VHD.
/// </para>
/// <para>
/// <b>Nothing is left behind on failure.</b> A half-written output file is a file
/// of the right name that invites being trusted; every failure path deletes it
/// before returning.
/// </para>
/// </remarks>
public sealed class ExtractCommand : ICliCommand
{
    private const int BufferSize = 1024 * 1024;

    private readonly PassphraseReader _passphrases;

    /// <summary>Builds the verb over the shipping passphrase reader.</summary>
    public ExtractCommand()
        : this(new PassphraseReader())
    {
    }

    /// <summary>Builds the verb over a substitute passphrase reader - the constructor a test uses.</summary>
    public ExtractCommand(PassphraseReader passphrases)
    {
        ArgumentNullException.ThrowIfNull(passphrases);

        _passphrases = passphrases;
    }

    /// <inheritdoc />
    public string Verb => "extract";

    /// <inheritdoc />
    public string Summary => "Write the decoded image to raw or VHD.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "extract",
        [
            new OptionSpec(
                "format",
                "Which format to write: 'raw' or 'vhd'. Defaults to 'vhd'.",
                ValueName: "FORMAT"),
            new OptionSpec(
                "force",
                "Overwrite OUTPUT if it already exists."),
            new OptionSpec(
                "password-stdin",
                "Read the passphrase for an encrypted image from stdin."),
            new OptionSpec(
                "password-env",
                "Read the passphrase from an environment variable.",
                ValueName: "VAR"),
        ],
        ["IMAGE", "OUTPUT"],
        [
            "Works on images that cannot be mounted at all: an unsupported filesystem, a codec "
            + "Windows has no driver behind, a machine that cannot elevate right now. Decoding is "
            + "all this verb does, so none of that stops it.",
            "'vhd', the default, is a fixed VHD - the same bytes dmg mount would attach. 'raw' is "
            + "the decoded sectors with no container, for a caller that wants to dd them elsewhere.",
            "Refuses to overwrite an existing OUTPUT unless --force is given, and deletes a "
            + "partially written OUTPUT on any failure - a file of the right name is not something "
            + "this verb leaves lying around half done.",
            "Progress goes to stderr as the write proceeds; the result line goes to stdout.",
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

        if (arguments.Positionals.Count != 2)
        {
            context.Output.Error(DmgError.Usage(
                "'extract' needs an image to decode and a path to write it to.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        string imagePath = arguments.Positionals[0];
        string outputPath = arguments.Positionals[1];

        Result<ExtractFormat> format = FormatOf(arguments);

        if (!format.TryGetValue(out ExtractFormat extractFormat))
        {
            context.Output.Error(format.Error);

            return format.Error.Code;
        }

        bool force = arguments.Has("force");

        if (File.Exists(outputPath) && !force)
        {
            context.Output.Error(DmgError.Usage(
                $"'{outputPath}' already exists. Use --force to overwrite it.",
                "extract never overwrites silently."));

            return DmgExitCode.UsageError;
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

            Result<OpenedImage> opened = OpenedImage.Open(imagePath, passphrase);

            if (!opened.TryGetValue(out OpenedImage? image))
            {
                context.Output.Error(opened.Error);

                return opened.Error.Code;
            }

            using (image)
            {
                return extractFormat == ExtractFormat.Raw
                    ? WriteRaw(context, image, outputPath)
                    : WriteVhd(context, image, outputPath);
            }
        }
        finally
        {
            passphrase?.Dispose();
        }
    }

    private static DmgExitCode WriteRaw(CliContext context, OpenedImage image, string outputPath)
    {
        Stream source = image.Disk;
        long total = source.Length;

        context.Output.Progress(
            $"Extracting {ByteSize.Format((ulong)total)} to '{outputPath}' (raw)...");

        source.Position = 0;

        FileStream destination;

        try
        {
            destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DmgError creationFailure = DescribeWriteFailure(exception);
            context.Output.Error(creationFailure);

            return creationFailure.Code;
        }

        bool succeeded = false;
        ProgressThrottle progress = new(context.Output);
        byte[] buffer = new byte[BufferSize];

        try
        {
            long written = 0;

            while (written < total)
            {
                int want = (int)Math.Min(buffer.Length, total - written);

                try
                {
                    source.ReadExactly(buffer, 0, want);
                }
                catch (DmgStreamException stream)
                {
                    // The block stream already knows exactly what went wrong - an
                    // unsupported codec, a chunk that ran past the image, a
                    // corrupt chunk table - and which DmgExitCode that is. Reusing
                    // its DmgError is the difference between reporting that and
                    // reporting every read failure as the same generic "corrupt".
                    context.Output.Error(stream.Error);

                    return stream.Error.Code;
                }
                catch (IOException exception)
                {
                    context.Output.Error(DmgError.Corrupt(
                        "The image ended before its declared length.",
                        exception.Message));

                    return DmgExitCode.CorruptImage;
                }

                destination.Write(buffer, 0, want);
                written += want;
                progress.Report(written, total);
            }

            destination.Flush();
            succeeded = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DmgError error = DescribeWriteFailure(exception);
            context.Output.Error(error);

            return error.Code;
        }
        finally
        {
            destination.Dispose();

            // A half-written output is a file of the right name that invites being
            // trusted. Every non-success exit removes it rather than leaving it
            // for the next `dmg mount` to find.
            if (!succeeded)
            {
                TryDelete(outputPath);
            }
        }

        context.Output.WriteLine($"Wrote {ByteSize.Format((ulong)total)} (raw) to '{outputPath}'.");

        return DmgExitCode.Success;
    }

    private static DmgExitCode WriteVhd(CliContext context, OpenedImage image, string outputPath)
    {
        context.Output.Progress(
            $"Extracting {ByteSize.Format((ulong)image.Disk.Length)} to '{outputPath}' (VHD)...");

        ProgressThrottle progress = new(context.Output);

        Result<VhdWriteResult> written = VhdWriter.WriteToFile(
            image.Disk,
            outputPath,
            progress: new Progress<VhdWriteProgress>(update => progress.Report(update.BytesWritten, update.TotalBytes)));

        if (!written.TryGetValue(out VhdWriteResult? result))
        {
            context.Output.Error(written.Error);

            return written.Error.Code;
        }

        context.Output.WriteLine($"Wrote {result} to '{outputPath}'.");

        return DmgExitCode.Success;
    }

    /// <summary>Removes an output file that a failure left half-written.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller is already returning the failure that matters more.
        }
    }

    /// <summary>
    /// Turns a write failure into the right exit code, the same rule
    /// <see cref="VhdWriter"/> applies to its own destination.
    /// </summary>
    private static DmgError DescribeWriteFailure(Exception exception)
    {
        const int ENOSPC = 28;
        const int ErrorHandleDiskFull = 39;
        const int ErrorDiskFull = 112;

        int code = exception.HResult & 0xFFFF;

        bool outOfSpace = OperatingSystem.IsWindows()
            ? code is ErrorDiskFull or ErrorHandleDiskFull
            : code == ENOSPC;

        return outOfSpace
            ? new DmgError(
                DmgExitCode.InsufficientSpace,
                "The disk filled up while the image was being written.",
                exception.Message)
            : DmgError.Internal("The output could not be written.", exception.Message);
    }

    private enum ExtractFormat
    {
        Raw,
        Vhd,
    }

    private static Result<ExtractFormat> FormatOf(ParsedArguments arguments)
    {
        if (!arguments.TryGetValue("format", out string? text))
        {
            return Result<ExtractFormat>.Success(ExtractFormat.Vhd);
        }

        return text switch
        {
            "raw" => Result<ExtractFormat>.Success(ExtractFormat.Raw),
            "vhd" => Result<ExtractFormat>.Success(ExtractFormat.Vhd),
            _ => Result<ExtractFormat>.Failure(DmgError.Usage(
                $"--format must be 'raw' or 'vhd', but got '{text}'.")),
        };
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

    /// <summary>
    /// Throttles progress to one line per whole percentage point, so a
    /// multi-gigabyte image does not flood stderr with a line per megabyte.
    /// </summary>
    private sealed class ProgressThrottle(Dmg.Core.Diagnostics.IOutput output)
    {
        private int _lastPercent = -1;

        public void Report(long written, long total)
        {
            int percent = total > 0 ? (int)(written * 100 / total) : 100;

            if (percent == _lastPercent)
            {
                return;
            }

            _lastPercent = percent;
            output.Progress($"{ByteSize.Format((ulong)written)} / {ByteSize.Format((ulong)total)} ({percent}%)");
        }
    }
}
