using System.Text.Json;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;
using Dmg.Windows.Volumes;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg unmount &lt;letter|id&gt; | --all [--keep-scratch]</c> - detach a mount
/// <c>dmg mount</c> made, and delete the scratch VHD behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>All the interesting decisions belong to <see cref="DetachService"/>.</b> This
/// verb is argument handling and reporting around it: pick the one mount a target
/// names, or every mount on record for <c>--all</c>, and turn the
/// <see cref="DetachOutcome"/>s it returns into a sentence or a JSON document. It
/// does not open a virtual disk handle or touch the registry itself.
/// </para>
/// <para>
/// <b>A target is a drive letter or an id, never both.</b>
/// <see cref="DriveLetter.Normalise"/> decides which: an id is eight hex
/// characters and can never parse as a single letter, so there is no ambiguity to
/// resolve by trying one and falling back to the other.
/// </para>
/// <para>
/// <b>Reclaimed space is measured before <see cref="DetachService"/> deletes
/// anything.</b> A <see cref="DetachOutcome"/> only says whether the scratch VHD
/// was deleted, not how large it was, and by the time this verb sees the outcome
/// the file is already gone. So the size is read from disk first, against the same
/// reconciled registry <see cref="DetachService"/> itself will read a moment
/// later - the actual detach still goes through it untouched.
/// </para>
/// <para>
/// <b>Not one of ours is not an error.</b> Matches
/// <see cref="DetachOutcome.NothingToDo"/>: a target that names no current mount -
/// wrong id, an unrelated drive letter, one already unmounted - gets a plain
/// sentence saying so and exit 0, not a failure. <see cref="DmgExitCode.MountFailed"/>
/// is reserved for a mount <see cref="DetachService"/> could not detach - most often
/// because a handle is still open on it - which is reported exactly as it came back,
/// with nothing forced.
/// </para>
/// </remarks>
public sealed class UnmountCommand : ICliCommand
{
    private readonly IVirtualDiskService _virtualDisks;
    private readonly Func<Result<MountRegistry>> _openRegistry;

    /// <summary>Builds the verb over the shipping Windows services.</summary>
    public UnmountCommand()
        : this(new WindowsVirtualDiskService())
    {
    }

    /// <summary>Builds the verb over a substitute - the constructor a test uses.</summary>
    /// <param name="virtualDisks">The virtdisk.dll port, used to detach and to reconcile the registry.</param>
    /// <param name="openRegistry">
    /// Opens the mount registry. Defaults to <see cref="MountRegistry.ForCurrentUser"/>;
    /// a test substitutes a registry rooted at a temporary directory.
    /// </param>
    public UnmountCommand(IVirtualDiskService virtualDisks, Func<Result<MountRegistry>>? openRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(virtualDisks);

        _virtualDisks = virtualDisks;
        _openRegistry = openRegistry ?? (() => MountRegistry.ForCurrentUser());
    }

    /// <inheritdoc />
    public string Verb => "unmount";

