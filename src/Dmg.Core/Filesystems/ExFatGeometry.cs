using System.Buffers.Binary;
using System.Globalization;

namespace Dmg.Core.Filesystems;

/// <summary>
/// An exFAT volume's layout, validated once and then trusted: where the FAT is,
/// where the cluster heap starts, how big a cluster is, and where the root
/// directory begins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists separately from <see cref="ExFatProbe"/>.</b> The probe needs
/// the geometry only long enough to find the volume label, so it computed these
/// numbers into local variables and threw them away;
/// <see cref="FilesystemInfo"/> keeps the sector and cluster sizes and nothing
/// else. Projecting the volume's contents (<a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>)
/// needs the whole layout, and validating it in a second place would mean two
/// implementations of the same hostile-input checks drifting apart. So the layout
/// is parsed and checked here, once, and everything that needs it asks this type.
/// </para>
/// <para>
/// <b>Every number here came out of the image.</b> The boot sector is written by
/// whoever made the file, so the FAT, the cluster heap and the root directory are
/// all checked to lie inside the volume - and inside each other - before any of
/// them is read. After <see cref="Parse"/> returns a geometry, its offsets are safe
/// to compute with; that is the whole point of the type.
/// </para>
/// </remarks>
internal sealed class ExFatGeometry
{
    /// <summary>The signature at offset 3 of the boot sector.</summary>
    internal static ReadOnlySpan<byte> FileSystemName => "EXFAT   "u8;

    /// <summary>The first cluster number the heap can address. 0 and 1 are reserved.</summary>
    internal const uint FirstDataCluster = 2;

    /// <summary>The FAT entry marking the end of a cluster chain.</summary>
    internal const uint EndOfChain = 0xFFFFFFFF;

    private ExFatGeometry(
        int bytesPerSector,
        int sectorsPerCluster,
        ulong volumeLengthSectors,
        uint fatOffsetSectors,
        uint fatLengthSectors,
        uint clusterHeapOffsetSectors,
        uint clusterCount,
        uint rootDirectoryCluster,
        uint volumeSerial,
        ushort revision,
        byte fatCount)
    {
        BytesPerSector = bytesPerSector;
        SectorsPerCluster = sectorsPerCluster;
        VolumeLengthSectors = volumeLengthSectors;
        FatOffsetSectors = fatOffsetSectors;
        FatLengthSectors = fatLengthSectors;
        ClusterHeapOffsetSectors = clusterHeapOffsetSectors;
        ClusterCount = clusterCount;
        RootDirectoryCluster = rootDirectoryCluster;
        VolumeSerial = volumeSerial;
        Revision = revision;
        FatCount = fatCount;
    }

    /// <summary>Bytes in one sector: 512 to 4096.</summary>
    internal int BytesPerSector { get; }

    /// <summary>Sectors in one cluster, a power of two.</summary>
    internal int SectorsPerCluster { get; }

    /// <summary>Bytes in one cluster. The unit everything in the heap is measured in.</summary>
    internal long BytesPerCluster => (long)BytesPerSector * SectorsPerCluster;

    /// <summary>The volume's length in sectors, as the boot sector declares it.</summary>
    internal ulong VolumeLengthSectors { get; }

    /// <summary>Where the first FAT starts, in sectors from the start of the volume.</summary>
    internal uint FatOffsetSectors { get; }

    /// <summary>How many sectors one FAT occupies.</summary>
    internal uint FatLengthSectors { get; }

    /// <summary>Where the cluster heap starts, in sectors from the start of the volume.</summary>
    internal uint ClusterHeapOffsetSectors { get; }

    /// <summary>How many clusters the heap holds.</summary>
    internal uint ClusterCount { get; }

    /// <summary>The first cluster of the root directory.</summary>
    internal uint RootDirectoryCluster { get; }

    /// <summary>The volume serial number, as <c>dmg info</c> reports it.</summary>
    internal uint VolumeSerial { get; }

    /// <summary>The filesystem revision from the boot sector.</summary>
    internal ushort Revision { get; }

    /// <summary>How many FATs the volume carries: 1, or 2 for TexFAT.</summary>
    internal byte FatCount { get; }

    /// <summary>The cluster number one past the last addressable cluster.</summary>
    internal uint ClusterLimit => ClusterCount + FirstDataCluster;

