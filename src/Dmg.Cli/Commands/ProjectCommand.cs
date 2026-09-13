using Dmg.Cli.Imaging;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Crypto;
using Dmg.Core.Filesystems;
using Dmg.Core.Projection;
using Dmg.Windows.Projection;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg project IMAGE [FOLDER]</c> - show an image's contents in a folder,
/// decrypting only what is opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verb for an image too large to materialise.</b> <c>mount</c> decodes the
/// whole selected volume to a scratch VHD and then attaches it, which costs a copy
/// of the volume in time and in disk. For a 931 GiB image that is roughly 1.9 TB of
/// I/O before a drive letter appears. This route copies nothing: opening the
/// projection reads the boot sector, the FAT regions it needs and the root
/// directory, and a file's bytes are decrypted when something actually opens it.
/// See <a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>.
/// </para>
/// <para>
/// <b>What it gives up.</b> A folder, not a drive letter - tools that insist on
/// <c>X:\</c> are still <c>mount</c>'s business. Read-only. exFAT only, because the
/// reader behind it reads exFAT and nothing else; anything else is refused by name
/// rather than half-served.
/// </para>
/// <para>
/// <b>It blocks, which no other verb does.</b> A projection exists only while the
/// process serving it is alive, so this one runs until interrupted. That makes
/// console cancellation part of the verb rather than a nicety: killed without
/// <c>PrjStopVirtualizing</c>, the root is left marked as a placeholder directory
/// and Explorer goes on asking a process that is gone to fill it in. Ctrl+C is
/// handled here so that stopping is ordinary rather than damaging.
/// </para>
/// </remarks>
public sealed class ProjectCommand : ICliCommand
{
    private readonly PassphraseReader _passphrases;
    private readonly IProjectionService _projections;
    private readonly Func<CancellationToken, Task> _waitForStop;

    /// <summary>Builds the verb over the shipping ProjFS service.</summary>
    public ProjectCommand()
        : this(new PassphraseReader(), new WindowsProjectionService())
    {
    }

    /// <summary>Builds the verb over substitutes - the constructor a test uses.</summary>
    /// <param name="passphrases">Supplies a passphrase when one is asked for.</param>
    /// <param name="projections">The ProjFS port.</param>
    /// <param name="waitForStop">
    /// Waits until the user asks the projection to stop. Defaults to waiting on
    /// Ctrl+C; a test substitutes something that returns at once, so the verb can be
    /// run end to end without a console and without hanging the suite.
    /// </param>
    public ProjectCommand(
        PassphraseReader passphrases,
        IProjectionService projections,
        Func<CancellationToken, Task>? waitForStop = null)
    {
        ArgumentNullException.ThrowIfNull(passphrases);
        ArgumentNullException.ThrowIfNull(projections);

        _passphrases = passphrases;
        _projections = projections;
        _waitForStop = waitForStop ?? WaitForConsoleInterrupt;
    }

    /// <inheritdoc />
    public string Verb => "project";

