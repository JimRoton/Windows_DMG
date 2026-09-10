using System.Globalization;
using Dmg.Core.Partitions;

namespace Dmg.Core.Filesystems;

/// <summary>
/// One volume on a disk: where it is, and what is in it.
/// </summary>
/// <param name="Number">
/// The number the user sees and passes to <c>--partition</c>. Taken from the
/// partition table, so it is the same number <c>dmg info</c> prints.
/// </param>
/// <param name="Partition">The region of the disk the volume occupies.</param>
/// <param name="Filesystem">What the probe found in it.</param>
/// <param name="ProbeFailure">
/// Why the probe could not finish, when it could not. A volume that failed to
/// probe is never a mount candidate, but it is still listed - an image with one
/// damaged volume and one good one should still be usable.
/// </param>
public sealed record DiskVolume(
    int Number,
    PartitionEntry Partition,
    FilesystemInfo Filesystem,
    DmgError? ProbeFailure = null)
{
    /// <summary>True when this volume can be handed to Windows.</summary>
    public bool IsMountable => ProbeFailure is null && Filesystem.WindowsCanMount;

    /// <summary>The volume's byte offset from the start of the decoded disk.</summary>
    public long ByteOffset => Partition.ByteOffset;

    /// <summary>The volume's length in bytes.</summary>
    public long ByteLength => Partition.ByteLength;

    /// <summary>One line naming the volume, for listings and for error messages.</summary>
    public override string ToString()
    {
        string what = Partition.IsFreeSpace
            ? "free space"
            : ProbeFailure is not null
                ? "unreadable"
                : Filesystem.Kind == FilesystemKind.Unknown
                    ? "an unrecognised filesystem"
                    : Filesystem.ToString();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Number}: {what}, {ByteLength:N0} bytes at offset {ByteOffset:N0}");
    }
}

/// <summary>
/// Everything on a decoded disk, and the rule for picking the one to mount.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default is the single mountable volume.</b> Not the first, not the
/// largest: the only one. An image with one exFAT volume needs no argument, and an
/// image with two gets an error listing both rather than a guess - guessing which
/// half of somebody's disk they meant is not a service.
/// </para>
/// <para>
/// <b>A volume that will not probe does not sink the map.</b> Reading the
/// partition table is the only thing that can fail outright here; a partition
/// whose filesystem contradicts itself is recorded with its failure, listed like
/// any other, and refused if it is the one the user asks for. An image with one
/// damaged volume and one good one is still usable.
/// </para>
/// </remarks>
public sealed class VolumeMap
{
    private VolumeMap(PartitionTable table, IReadOnlyList<DiskVolume> volumes)
    {
        Table = table;
        Volumes = volumes;
    }

    /// <summary>The partition table the volumes came from.</summary>
    public PartitionTable Table { get; }

    /// <summary>Every volume on the disk, free space included, in table order.</summary>
    public IReadOnlyList<DiskVolume> Volumes { get; }

    /// <summary>The scheme the disk uses.</summary>
    public PartitionScheme Scheme => Table.Scheme;

    /// <summary>The volumes Windows could mount.</summary>
    public IReadOnlyList<DiskVolume> Mountable => [.. Volumes.Where(volume => volume.IsMountable)];

    /// <summary>Reads the partition table and probes every volume in it.</summary>
    /// <param name="disk">The fully decoded disk.</param>
    public static Result<VolumeMap> Read(Stream disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        Result<PartitionTable> read = PartitionTableReader.Read(disk);

        if (!read.TryGetValue(out PartitionTable? table))
        {
            return read.CastFailure<VolumeMap>();
        }

        List<DiskVolume> volumes = [];

        foreach (PartitionEntry partition in table.Partitions)
        {
            if (partition.IsFreeSpace || partition.SectorCount == 0)
            {
                volumes.Add(new DiskVolume(
                    partition.Number,
                    partition,
                    new FilesystemInfo { Kind = FilesystemKind.Unknown }));
                continue;
            }

            Result<FilesystemInfo> probed = FilesystemProbe.Probe(disk, partition);

            volumes.Add(probed.TryGetValue(out FilesystemInfo? info)
                ? new DiskVolume(partition.Number, partition, info)
                : new DiskVolume(
                    partition.Number,
                    partition,
                    new FilesystemInfo { Kind = FilesystemKind.Unknown },
                    probed.Error));
        }

        return Result<VolumeMap>.Success(new VolumeMap(table, volumes));
    }