    /// <summary>
    /// Reads and validates the layout from a boot sector.
    /// </summary>
    /// <param name="boot">The volume's first sector, at least 512 bytes.</param>
    /// <returns>
    /// The geometry, or <see cref="DmgExitCode.CorruptImage"/> naming the field that
    /// made no sense.
    /// </returns>
    internal static Result<ExFatGeometry> Parse(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 512 || !boot.Slice(3, 8).SequenceEqual(FileSystemName))
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume claims to be exFAT but its boot sector does not say so.",
                "The EXFAT signature is missing from offset 3."));
        }

        // exFAT reuses the bytes a FAT BIOS parameter block would occupy and
        // requires them to be zero. A writer that filled them in produced
        // something no exFAT driver will mount.
        if (boot[11..64].ContainsAnyExcept((byte)0))
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector has a BIOS parameter block in it.",
                "Bytes 11-63 must be zero in exFAT; this volume has data there."));
        }

        byte sectorShift = boot[108];
        byte clusterShift = boot[109];

        if (sectorShift is < 9 or > 12)
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible sector size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSectorShift={sectorShift}; it must be between 9 (512) and 12 (4096).")));
        }

        if (sectorShift + clusterShift > 25)
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible cluster size.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"BytesPerSectorShift={sectorShift} plus SectorsPerClusterShift={clusterShift} exceeds the 32 MB maximum.")));
        }

        int bytesPerSector = 1 << sectorShift;
        int sectorsPerCluster = 1 << clusterShift;

        ulong volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(boot[72..]);
        uint fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]);
        uint fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]);
        uint heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]);
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot[92..]);
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]);
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(boot[100..]);
        ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(boot[104..]);
        byte fatCount = boot[110];

        if (fatCount is not (1 or 2))
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT boot sector declares an impossible number of FATs.",
                string.Create(CultureInfo.InvariantCulture, $"NumberOfFats={fatCount}; exFAT allows 1 or 2.")));
        }

        if (fatOffset < 24 || fatLength == 0 || heapOffset < (ulong)fatOffset + fatLength)
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT layout overlaps itself.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FatOffset={fatOffset}, FatLength={fatLength}, ClusterHeapOffset={heapOffset}.")));
        }

        if (volumeLength < (ulong)heapOffset + ((ulong)clusterCount * (ulong)sectorsPerCluster))
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT cluster heap does not fit in the volume it declares.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"VolumeLength={volumeLength} sectors, ClusterHeapOffset={heapOffset}, "
                    + $"ClusterCount={clusterCount}, {sectorsPerCluster} sectors per cluster.")));
        }

        if (rootCluster < FirstDataCluster || rootCluster >= (ulong)clusterCount + FirstDataCluster)
        {
            return Result<ExFatGeometry>.Failure(DmgError.Corrupt(
                "This volume's exFAT root directory is outside its cluster heap.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FirstClusterOfRootDirectory={rootCluster}, ClusterCount={clusterCount}.")));
        }

        return Result<ExFatGeometry>.Success(new ExFatGeometry(
            bytesPerSector,
            sectorsPerCluster,
            volumeLength,
            fatOffset,
            fatLength,
            heapOffset,
            clusterCount,
            rootCluster,
            serial,
            revision,
            fatCount));
    }

    /// <summary>Whether <paramref name="cluster"/> addresses a real cluster in the heap.</summary>
    internal bool IsDataCluster(uint cluster) =>
        cluster >= FirstDataCluster && cluster < ClusterLimit;

    /// <summary>
    /// The byte offset of <paramref name="cluster"/> from the start of the volume.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cluster"/> is not a data cluster. Callers check with
    /// <see cref="IsDataCluster"/> and report a corrupt image; reaching here with a
    /// bad cluster is a bug in the caller, not a bad image.
    /// </exception>
    internal long ClusterOffset(uint cluster)
    {
        if (!IsDataCluster(cluster))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cluster),
                cluster,
                $"Cluster {cluster} is outside the heap's {FirstDataCluster}..{ClusterLimit - 1}.");
        }

        return ((long)ClusterHeapOffsetSectors + ((long)(cluster - FirstDataCluster) * SectorsPerCluster))
            * BytesPerSector;
    }

    /// <summary>
    /// Reads the FAT entry for <paramref name="cluster"/>: the next cluster in its
    /// chain, or <see cref="EndOfChain"/>.
    /// </summary>
    /// <param name="volume">The volume to read the FAT from.</param>
    /// <param name="cluster">The cluster whose successor is wanted.</param>
    internal Result<uint> NextCluster(VolumeReader volume, uint cluster)
    {
        ArgumentNullException.ThrowIfNull(volume);

        long entryOffset = ((long)FatOffsetSectors * BytesPerSector) + ((long)cluster * 4);

        if (entryOffset + 4 > ((long)FatOffsetSectors + FatLengthSectors) * BytesPerSector)
        {
            return Result<uint>.Failure(DmgError.Corrupt(
                "This volume's exFAT cluster chain points outside its own FAT.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cluster {cluster} has no entry in a {FatLengthSectors}-sector FAT.")));
        }

        Result<byte[]> read = volume.Read(entryOffset, 4, "file allocation table");

        return read.TryGetValue(out byte[]? bytes)
            ? Result<uint>.Success(BinaryPrimitives.ReadUInt32LittleEndian(bytes))
            : read.CastFailure<uint>();
    }
}
