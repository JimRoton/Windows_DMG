using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dmg.Core.Filesystems;

/// <summary>
/// One entry in an exFAT directory: a file or a subdirectory, with everything the
/// projection needs to list it and to read it.
/// </summary>
/// <param name="Name">The file name, already assembled from its name fragments.</param>
/// <param name="IsDirectory">True when this entry is a directory.</param>
/// <param name="Length">The data length in bytes. Zero for an empty file.</param>
/// <param name="FirstCluster">The first cluster of the data, or 0 when there is none.</param>
/// <param name="IsContiguous">
/// True when the entry's <c>NoFatChain</c> flag is set, meaning its clusters run
/// consecutively and the FAT must not be consulted for them.
/// </param>
/// <param name="Created">Creation time, or null when the stamp was not a real date.</param>
/// <param name="Modified">Last-modified time, or null.</param>
/// <param name="IsReadOnly">The read-only attribute.</param>
/// <param name="IsHidden">The hidden attribute.</param>
public sealed record ExFatEntry(
    string Name,
    bool IsDirectory,
    long Length,
    uint FirstCluster,
    bool IsContiguous,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool IsReadOnly,
    bool IsHidden);

/// <summary>
/// Reads an exFAT volume's directories and file data, read-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The VHD route hands sectors to Microsoft's exFAT driver
/// and never parses a directory. Projecting a volume's contents
/// (<a href="../../../docs/adr/ADR-008-projfs-projection-head.md">ADR-008</a>)
/// cannot: something has to turn clusters into names and file extents, and on the
/// projection route that something is this. It is the cost ADR-003 named when it
/// rejected a user-mode filesystem - halved, because this reads and never writes,
/// so a bug here cannot corrupt anyone's volume.
/// </para>
/// <para>
/// <b>A directory is three entries, not one.</b> exFAT spreads a single file across
/// a set: a <c>0x85</c> File entry carrying attributes and timestamps, a
/// <c>0xC0</c> Stream Extension carrying the length and first cluster, and one or
/// more <c>0xC1</c> File Name entries carrying fifteen UTF-16 characters each. A
/// set is only meaningful complete, so a set that is cut short by the end of the
/// directory is skipped rather than half-believed.
/// </para>
/// <para>
/// <b>Two ways to find the next cluster.</b> With <c>NoFatChain</c> set the file's
/// clusters are consecutive and the FAT holds nothing for them - following it
/// anyway reads a stale entry and lands somewhere arbitrary. With it clear the
/// chain is in the FAT. Both are implemented; the flag decides, per entry.
/// </para>
/// <para>
/// <b>Every bound is checked against the volume.</b> Cluster numbers, name lengths
/// and data lengths all come out of the image. A directory that claims a million
/// clusters, a name that claims 4 GB or a chain that loops back on itself is a
/// corrupt image and is reported as one, not a hang.
/// </para>
/// </remarks>
public sealed class ExFatReader
{
    /// <summary>File Directory Entry: attributes and timestamps.</summary>
    internal const byte FileEntry = 0x85;

    /// <summary>Stream Extension Directory Entry: length and first cluster.</summary>
    internal const byte StreamExtensionEntry = 0xC0;

    /// <summary>File Name Directory Entry: fifteen UTF-16 characters.</summary>
    internal const byte FileNameEntry = 0xC1;

    /// <summary>End of directory. Nothing after this is meaningful.</summary>
    internal const byte EndOfDirectory = 0x00;

    /// <summary>Set on an entry type byte while the entry is in use.</summary>
    internal const byte InUseFlag = 0x80;

    /// <summary>Characters one <see cref="FileNameEntry"/> carries.</summary>
    internal const int NameCharactersPerEntry = 15;

    /// <summary>The longest name exFAT allows.</summary>
    internal const int MaxNameLength = 255;

    /// <summary>Bit 4 of FileAttributes: this entry is a directory.</summary>
    private const ushort AttributeDirectory = 0x0010;