    /// <summary>
    /// Picks the volume to mount: the one the user asked for, or the single
    /// mountable one when they asked for nothing.
    /// </summary>
    /// <param name="partitionNumber">
    /// The number given to <c>--partition</c>, or null for the default rule.
    /// </param>
    public Result<DiskVolume> Select(int? partitionNumber)
    {
        if (partitionNumber is int number)
        {
            return SelectExplicit(number);
        }

        IReadOnlyList<DiskVolume> mountable = Mountable;

        return mountable.Count switch
        {
            1 => Result<DiskVolume>.Success(mountable[0]),
            0 => Result<DiskVolume>.Failure(NothingMountable()),
            _ => Result<DiskVolume>.Failure(DmgError.Usage(
                $"This image contains {mountable.Count} volumes Windows can mount, so there is no "
                + "single obvious one. Choose with --partition: "
                + string.Join("; ", mountable.Select(volume => volume.ToString())) + ".",
                $"The disk uses {Describe(Scheme)} and declares {Volumes.Count} regions in all.")),
        };
    }

    /// <summary>The volume the user named, or the reason it cannot be mounted.</summary>
    /// <param name="number">The partition number given on the command line.</param>
    private Result<DiskVolume> SelectExplicit(int number)
    {
        DiskVolume? chosen = Volumes.FirstOrDefault(volume => volume.Number == number);

        if (chosen is null)
        {
            return Result<DiskVolume>.Failure(DmgError.Usage(
                $"This image has no partition {number.ToString(CultureInfo.InvariantCulture)}. "
                + (Volumes.Count == 0
                    ? "It declares no partitions at all."
                    : "It contains: " + string.Join("; ", Volumes.Select(volume => volume.ToString())) + "."),
                $"The disk uses {Describe(Scheme)}."));
        }

        if (chosen.ProbeFailure is not null)
        {
            return Result<DiskVolume>.Failure(chosen.ProbeFailure);
        }

        if (chosen.Partition.IsFreeSpace)
        {
            return Result<DiskVolume>.Failure(new DmgError(
                DmgExitCode.FilesystemNotMountable,
                $"Partition {number.ToString(CultureInfo.InvariantCulture)} of this image is free space, "
                + "not a volume, so there is nothing there to mount.",
                $"The entry is {chosen.Partition.TypeName}."));
        }

        return chosen.IsMountable
            ? Result<DiskVolume>.Success(chosen)
            : Result<DiskVolume>.Failure(
                FilesystemInfo.Refusal(chosen.Filesystem.Kind, chosen.Filesystem.VolumeLabel));
    }

    /// <summary>
    /// The refusal for an image with nothing Windows can mount, naming what is
    /// there instead.
    /// </summary>
    /// <remarks>
    /// When there is exactly one volume, its own refusal is the better message -
    /// it names the filesystem and points at <c>dmg extract</c> - so it is used
    /// verbatim rather than wrapped in a summary.
    /// </remarks>
    private DmgError NothingMountable()
    {
        List<DiskVolume> real = [.. Volumes.Where(volume => !volume.Partition.IsFreeSpace)];

        if (real.Count == 1 && real[0].ProbeFailure is null)
        {
            return FilesystemInfo.Refusal(real[0].Filesystem.Kind, real[0].Filesystem.VolumeLabel);
        }

        if (real.Count == 1)
        {
            return real[0].ProbeFailure!;
        }

        string found = real.Count == 0
            ? "it declares no volumes at all"
            : "it contains " + string.Join("; ", real.Select(volume => volume.ToString()));

        return new DmgError(
            DmgExitCode.FilesystemNotMountable,
            $"This image contains no volume Windows can mount: {found}. Use 'dmg extract' to copy files "
            + "out of it instead.",
            $"The disk uses {Describe(Scheme)} and declares {Volumes.Count} regions in all.");
    }

    /// <summary>The scheme's name, for the technical detail line.</summary>
    /// <param name="scheme">The partition scheme.</param>
    private static string Describe(PartitionScheme scheme) => scheme switch
    {
        PartitionScheme.WholeDisk => "no partition table",
        PartitionScheme.MasterBootRecord => "a master boot record",
        PartitionScheme.GuidPartitionTable => "a GUID partition table",
        PartitionScheme.ApplePartitionMap => "an Apple partition map",
        _ => "an unknown partitioning scheme",
    };
}
