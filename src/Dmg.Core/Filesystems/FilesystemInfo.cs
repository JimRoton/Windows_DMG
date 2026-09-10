using System.Globalization;

namespace Dmg.Core.Filesystems;

/// <summary>
/// What a probe found inside a volume: which filesystem, what it is called, and
/// whether Windows can mount it.
/// </summary>
/// <remarks>
/// <para>
/// This is the record <c>dmg info</c> prints and <c>dmg mount</c> decides on, so
/// it carries the two things a user recognises their disk by - the label and the
/// serial - rather than only the geometry. A field the format does not have, or
/// that this build does not read, is null; nothing here is invented.
/// </para>
/// <para>
/// <see cref="WindowsCanMount"/> is the whole point of the type. The product's
/// premise is that Windows already has the driver, so the tool's job is to say
/// which volumes that is true for and to refuse the rest by name.
/// </para>
/// </remarks>
public sealed record FilesystemInfo
{
    /// <summary>The filesystem found.</summary>
    public required FilesystemKind Kind { get; init; }

    /// <summary>The filesystem's name as it should be printed - <c>exFAT</c>, <c>HFS+</c>.</summary>
    public string Name => FilesystemSignature.Describe(Kind);

    /// <summary>
    /// The volume label, or null when the volume has none or this build does not
    /// read labels for this filesystem. Never a placeholder.
    /// </summary>
    public string? VolumeLabel { get; init; }

    /// <summary>The volume serial as the format's own tools print it, or null.</summary>
    public string? VolumeSerial { get; init; }

    /// <summary>The filesystem's own sector size, which need not be the disk's 512.</summary>
    public int BytesPerSector { get; init; } = 512;

    /// <summary>The allocation unit in bytes, or zero when the format has no clusters.</summary>
    public long BytesPerCluster { get; init; }

    /// <summary>The volume's length in bytes as the filesystem itself declares it.</summary>
    public long VolumeBytes { get; init; }

    /// <summary>True when Windows has a driver for this filesystem.</summary>
    public bool WindowsCanMount => FilesystemSignature.WindowsCanMount(Kind);

    /// <summary>
    /// Why this volume cannot be mounted, or null when it can be.
    /// </summary>
    /// <remarks>
    /// The message names the filesystem on purpose. "Unsupported filesystem"
    /// leaves a user with nowhere to go; "this image contains an HFS+ volume,
    /// which Windows cannot mount" tells them why it will never work and what to
    /// reach for instead, which is the whole difference between a refusal that
    /// helps and one that does not.
    /// </remarks>
    public DmgError? MountRefusal => WindowsCanMount ? null : Refusal(Kind, VolumeLabel);

    /// <summary>
    /// Fails with <see cref="DmgExitCode.FilesystemNotMountable"/> when Windows has
    /// no driver for this volume, and succeeds when it has.
    /// </summary>
    public Result EnsureWindowsCanMount()
    {
        DmgError? refusal = MountRefusal;

        return refusal is null ? Result.Success() : Result.Failure(refusal);
    }

    /// <summary>The refusal for a filesystem Windows cannot mount.</summary>
    /// <param name="kind">The filesystem found.</param>
    /// <param name="label">The volume label, when one is known.</param>
    public static DmgError Refusal(FilesystemKind kind, string? label = null)
    {
        string named = string.IsNullOrEmpty(label) ? string.Empty : $" \"{label}\"";

        return kind == FilesystemKind.Unknown
            ? new DmgError(
                DmgExitCode.FilesystemNotMountable,
                $"This image contains a volume{named} in a format this build does not recognise, so it "
                + "cannot be handed to Windows to mount. Use 'dmg info' to see what the image declares.",
                "No exFAT, FAT, NTFS, HFS+ or APFS structure was found at the start of the volume.")
            : new DmgError(
                DmgExitCode.FilesystemNotMountable,
                $"This image contains a{Article(kind)} {FilesystemSignature.Describe(kind)} volume{named}, "
                + "which Windows cannot mount. Use 'dmg extract' to copy files out of it instead.",
                $"{FilesystemSignature.Describe(kind)} is an Apple filesystem and Windows ships no driver "
                + "for it, so attaching the image as a virtual disk would produce an unreadable volume.");
    }

    /// <summary>The indefinite article for a filesystem's name, so the message reads as English.</summary>
    /// <param name="kind">The filesystem.</param>
    private static string Article(FilesystemKind kind) =>
        kind is FilesystemKind.Apfs or FilesystemKind.HfsPlus or FilesystemKind.Hfs ? "n" : string.Empty;

    /// <summary>One line, the way <c>dmg info</c> prints it.</summary>
    public override string ToString()
    {
        string label = string.IsNullOrEmpty(VolumeLabel) ? "unlabelled" : $"\"{VolumeLabel}\"";
        string serial = string.IsNullOrEmpty(VolumeSerial) ? string.Empty : $", serial {VolumeSerial}";

        return string.Create(CultureInfo.InvariantCulture, $"{Name} {label}{serial}");
    }

    /// <summary>Formats a 32-bit volume serial the way Windows and hdiutil print it.</summary>
    /// <param name="serial">The raw serial from the boot sector.</param>
    public static string FormatSerial(uint serial) =>
        string.Create(CultureInfo.InvariantCulture, $"{serial >> 16:X4}-{serial & 0xFFFF:X4}");
}