    /// <summary>Bit 0 of FileAttributes: read-only.</summary>
    private const ushort AttributeReadOnly = 0x0001;

    /// <summary>Bit 1 of FileAttributes: hidden.</summary>
    private const ushort AttributeHidden = 0x0002;

    /// <summary>Bit 1 of GeneralSecondaryFlags: the clusters are consecutive.</summary>
    private const byte NoFatChainFlag = 0x02;

    /// <summary>
    /// The most clusters any one chain will be walked for. A directory or file
    /// longer than this is refused rather than followed, so a chain that loops
    /// cannot spin forever.
    /// </summary>
    internal const int MaxClustersPerChain = 1 << 20;

    /// <summary>The most entries one directory may hold, to bound a listing.</summary>
    internal const int MaxEntriesPerDirectory = 1 << 18;

    private readonly VolumeReader _volume;
    private readonly ExFatGeometry _geometry;

    /// <summary>Opens a reader over a volume whose geometry is already parsed.</summary>
    /// <param name="volume">The volume window.</param>
    /// <param name="geometry">The validated layout.</param>
    internal ExFatReader(VolumeReader volume, ExFatGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(geometry);

        _volume = volume;
        _geometry = geometry;
    }

    /// <summary>
    /// Opens a reader over the exFAT volume at <paramref name="volumeOffset"/> in
    /// <paramref name="disk"/>.
    /// </summary>
    /// <param name="disk">
    /// The decoded disk - a <c>DmgBlockStream</c>, an <c>EncryptedBlockStream</c>, or
    /// anything else readable and seekable. Reads through this reader are clipped to
    /// the volume, so the rest of the disk is unreachable from here.
    /// </param>
    /// <param name="volumeOffset">The volume's byte offset from the start of the disk.</param>
    /// <param name="volumeLength">The volume's length in bytes.</param>
    /// <returns>
    /// The reader, or the failure that makes the volume unreadable: a boot sector
    /// that is not exFAT, or a layout that does not fit inside itself.
    /// </returns>
    /// <remarks>
    /// This is the entry point <c>Dmg.Windows</c>' projection head uses. Opening
    /// reads exactly one sector; nothing else is touched until a directory is listed
    /// or a file is read, which is the whole point of projecting rather than
    /// materialising.
    /// </remarks>
    public static Result<ExFatReader> Open(Stream disk, long volumeOffset, long volumeLength)
    {
        ArgumentNullException.ThrowIfNull(disk);

        VolumeReader volume = new(disk, volumeOffset, volumeLength);

        Result<byte[]> boot = volume.Read(0, 512, "boot sector");

        if (!boot.TryGetValue(out byte[]? sector))
        {
            return boot.CastFailure<ExFatReader>();
        }

        Result<ExFatGeometry> parsed = ExFatGeometry.Parse(sector);

        return parsed.TryGetValue(out ExFatGeometry? layout)
            ? Result<ExFatReader>.Success(new ExFatReader(volume, layout))
            : parsed.CastFailure<ExFatReader>();
    }

    /// <summary>The volume's layout.</summary>
    internal ExFatGeometry Geometry => _geometry;

    /// <summary>Lists the root directory.</summary>
    public Result<IReadOnlyList<ExFatEntry>> ReadRootDirectory() =>
        ReadDirectory(_geometry.RootDirectoryCluster, contiguous: false, length: 0);

