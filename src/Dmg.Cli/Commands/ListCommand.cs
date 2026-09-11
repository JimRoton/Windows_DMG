using System.Text.Json;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;
using Dmg.Windows.Volumes;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg list</c> - every image dmg currently has mounted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciled, not raw.</b> Like <c>dmg unmount</c>, this reads the registry
/// through <see cref="MountRegistry.Read(IVirtualDiskService)"/> (S8.8), so a mount
/// a reboot silently dropped is never listed as if it were still there.
/// </para>
/// <para>
/// <b>Two columns the registry does not store.</b> <see cref="MountRecord"/> keeps
/// what a mount needs to be found and undone - id, source, VHD path, drive letter,
/// mode - not what filesystem ended up on it or how large it is. Both are read live
/// instead: size from the scratch VHD's own length on disk, which is exactly the
/// volume's decoded size plus the small VHD footer <c>Dmg.Core.Vhd.VhdWriter</c>
/// appends, and filesystem from <see cref="IVolumeFilesystemProbe"/> - answerable
/// only for a mount that actually has a drive letter.
/// </para>
/// <para>
/// <b>An empty list says so.</b> A table with a header and no rows reads like
/// something went wrong; a sentence does not.
/// </para>
/// </remarks>
public sealed class ListCommand : ICliCommand
{
    private readonly IVirtualDiskService _virtualDisks;
    private readonly Func<Result<MountRegistry>> _openRegistry;
    private readonly IVolumeFilesystemProbe _filesystems;

    /// <summary>Builds the verb over the shipping Windows services.</summary>
    public ListCommand()
        : this(new WindowsVirtualDiskService())
    {
    }

    /// <summary>Builds the verb over substitutes - the constructor a test uses.</summary>
    /// <param name="virtualDisks">The virtdisk.dll port, used to reconcile the registry.</param>
    /// <param name="openRegistry">
    /// Opens the mount registry. Defaults to <see cref="MountRegistry.ForCurrentUser"/>;
    /// a test substitutes a registry rooted at a temporary directory.
    /// </param>
    /// <param name="filesystems">
    /// Answers the filesystem column for a drive letter. Defaults to
    /// <see cref="DriveVolumeFilesystemProbe.Instance"/>; a test substitutes a fake.
    /// </param>
    public ListCommand(
        IVirtualDiskService virtualDisks,
        Func<Result<MountRegistry>>? openRegistry = null,
        IVolumeFilesystemProbe? filesystems = null)
    {
        ArgumentNullException.ThrowIfNull(virtualDisks);

        _virtualDisks = virtualDisks;
        _openRegistry = openRegistry ?? (() => MountRegistry.ForCurrentUser());
        _filesystems = filesystems ?? DriveVolumeFilesystemProbe.Instance;
    }

    /// <inheritdoc />
    public string Verb => "list";

    /// <inheritdoc />
    public string Summary => "List the images dmg currently has mounted.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "list",
        [],
        [],
        [
            "Reconciles against Windows first: a mount a reboot silently dropped is never listed.",
            "Size is the scratch VHD's size on disk, which is the volume's decoded size plus its "
            + "small VHD footer - not the source .dmg's size.",
            "Filesystem is read from the drive letter, so a disk attached with no letter shows "
            + "'-' there rather than a guess.",
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

        if (arguments.Positionals.Count > 0)
        {
            context.Output.Error(DmgError.Usage(
                $"'list' takes no arguments, but {arguments.Positionals.Count} were given.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        Result<MountRegistry> opened = _openRegistry();

        if (!opened.TryGetValue(out MountRegistry? registry))
        {
            context.Output.Error(opened.Error);

            return opened.Error.Code;
        }

        IReadOnlyList<MountRecord> records = registry.Read(_virtualDisks);

        Report(context, records);

        return DmgExitCode.Success;
    }

    private void Report(CliContext context, IReadOnlyList<MountRecord> records)
    {
        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                new MountListPayload([.. records.Select(ToPayload)]),
                CliJson.Readable.MountListPayload));

            return;
        }

        if (records.Count == 0)
        {
            context.Output.WriteLine("No images are mounted. Mount one with 'dmg mount IMAGE'.");

            return;
        }

        foreach (string line in MountTable.Render(records.Select(ToRow)))
        {
            context.Output.WriteLine(line);
        }
    }

    private MountListEntryPayload ToPayload(MountRecord record) =>
        new(
            record.Id,
            record.DriveLetter,
            FilesystemOf(record),
            SizeOf(record),
            record.Mode,
            record.SourcePath,
            record.VhdPath,
            record.MountedAtUtc);

    private MountTable.Row ToRow(MountRecord record) =>
        new(
            record.Id,
            record.DescribeDriveLetter(),
            FilesystemOf(record),
            ByteSize.Format(SizeOf(record)),
            record.Mode,
            record.SourcePath);

    /// <summary>
    /// <c>unknown</c> for a mount with no drive letter, or one the probe could not
    /// read - never a guess.
    /// </summary>
    private string FilesystemOf(MountRecord record)
    {
        if (record.DriveLetter is not string letter)
        {
            return "-";
        }

        Result<string> filesystem = _filesystems.Filesystem(letter);

        return filesystem.TryGetValue(out string? name) ? name : "unknown";
    }

    /// <summary>The scratch VHD's size on disk, or zero when it cannot be read.</summary>
    private static long SizeOf(MountRecord record)
    {
        try
        {
            FileInfo info = new(record.VhdPath);

            return info.Exists ? info.Length : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A plain, aligned table: a header row, then one row per mount, columns
    /// padded to the widest value each holds.
    /// </summary>
    private static class MountTable
    {
        private static readonly string[] Headers = ["ID", "LETTER", "FILESYSTEM", "SIZE", "MODE", "SOURCE"];

        public static IEnumerable<string> Render(IEnumerable<Row> rows)
        {
            Row[] materialised = [.. rows];

            int[] widths = [.. Headers.Select((header, index) => Widest(header, materialised, index))];

            yield return FormatRow(Headers, widths);

            foreach (Row row in materialised)
            {
                yield return FormatRow(row.Columns, widths);
            }
        }

        private static int Widest(string header, IReadOnlyList<Row> rows, int column) =>
            rows.Count == 0
                ? header.Length
                : Math.Max(header.Length, rows.Max(row => row.Columns[column].Length));

        private static string FormatRow(IReadOnlyList<string> columns, IReadOnlyList<int> widths)
        {
            // The last column (source) is never padded: a long path trailing
            // spaces is not something anyone wants to pipe onward.
            return string.Join("  ", columns.Select((value, index) =>
                index == columns.Count - 1 ? value : value.PadRight(widths[index])));
        }

        /// <summary>One line of the table, already formatted as text.</summary>
        public readonly record struct Row(string Id, string Letter, string Filesystem, string Size, string Mode, string Source)
        {
            public string[] Columns => [Id, Letter, Filesystem, Size, Mode, Source];
        }
    }
}
