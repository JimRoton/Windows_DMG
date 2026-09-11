using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Cli.Output;

/// <summary>
/// What <c>dmg mount --json</c> puts on stdout.
/// </summary>
/// <remarks>
/// Flat, like <see cref="InfoPayload"/>: a script wants <c>.driveLetter</c> and
/// <c>.id</c> as fields, not something to scrape out of the human sentence. The
/// fields mirror <see cref="MountRecord"/> - this <i>is</i> the record dmg just
/// wrote to the mount registry, plus the two facts the registry does not carry:
/// the physical device Windows attached, and which partition was chosen.
/// </remarks>
/// <param name="Id">The short id <c>dmg unmount</c> takes.</param>
/// <param name="SourcePath">The image that was mounted.</param>
/// <param name="Partition">The partition number that was selected.</param>
/// <param name="VhdPath">The scratch VHD the volume was decoded to.</param>
/// <param name="DriveLetter">The letter Windows assigned, without a colon, or null when there is none.</param>
/// <param name="PhysicalPath">The device Windows created, e.g. <c>\\.\PhysicalDrive3</c>.</param>
/// <param name="Mode"><c>read-only</c> or <c>read-write</c>.</param>
/// <param name="MountedAtUtc">When the mount happened, in UTC.</param>
public sealed record MountPayload(
    string Id,
    string SourcePath,
    int Partition,
    string VhdPath,
    string? DriveLetter,
    string PhysicalPath,
    string Mode,
    DateTimeOffset MountedAtUtc)
{
    /// <summary>Builds the payload from a stored registry record and the attachment it came from.</summary>
    /// <param name="record">The record just written to the mount registry.</param>
    /// <param name="attachment">What the attach call produced.</param>
    /// <param name="partition">The partition number that was mounted.</param>
    public static MountPayload From(MountRecord record, VirtualDiskAttachment attachment, int partition)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(attachment);

        return new MountPayload(
            record.Id,
            record.SourcePath,
            partition,
            record.VhdPath,
            record.DriveLetter,
            attachment.PhysicalPath,
            attachment.ModeDescription,
            record.MountedAtUtc);
    }
}