    /// <summary>Lists the entries of the directory starting at <paramref name="firstCluster"/>.</summary>
    /// <param name="firstCluster">The directory's first cluster.</param>
    /// <param name="contiguous">The directory's <c>NoFatChain</c> flag.</param>
    /// <param name="length">
    /// The directory's declared length in bytes, or 0 to walk until the chain ends.
    /// The root directory declares no length, which is why 0 is meaningful here.
    /// </param>
    public Result<IReadOnlyList<ExFatEntry>> ReadDirectory(uint firstCluster, bool contiguous, long length)
    {
        Result<byte[]> content = ReadChain(firstCluster, contiguous, length, "directory");

        if (!content.TryGetValue(out byte[]? bytes))
        {
            return content.CastFailure<IReadOnlyList<ExFatEntry>>();
        }

        List<ExFatEntry> entries = [];

        for (int offset = 0; offset + 32 <= bytes.Length; offset += 32)
        {
            byte type = bytes[offset];

            if (type == EndOfDirectory)
            {
                break;
            }

            // The in-use bit is clear on a deleted entry. It is not an error; the
            // entry is simply not there any more.
            if ((type & InUseFlag) == 0 || type != FileEntry)
            {
                continue;
            }

            if (entries.Count >= MaxEntriesPerDirectory)
            {
                return Result<IReadOnlyList<ExFatEntry>>.Failure(DmgError.Corrupt(
                    "This volume has a directory with an implausible number of entries in it.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Stopped after {MaxEntriesPerDirectory} entries.")));
            }

            Result<ExFatEntry?> parsed = ParseEntrySet(bytes, ref offset);

            if (!parsed.TryGetValue(out ExFatEntry? entry))
            {
                return parsed.CastFailure<IReadOnlyList<ExFatEntry>>();
            }

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return Result<IReadOnlyList<ExFatEntry>>.Success(entries);
    }

    /// <summary>
    /// Reads <paramref name="count"/> bytes of <paramref name="entry"/>'s data from
    /// <paramref name="offset"/> within the file.
    /// </summary>
    /// <param name="entry">The file to read. A directory is refused.</param>
    /// <param name="offset">The offset within the file.</param>
    /// <param name="count">How many bytes to read. Clipped to the file's length.</param>
    /// <returns>
    /// The bytes, which may be shorter than <paramref name="count"/> at the end of
    /// the file, or empty when <paramref name="offset"/> is past the end.
    /// </returns>
    public Result<byte[]> ReadFile(ExFatEntry entry, long offset, int count)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (entry.IsDirectory)
        {
            return Result<byte[]>.Failure(DmgError.Usage(
                $"'{entry.Name}' is a directory, not a file.",
                "Directories are listed, not read as data."));
        }

        if (offset >= entry.Length || count == 0)
        {
            return Result<byte[]>.Success([]);
        }

        long wanted = Math.Min(count, entry.Length - offset);

        long clusterBytes = _geometry.BytesPerCluster;
        long firstIndex = offset / clusterBytes;
        long lastIndex = (offset + wanted - 1) / clusterBytes;

