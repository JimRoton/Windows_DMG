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
