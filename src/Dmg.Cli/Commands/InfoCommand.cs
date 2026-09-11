using System.Text.Json;
using Dmg.Cli.Info;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Crypto;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg info IMAGE</c> - what an image is, without opening it up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Answering the question is the job, so answering it is a success.</b> An image
/// whose codec this build cannot decode, a volume Windows cannot mount, a partition
/// table that will not parse: all of those are things a user asked <c>info</c> to
/// find out, and reporting them is the verb working, not failing. So this exits 0
/// for every image it can describe, and non-zero only when it could not describe one
/// at all - no such file (2), not a disk image (3), a passphrase that did not work
/// (4), a trailer that is not a trailer (9). A script that wants the verdict rather
/// than the exit code reads <c>canDecode</c> and <c>canMount</c> out of
/// <c>--json</c>.
/// </para>
/// <para>
/// <b>What it does not do.</b> It never decodes the image. It decodes at most the
/// handful of chunks that hold sector 0 and each volume's superblock, and only when
/// the codec inventory - read from the block map, without touching the data fork -
/// says every codec in the image has a decoder. On a bzip2 image it skips that step
/// and says so, rather than failing or hanging.
/// </para>
/// <para>
/// <b>No <c>--password</c>.</b> A passphrase on a command line lands in shell
/// history, in the process list, and in any command-line auditing the machine does.
/// <c>--password-stdin</c> and <c>--password-env</c> exist instead, and an encrypted
/// image with neither is described as far as its wrapper and no further - which is
/// still a useful answer, and does not stop to prompt when nobody may be watching.
/// </para>
/// </remarks>
public sealed class InfoCommand : ICliCommand
{
    private readonly ImageInspector _inspector;
    private readonly PassphraseReader _passphrases;

    /// <summary>Builds the verb over the shipping inspector and passphrase reader.</summary>
    public InfoCommand()
        : this(new ImageInspector(), new PassphraseReader())
    {
    }

    /// <summary>Builds the verb over substitutes - the constructor a test uses.</summary>
    /// <param name="inspector">Reads the image.</param>
    /// <param name="passphrases">Supplies a passphrase when one is asked for.</param>
    public InfoCommand(ImageInspector inspector, PassphraseReader passphrases)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(passphrases);

        _inspector = inspector;
        _passphrases = passphrases;
    }

    /// <inheritdoc />
    public string Verb => "info";

    /// <inheritdoc />
    public string Summary => "Describe an image without mounting or decoding it.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "info",
        [
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
            "Reads the trailer, the property list and the block map. It decodes at most the few "
            + "chunks that hold the partition table, and skips even those when the image uses a codec "
            + "this build cannot decode - so it is quick and safe on any image.",
            "Exits 0 for any image it can describe, including one that cannot be decoded or mounted: "
            + "saying so is the answer. Read canDecode and canMount out of --json for the verdict in "
            + "a script.",
            "There is deliberately no --password: a passphrase on a command line lands in shell "
            + "history and in the process list.",
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

        if (arguments.Positionals.Count == 0)
        {
            context.Output.Error(DmgError.Usage(
                "'info' needs an image to describe.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        string path = arguments.Positionals[0];

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

            context.Output.Trace($"Reading {path}");

            Result<ImageReport> inspected = _inspector.Inspect(path, passphrase);

            if (!inspected.TryGetValue(out ImageReport? report))
            {
                context.Output.Error(inspected.Error);

                return inspected.Error.Code;
            }

            Report(context, report);

            return DmgExitCode.Success;
        }
        finally
        {
            passphrase?.Dispose();
        }
    }

    private static void Report(CliContext context, ImageReport report)
    {
        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                InfoPayload.From(report),
                CliJson.Readable.InfoPayload));

            return;
        }

        ImageReportText.WriteTo(context.Output, report);

        // Deliberately no matching warning on stderr when the partitioning could
        // not be read. The report already says so, in the same words, in the row
        // where a reader is looking for it; a second copy on stderr is a duplicate
        // in the common case where both streams are the same terminal. --json
        // carries it as partitioningNote for a caller who is not reading the
        // screen.
        context.Output.Trace(report.Summary);
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