    /// <inheritdoc />
    public string Summary => "Detach a mount dmg made, and delete its scratch VHD.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "unmount",
        [
            new OptionSpec("all", "Detach every mount currently on record."),
            new OptionSpec(
                "keep-scratch",
                "Leave the scratch VHD in place instead of deleting it."),
        ],
        ["LETTER|ID"],
        [
            "The target is the drive letter or the id 'dmg mount' printed - 'dmg unmount E:', "
            + "'dmg unmount E' and 'dmg unmount a1b2c3d4' all work.",
            "A target that names no current mount is not an error: it has already got you what "
            + "you asked for, so this exits 0 and says so.",
            "If Windows will not let the disk go - most often a handle still open on it - dmg says "
            + "so and does not force it. The mount is left exactly as it was; try again once "
            + "whatever is holding it lets go.",
            "--all is an alternative to a target, not a modifier of one: give one or the other.",
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

        bool all = arguments.Has("all");
        bool keepScratch = arguments.Has("keep-scratch");

        if (all && arguments.Positionals.Count > 0)
        {
            context.Output.Error(DmgError.Usage(
                "--all does not take a target. Give a drive letter or an id, or --all, not both.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        if (!all && arguments.Positionals.Count != 1)
        {
            context.Output.Error(DmgError.Usage(
                "'unmount' needs a drive letter or an id to detach, or --all for every mount.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        Result<MountRegistry> opened = _openRegistry();

        if (!opened.TryGetValue(out MountRegistry? registry))
        {
            context.Output.Error(opened.Error);

            return opened.Error.Code;
        }

        DetachService detachService = new(registry, _virtualDisks, context.Output);

        return all
            ? ExecuteAll(context, detachService, registry, keepScratch)
            : ExecuteOne(context, detachService, registry, arguments.Positionals[0], keepScratch);
    }

    /// <summary>
    /// Detaches one target. The pre-detach lookup below exists for exactly one
    /// reason - to weigh the scratch VHD before <see cref="DetachService"/> can
    /// delete it - and plays no part in deciding what gets detached; that decision
    /// is <see cref="DetachService"/>'s alone, reached a moment later by the same
    /// letter-or-id rule applied to its own reconciled read of the registry.
    /// </summary>
    private DmgExitCode ExecuteOne(
        CliContext context,
        DetachService detachService,
        MountRegistry registry,
        string target,
        bool keepScratch)
    {
        string? driveLetter = DriveLetter.Normalise(target);

        MountRecord? before = driveLetter is not null
            ? Find(registry, record => record.DriveLetter == driveLetter)
            : Find(registry, record => string.Equals(record.Id, target, StringComparison.OrdinalIgnoreCase));

        long reclaimable = before is null ? 0 : VhdSizeIfExists(before.VhdPath);

        Result<DetachOutcome> result = driveLetter is not null
            ? detachService.DetachByDriveLetter(target, keepScratch)
            : detachService.DetachById(target, keepScratch);

        if (!result.TryGetValue(out DetachOutcome? outcome))
        {
            context.Output.Error(result.Error);

            return result.Error.Code;
        }

        Report(context, target, outcome, reclaimable);

        return DmgExitCode.Success;
    }

    private DmgExitCode ExecuteAll(
        CliContext context,
        DetachService detachService,
        MountRegistry registry,
        bool keepScratch)
    {
        IReadOnlyList<MountRecord> before = registry.Read(_virtualDisks);

        Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase);

        foreach (MountRecord record in before)
        {
            sizes[record.Id] = VhdSizeIfExists(record.VhdPath);
        }

        DetachAllOutcome outcome = detachService.DetachAll(keepScratch);

        ReportAll(context, outcome, sizes);

        return outcome.Failed.Count > 0 ? DmgExitCode.MountFailed : DmgExitCode.Success;
    }

    private MountRecord? Find(MountRegistry registry, Func<MountRecord, bool> matches) =>
        registry.Read(_virtualDisks).FirstOrDefault(matches);

    /// <summary>
    /// The scratch VHD's size, read before <see cref="DetachService"/> gets a
    /// chance to delete it. Best-effort: a file that cannot be measured reads as
    /// nothing reclaimed rather than failing the whole command over a detail the
    /// user did not ask about.
    /// </summary>
    private static long VhdSizeIfExists(string vhdPath)
    {
        try
        {
            FileInfo info = new(vhdPath);

            return info.Exists ? info.Length : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }

    private static void Report(CliContext context, string target, DetachOutcome outcome, long reclaimedBytes)
    {
        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                new UnmountPayload(
                    outcome.WasMounted,
                    outcome.Record?.Id,
                    outcome.Record?.SourcePath,
                    outcome.Record?.DriveLetter,
                    outcome.Record?.VhdPath,
                    outcome.ScratchDeleted,
                    outcome.ScratchDeleted ? reclaimedBytes : 0),
                CliJson.Readable.UnmountPayload));

            return;
        }

        context.Output.WriteLine(Describe(target, outcome, reclaimedBytes));
    }

    private static void ReportAll(CliContext context, DetachAllOutcome outcome, IReadOnlyDictionary<string, long> sizes)
    {
        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                new UnmountAllPayload(
                    [
                        .. outcome.Detached.Select(detached => new UnmountPayload(
                            detached.WasMounted,
                            detached.Record?.Id,
                            detached.Record?.SourcePath,
                            detached.Record?.DriveLetter,
                            detached.Record?.VhdPath,
                            detached.ScratchDeleted,
                            ReclaimedOf(detached, sizes))),
                    ],
                    [
                        .. outcome.Failed.Select(failure => new UnmountFailurePayload(
                            failure.Record.Id,
                            failure.Record.DriveLetter,
                            failure.Record.SourcePath,
                            failure.Error.Message)),
                    ]),
                CliJson.Readable.UnmountAllPayload));

            return;
        }

        if (outcome.Detached.Count == 0 && outcome.Failed.Count == 0)
        {
            context.Output.WriteLine("Nothing is mounted; there is nothing to unmount.");

            return;
        }

        long totalReclaimed = 0;

        foreach (DetachOutcome detached in outcome.Detached)
        {
            long reclaimed = ReclaimedOf(detached, sizes);
            totalReclaimed += reclaimed;

            context.Output.WriteLine(Describe(detached.Record?.DescribeDriveLetter() ?? "?", detached, reclaimed));
        }

        foreach (DetachFailure failure in outcome.Failed)
        {
            context.Output.WriteLine(
                $"Could not unmount '{failure.Record.SourcePath}' ({failure.Record.DescribeDriveLetter()}, "
                + $"id {failure.Record.Id}): {failure.Error.Message}");
        }

        context.Output.WriteLine(
            $"Unmounted {outcome.Detached.Count} of {outcome.Detached.Count + outcome.Failed.Count}, "
            + $"reclaimed {ByteSize.Format(totalReclaimed)}.");
    }

    private static long ReclaimedOf(DetachOutcome outcome, IReadOnlyDictionary<string, long> sizes) =>
        outcome is { ScratchDeleted: true, Record: not null } ? sizes.GetValueOrDefault(outcome.Record.Id) : 0;

    private static string Describe(string target, DetachOutcome outcome, long reclaimedBytes)
    {
        if (!outcome.WasMounted || outcome.Record is not MountRecord record)
        {
            return $"Nothing is mounted at '{target}'; there is nothing to unmount.";
        }

        string where = $"'{record.SourcePath}' from {record.DescribeDriveLetter()} (id {record.Id})";

        if (!outcome.ScratchDeleted)
        {
            return $"Unmounted {where}. The scratch VHD was kept: '{record.VhdPath}'.";
        }

        return $"Unmounted {where}. Reclaimed {ByteSize.Format(reclaimedBytes)}.";
    }
}