        if (lastIndex - firstIndex + 1 > MaxClustersPerChain)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                "This read spans an implausible number of clusters.",
                string.Create(CultureInfo.InvariantCulture, $"{lastIndex - firstIndex + 1} clusters.")));
        }

        Result<uint[]> chain = ResolveClusters(
            entry.FirstCluster,
            entry.IsContiguous,
            lastIndex + 1,
            entry.Name);

        if (!chain.TryGetValue(out uint[]? clusters))
        {
            return chain.CastFailure<byte[]>();
        }

        byte[] result = new byte[wanted];
        int written = 0;

        for (long index = firstIndex; index <= lastIndex; index++)
        {
            long withinCluster = index == firstIndex ? offset - (firstIndex * clusterBytes) : 0;
            long take = Math.Min(clusterBytes - withinCluster, wanted - written);

            Result<byte[]> read = _volume.Read(
                _geometry.ClusterOffset(clusters[index]) + withinCluster,
                (int)take,
                "file data");

            if (!read.TryGetValue(out byte[]? chunk))
            {
                return read.CastFailure<byte[]>();
            }

            chunk.CopyTo(result.AsSpan(written));
            written += (int)take;
        }

        return Result<byte[]>.Success(result);
    }

    /// <summary>
    /// Parses one File entry and the secondaries that belong to it, advancing
    /// <paramref name="offset"/> past the whole set.
    /// </summary>
    /// <returns>
    /// The entry, or null when the set is incomplete or malformed in a way that
    /// makes it meaningless rather than the image corrupt.
    /// </returns>
    private Result<ExFatEntry?> ParseEntrySet(byte[] bytes, ref int offset)
    {
        ReadOnlySpan<byte> file = bytes.AsSpan(offset, 32);

        int secondaries = file[1];
        ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        uint created = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint modified = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);

        // A set is the File entry plus its secondaries. One of those must be the
        // stream extension, so fewer than one secondary is not a file at all.
        if (secondaries < 1)
        {
            return Result<ExFatEntry?>.Success(null);
        }

        int setEnd = offset + ((secondaries + 1) * 32);

        if (setEnd > bytes.Length)
        {
            // Cut short by the end of the directory. Nothing usable follows.
            offset = bytes.Length;
            return Result<ExFatEntry?>.Success(null);
        }

        int streamOffset = offset + 32;
        ReadOnlySpan<byte> stream = bytes.AsSpan(streamOffset, 32);

        if (stream[0] != StreamExtensionEntry)
        {
            offset = setEnd - 32;
            return Result<ExFatEntry?>.Success(null);
        }

        byte secondaryFlags = stream[1];
        int nameLength = stream[3];
        uint firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(stream[20..]);
        ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(stream[24..]);

        if (nameLength is < 1 or > MaxNameLength)
        {
            return Result<ExFatEntry?>.Failure(DmgError.Corrupt(
                "This volume has a directory entry whose name length is impossible.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"NameLength={nameLength}; exFAT allows 1 to {MaxNameLength}.")));
        }

        if (dataLength > long.MaxValue)
        {
            return Result<ExFatEntry?>.Failure(DmgError.Corrupt(
                "This volume has a directory entry claiming an impossible length.",
                string.Create(CultureInfo.InvariantCulture, $"DataLength={dataLength}.")));
        }

        StringBuilder name = new(nameLength);

        for (int index = 1; index < secondaries; index++)
        {
            ReadOnlySpan<byte> fragment = bytes.AsSpan(offset + ((index + 1) * 32), 32);

            if (fragment[0] != FileNameEntry)
            {
                continue;
            }

            int remaining = nameLength - name.Length;
            int take = Math.Min(NameCharactersPerEntry, remaining);

            if (take <= 0)
            {
                break;
            }

            name.Append(Encoding.Unicode.GetString(fragment.Slice(2, take * 2)));
        }

        offset = setEnd - 32;

        if (name.Length != nameLength)
        {
            // The name entries did not add up to the length the stream declared.
            return Result<ExFatEntry?>.Success(null);
        }

        bool isDirectory = (attributes & AttributeDirectory) != 0;

        if (dataLength > 0 && !_geometry.IsDataCluster(firstCluster))
        {
            return Result<ExFatEntry?>.Failure(DmgError.Corrupt(
                "This volume has a directory entry whose data starts outside the cluster heap.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' starts at cluster {firstCluster}; the heap holds {_geometry.ClusterCount}.")));
        }

        return Result<ExFatEntry?>.Success(new ExFatEntry(
            name.ToString(),
            isDirectory,
            (long)dataLength,
            firstCluster,
            (secondaryFlags & NoFatChainFlag) != 0,
            ToTimestamp(created),
            ToTimestamp(modified),
            (attributes & AttributeReadOnly) != 0,
            (attributes & AttributeHidden) != 0));
    }

    /// <summary>Reads a whole cluster chain into memory.</summary>
    /// <param name="firstCluster">The chain's first cluster.</param>
    /// <param name="contiguous">The entry's <c>NoFatChain</c> flag.</param>
    /// <param name="length">The declared length, or 0 to follow the chain to its end.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    private Result<byte[]> ReadChain(uint firstCluster, bool contiguous, long length, string what)
    {
        if (length == 0 && !_geometry.IsDataCluster(firstCluster))
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"This volume's {what} starts outside the cluster heap.",
                string.Create(CultureInfo.InvariantCulture, $"Cluster {firstCluster}.")));
        }

        long clusterBytes = _geometry.BytesPerCluster;
        long clustersWanted = length == 0
            ? MaxClustersPerChain
            : (length + clusterBytes - 1) / clusterBytes;

        Result<uint[]> resolved = ResolveClusters(firstCluster, contiguous, clustersWanted, what);

        if (!resolved.TryGetValue(out uint[]? clusters))
        {
            return resolved.CastFailure<byte[]>();
        }

        long total = length == 0 ? clusters.Length * clusterBytes : length;

        if (total > int.MaxValue)
        {
            return Result<byte[]>.Failure(DmgError.Corrupt(
                $"This volume's {what} is too large to read in one go.",
                string.Create(CultureInfo.InvariantCulture, $"{total} bytes.")));
        }

        byte[] buffer = new byte[total];
        int written = 0;

        foreach (uint cluster in clusters)
        {
            int take = (int)Math.Min(clusterBytes, total - written);

            if (take <= 0)
            {
                break;
            }

            Result<byte[]> read = _volume.Read(_geometry.ClusterOffset(cluster), take, what);

            if (!read.TryGetValue(out byte[]? chunk))
            {
                return read.CastFailure<byte[]>();
            }

            chunk.CopyTo(buffer.AsSpan(written));
            written += take;
        }

        return Result<byte[]>.Success(buffer);
    }

    /// <summary>
    /// Works out the cluster numbers a chain occupies, either by counting on from
    /// the first (contiguous) or by following the FAT.
    /// </summary>
    private Result<uint[]> ResolveClusters(uint firstCluster, bool contiguous, long wanted, string what)
    {
        if (wanted <= 0)
        {
            return Result<uint[]>.Success([]);
        }

        if (wanted > MaxClustersPerChain)
        {
            wanted = MaxClustersPerChain;
        }

        List<uint> clusters = new((int)Math.Min(wanted, 1024));
        uint cluster = firstCluster;

        for (long index = 0; index < wanted; index++)
        {
            if (!_geometry.IsDataCluster(cluster))
            {
                if (index == 0)
                {
                    return Result<uint[]>.Failure(DmgError.Corrupt(
                        $"This volume's {what} starts outside the cluster heap.",
                        string.Create(CultureInfo.InvariantCulture, $"Cluster {cluster}.")));
                }

                break;
            }

            clusters.Add(cluster);

            if (contiguous)
            {
                cluster++;
                continue;
            }

            Result<uint> next = _geometry.NextCluster(_volume, cluster);

            if (!next.TryGetValue(out uint following))
            {
                return next.CastFailure<uint[]>();
            }

            if (following is ExFatGeometry.EndOfChain or 0)
            {
                break;
            }

            if (!_geometry.IsDataCluster(following))
            {
                return Result<uint[]>.Failure(DmgError.Corrupt(
                    $"This volume's {what} chain leaves the cluster heap.",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cluster {cluster} is followed by {following}, and the heap holds {_geometry.ClusterCount} clusters.")));
            }

            cluster = following;
        }

        return Result<uint[]>.Success([.. clusters]);
    }

    /// <summary>
    /// Converts an exFAT packed timestamp to a date, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// The format is the DOS one: seconds in twos, then minute, hour, day, month,
    /// and year counted from 1980. A zero stamp means "not recorded", and a stamp
    /// with a month or day of zero is not a date at all - both come back as null
    /// rather than as an exception or a wrong date in 1979.
    /// </remarks>
    private static DateTimeOffset? ToTimestamp(uint packed)
    {
        if (packed == 0)
        {
            return null;
        }

        int second = (int)((packed & 0x1F) * 2);
        int minute = (int)((packed >> 5) & 0x3F);
        int hour = (int)((packed >> 11) & 0x1F);
        int day = (int)((packed >> 16) & 0x1F);
        int month = (int)((packed >> 21) & 0x0F);
        int year = (int)((packed >> 25) & 0x7F) + 1980;

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    }
}
