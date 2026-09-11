using System.Globalization;
using Dmg.Cli.Output;
using Dmg.Core.Diagnostics;
using Dmg.Core.Partitions;

namespace Dmg.Cli.Info;

/// <summary>
/// An <see cref="ImageReport"/> as the screen a person reads.
/// </summary>
/// <remarks>
/// <para>
/// A label column and a value column, in the order the questions get asked: what is
/// this file, can it be opened, how big is it really, what is it made of, and could
/// Windows mount it. The shape is the one in the CLI design document, and it is
/// deliberately narrow enough to survive an 80-column terminal.
/// </para>
/// <para>
/// <b>A refusal gets a sentence, not a code.</b> "NOT MOUNTABLE" alone tells a user
/// nothing they can act on, so the reason follows it on its own indented line -
/// which for HFS+ is "Windows has no HFS+ driver" and a suggestion of what to do
/// instead. That sentence comes from <c>FilesystemInfo.MountRefusal</c>, so the
/// wording is the same one <c>dmg mount</c> would fail with.
/// </para>
/// </remarks>
public static class ImageReportText
{
    private const int LabelWidth = 15;
    private const string Indent = "  ";


    /// <summary>Writes the report to stdout.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="report">What was found.</param>
    public static void WriteTo(IOutput output, ImageReport report)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(report);

        foreach (string line in Render(report))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>The report as lines, without a sink in the way.</summary>
    /// <param name="report">What was found.</param>
    public static IReadOnlyList<string> Render(ImageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<string> lines =
        [
            Row("file", $"{report.FileName}  ({ByteSize.Format(report.FileBytes)} on disk)"),
            Row("container", report.Container),
            Row("encryption", report.Encryption.Description),
            Row("decoded size", $"{ByteSize.Format(report.DecodedBytes)}  ({ByteSize.Count(report.SectorCount)} sectors)"),
        ];

        if (report.ChunkCount > 0)
        {
            lines.Add(Row("chunks", Chunks(report)));
        }

        AppendPartitions(lines, report);

        return lines;
    }

    private static void AppendPartitions(List<string> lines, ImageReport report)
    {
        if (report.PartitioningNote is { } note)
        {
            lines.Add(Row("partitioning", "not read"));
            lines.AddRange(Continuation(note.Message));

            return;
        }

        if (report.Partitioning is not PartitionScheme scheme)
        {
            return;
        }

        lines.Add(Row("partitioning", PartitionSchemeName.Display(scheme)));

        if (report.Volumes.Count == 0)
        {
            lines.Add(Row("partitions", "none"));

            return;
        }

        bool first = true;

        foreach (VolumeUsage volume in report.Volumes)
        {
            lines.Add(Row(first ? "partitions" : string.Empty, Describe(volume, report.Volumes.Count)));
            first = false;

            if (volume.Refusal is { } refusal)
            {
                lines.AddRange(Continuation(refusal));
            }
        }
    }

    /// <summary>
    /// <c>2,462   zlib 2,301 · raw 44 · zero-fill 117</c> - the count first, because
    /// that is the number that says how much work a mount would be, then the
    /// breakdown that says whether it is possible at all.
    /// </summary>
    private static string Chunks(ImageReport report)
    {
        string breakdown = string.Join(
            " · ",
            report.Codecs.Select(codec => codec.IsSupported
                ? $"{codec.Name} {ByteSize.Count(codec.Chunks)}"
                : $"{codec.Name} {ByteSize.Count(codec.Chunks)} (NOT SUPPORTED)"));

        return $"{ByteSize.Count(report.ChunkCount)}   {breakdown}";
    }

    /// <summary>
    /// <c>1  ·  exFAT "Installer"  2.40 GiB  -&gt;  mountable</c>
    /// </summary>
    private static string Describe(VolumeUsage volume, int total)
    {
        string number = total == 1 ? "1" : volume.Number.ToString(CultureInfo.InvariantCulture);
        string label = volume.VolumeLabel is { } text
            ? $" \"{text}\""
            : volume.PartitionName is { } named && !volume.IsFreeSpace
                ? $" \"{named}\""
                : string.Empty;
        string verdict = volume.IsFreeSpace
            ? string.Empty
            : volume.CanMount ? "  ->  mountable" : "  ->  NOT MOUNTABLE";

        return $"{number}  ·  {volume.Filesystem}{label}  {ByteSize.Format(volume.Bytes)}{verdict}";
    }

    private static string Row(string label, string value) =>
        Indent + label.PadRight(LabelWidth) + value;

    /// <summary>
    /// The lines under a row, indented past the label column so they read as
    /// belonging to the row above rather than as rows of their own, and wrapped so
    /// that a refusal sentence - which is a whole sentence, by design - does not
    /// run off the edge and fold at whatever column the terminal happens to have.
    /// </summary>
    private static IEnumerable<string> Continuation(string text) =>
        TextWrap.Indent(text, Indent + new string(' ', LabelWidth) + Indent);
}