    /// <inheritdoc />
    public string Summary => "Show an image's files in a folder without copying it.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "project",
        [
            new OptionSpec(
                "partition",
                "Which partition to project, when the image holds more than one.",
                ValueName: "N"),
            new OptionSpec(
                "password-stdin",
                "Read the passphrase for an encrypted image from stdin."),
            new OptionSpec(
                "password-env",
                "Read the passphrase from an environment variable.",
                ValueName: "VAR"),
            new OptionSpec(
                "keep",
                "Leave whatever was opened behind when the projection stops, instead of "
                + "removing it."),
            CacheOption.Spec,
        ],
        ["IMAGE", "FOLDER"],
        [
            "Nothing is copied. The folder's contents are served from the image as they are "
            + "asked for, so this starts in about the same time whatever the image's size.",
            "The folder must be empty, or not exist yet. Everything in it comes from the image.",
            "Runs until you press Ctrl+C. Windows keeps a copy of everything that gets opened "
            + "while it runs - decrypted, for an encrypted image - so stopping clears the folder "
            + "out again. Pass --keep to leave it.",
            "exFAT volumes only, and read-only. For a drive letter, or for FAT32, use 'dmg mount'.",
            "Needs the Windows Projected File System, an optional Windows feature. dmg says so, "
            + "and how to enable it, if it is switched off.",
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

        if (arguments.Positionals.Count is < 1 or > 2)
        {
            context.Output.Error(DmgError.Usage(
                "'project' needs an image, and optionally a folder to show it in.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        string imagePath = arguments.Positionals[0];
        string rootPath = arguments.Positionals.Count == 2
            ? arguments.Positionals[1]
            : DefaultRootFor(imagePath);

        Result<int?> partition = arguments.TryGetInt32("partition");

        if (!partition.TryGetValue(out int? partitionNumber))
        {
            context.Output.Error(partition.Error);

            return partition.Error.Code;
        }

        Result<long?> cache = CacheOption.BytesFor(arguments);

        if (!cache.TryGetValue(out long? cacheCapacityBytes))
        {
            context.Output.Error(cache.Error);

            return cache.Error.Code;
        }

        // Asked before the image is opened or a passphrase is prompted for: a
        // machine without the feature cannot be helped by either, and finding out
        // after typing a passphrase would be rude.
        Result available = _projections.EnsureAvailable();

        if (!available.Ok)
        {
            context.Output.Error(available.Error);

            return available.Error.Code;
        }

        Result<PassphraseOptions> passphraseOptions = PassphraseOptionsOf(arguments);

        if (!passphraseOptions.TryGetValue(out PassphraseOptions? options))
        {
            context.Output.Error(passphraseOptions.Error);

            return passphraseOptions.Error.Code;
        }

        return Project(
            context,
            imagePath,
            rootPath,
            partitionNumber,
            cacheCapacityBytes,
            options,
            arguments.Has("keep"));
    }

    /// <summary>Opens the image, finds the volume, and serves it until stopped.</summary>
    private DmgExitCode Project(
        CliContext context,
        string imagePath,
        string rootPath,
        int? partitionNumber,
        long? cacheCapacityBytes,
        PassphraseOptions passphraseOptions,
        bool keepContents)
    {
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

            Result<OpenedImage> opened = OpenedImage.Open(
                imagePath,
                passphrase,
                cacheCapacityBytes: cacheCapacityBytes);

            if (!opened.TryGetValue(out OpenedImage? image))
            {
                context.Output.Error(opened.Error);

                return opened.Error.Code;
            }

            using (image)
            {
                return Serve(context, image, imagePath, rootPath, partitionNumber, keepContents);
            }
        }
        finally
        {
            passphrase?.Dispose();
        }
    }

    /// <summary>Picks the volume, builds the content, and runs the projection.</summary>
    private DmgExitCode Serve(
        CliContext context,
        OpenedImage image,
        string imagePath,
        string rootPath,
        int? partitionNumber,
        bool keepContents)
    {
        Result<VolumeMap> read = VolumeMap.Read(image.Disk);

        if (!read.TryGetValue(out VolumeMap? map))
        {
            context.Output.Error(read.Error);

            return read.Error.Code;
        }

        Result<DiskVolume> selected = map.Select(partitionNumber);

        if (!selected.TryGetValue(out DiskVolume? volume))
        {
            context.Output.Error(selected.Error);

            return selected.Error.Code;
        }

        // The reader behind a projection reads exFAT. Refusing by name here is the
        // same courtesy 'mount' pays a filesystem Windows has no driver for.
        if (volume.Filesystem.Kind != FilesystemKind.ExFat)
        {
            context.Output.Error(DmgError.Unsupported(
                $"Partition {volume.Number} is {volume.Filesystem.Name}, and 'project' reads exFAT "
                + "volumes only. Try 'dmg mount', which hands the volume to Windows' own drivers.",
                $"'{imagePath}' partition {volume.Number}: {volume.Filesystem.Name}."));

            return DmgExitCode.UnsupportedFormat;
        }

        context.Output.Trace(
            $"Selected {volume.Number}: {volume.Filesystem.Name} at offset {volume.ByteOffset}");

        Result<ExFatReader> reader = ExFatReader.Open(image.Disk, volume.ByteOffset, volume.ByteLength);

        if (!reader.TryGetValue(out ExFatReader? exfat))
        {
            context.Output.Error(reader.Error);

            return reader.Error.Code;
        }

        ProjectionOptions projectionOptions = new(
            rootPath,
            volume.Filesystem.VolumeLabel,
            KeepContents: keepContents);

        Result<IProjectionSession> started = _projections.Start(
            projectionOptions,
            new ExFatProjectedContent(exfat));

        if (!started.TryGetValue(out IProjectionSession? session))
        {
            context.Output.Error(started.Error);

            return started.Error.Code;
        }

        using (session)
        {
            Report(context, session, volume, imagePath);

            _waitForStop(CancellationToken.None).GetAwaiter().GetResult();

            Result stopped = session.Stop();

            if (!stopped.Ok)
            {
                context.Output.Error(stopped.Error);

                return stopped.Error.Code;
            }
        }

        context.Output.WriteLine(keepContents
            ? $"Stopped. Whatever was opened is still in '{rootPath}' - decrypted, if the image "
                + "was. Delete it when you are done with it."
            : $"Stopped, and '{rootPath}' cleared.");

        return DmgExitCode.Success;
    }

    /// <summary>Says what is where, and how to stop it.</summary>
    private static void Report(
        CliContext context,
        IProjectionSession session,
        DiskVolume volume,
        string imagePath)
    {
        string label = string.IsNullOrWhiteSpace(volume.Filesystem.VolumeLabel)
            ? volume.Filesystem.Name
            : $"{volume.Filesystem.Name} \"{volume.Filesystem.VolumeLabel}\"";

        context.Output.WriteLine(
            $"Projecting '{imagePath}' (partition {volume.Number}, {label}) at {session.RootPath}.");
        context.Output.WriteLine("Nothing has been copied. Press Ctrl+C to stop.");
    }

    /// <summary>
    /// Where a projection goes when the user did not say: a folder beside the
    /// image, named after it.
    /// </summary>
    /// <remarks>
    /// Built from the image's own file name rather than from anything inside it -
    /// a volume label out of an image must never reach the filesystem, for the same
    /// reason scratch paths are constructed rather than taken from the image.
    /// </remarks>
    private static string DefaultRootFor(string imagePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(imagePath);

        return Path.Combine(directory, $"{name}.projected");
    }

    /// <summary>
    /// Waits for Ctrl+C.
    /// </summary>
    /// <remarks>
    /// <see cref="Console.CancelKeyPress"/> with <c>Cancel = true</c>, so the
    /// runtime does not tear the process down before the projection is stopped.
    /// This is the only place in dmg that handles a console signal, and it exists
    /// because this is the only verb that outlives its own work: every other one
    /// returns when it is done.
    /// </remarks>
    private static Task WaitForConsoleInterrupt(CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            // Stop the runtime from killing the process: the projection has to be
            // stopped first, or the root is left marked with nothing serving it.
            eventArgs.Cancel = true;
            completion.TrySetResult();
        }

        Console.CancelKeyPress += OnCancel;

        return completion.Task.ContinueWith(
            _ => Console.CancelKeyPress -= OnCancel,
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Reads the passphrase options, and enforces the one rule the parser cannot:
    /// the two sources are alternatives.
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
