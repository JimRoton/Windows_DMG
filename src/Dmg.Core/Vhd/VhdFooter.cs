using System.Buffers.Binary;

namespace Dmg.Core.Vhd;

/// <summary>
/// The 512-byte, big-endian footer that turns a raw disk image into a fixed VHD
/// that Windows will attach.
/// </summary>
/// <remarks>
/// <para>
/// A fixed VHD is the simplest container in this project: the decoded payload
/// verbatim, then this footer appended at the end of the file. There is no block
/// allocation table, no bitmap, and nothing that has to be kept in sync while the
/// payload streams out - which is precisely why it was chosen over VHDX. The whole
/// contract with Windows lives in these 512 bytes.
/// </para>
/// <para>
/// The layout is the one in the "Virtual Hard Disk Image Format Specification"
/// (Microsoft, October 2006). Every multi-byte field is <b>big-endian</b>, which is
/// the single most common way of getting this wrong on a little-endian machine, so
/// every read and write here goes through <see cref="BinaryPrimitives"/> with an
/// explicit endianness rather than through a struct overlay.
/// </para>
/// <para>
/// This type is deliberately portable: it lives in <c>Dmg.Core</c>, has no Windows
/// dependency, and is verified on macOS against the constants in the specification.
/// A footer is a pure function of a disk size, an identifier and a timestamp.
/// </para>
/// </remarks>
public sealed class VhdFooter
{
    /// <summary>The footer's size on disk, in bytes.</summary>
    public const int Length = 512;

    /// <summary>The sector size the VHD format assumes, in bytes.</summary>
    public const int SectorSize = 512;

    /// <summary>
    /// The largest disk a VHD can describe: 2040 GB. Beyond roughly 127 GB the CHS
    /// geometry saturates and the size fields carry the capacity on their own.
    /// </summary>
    public const long MaxDiskSize = 2040L * 1024L * 1024L * 1024L;

    /// <summary>The eight ASCII bytes every VHD footer starts with.</summary>
    public const string Cookie = "conectix";

    /// <summary>The <c>Creator Application</c> four-character code this tool stamps.</summary>
    public const string DefaultCreatorApplication = "dmg ";

    /// <summary>
    /// The <c>Creator Host OS</c> four-character code for Windows, <c>Wi2k</c>. The
    /// footer describes the machine the VHD is written for, and these VHDs exist to
    /// be attached by the Windows VHD provider.
    /// </summary>
    public const string WindowsHostOs = "Wi2k";

    /// <summary>The <c>Creator Host OS</c> four-character code for Macintosh, <c>Mac </c>.</summary>
    public const string MacintoshHostOs = "Mac ";

    // Reserved bit 1 is defined by the specification as "must be set"; no other
    // feature bit applies to a fixed disk.
    private const uint ReservedFeature = 0x0000_0002u;

    // Version 1.0 of the file format, packed as major:minor in two 16-bit halves.
    private const uint FileFormatVersion1 = 0x0001_0000u;

    // A fixed disk has no dynamic header, so Data Offset is all ones.
    private const ulong NoDataOffset = 0xFFFF_FFFF_FFFF_FFFFuL;

    private const int CookieOffset = 0;
    private const int FeaturesOffset = 8;
    private const int FileFormatVersionOffset = 12;
    private const int DataOffsetOffset = 16;
    private const int TimeStampOffset = 24;
    private const int CreatorApplicationOffset = 28;
    private const int CreatorVersionOffset = 32;
    private const int CreatorHostOsOffset = 36;
    private const int OriginalSizeOffset = 40;
    private const int CurrentSizeOffset = 48;
    private const int DiskGeometryOffset = 56;
    private const int DiskTypeOffset = 60;
    private const int ChecksumOffset = 64;
    private const int UniqueIdOffset = 68;
    private const int SavedStateOffset = 84;

    private const int FourCharCodeLength = 4;

    /// <summary>
    /// The VHD epoch: midnight UTC, 1 January 2000. The <c>Time Stamp</c> field
    /// counts seconds from here, not from the Unix epoch.
    /// </summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private VhdFooter(
        long diskSize,
        VhdGeometry geometry,
        VhdDiskType diskType,
        ulong dataOffset,
        uint features,
        uint fileFormatVersion,
        DateTimeOffset createdUtc,
        string creatorApplication,
        uint creatorVersion,
        string creatorHostOs,
        Guid uniqueId,
        bool savedState,
        uint checksum)
    {
        DiskSize = diskSize;
        Geometry = geometry;
        DiskType = diskType;
        DataOffset = dataOffset;
        Features = features;
        FileFormatVersion = fileFormatVersion;
        CreatedUtc = createdUtc;
        CreatorApplication = creatorApplication;
        CreatorVersion = creatorVersion;
        CreatorHostOs = creatorHostOs;
        UniqueId = uniqueId;
        SavedState = savedState;
        Checksum = checksum;
    }

    /// <summary>The disk's capacity in bytes, written to both <c>Original Size</c> and <c>Current Size</c>.</summary>
    public long DiskSize { get; }

    /// <summary>The CHS geometry at offset 56.</summary>
    public VhdGeometry Geometry { get; }

    /// <summary>The disk type at offset 60. Always <see cref="VhdDiskType.Fixed"/> for footers this tool writes.</summary>
    public VhdDiskType DiskType { get; }

    /// <summary>The <c>Data Offset</c> field: all ones for a fixed disk.</summary>
    public ulong DataOffset { get; }

    /// <summary>The <c>Features</c> bitfield, with the specification's reserved bit set.</summary>
    public uint Features { get; }

    /// <summary>The <c>File Format Version</c>, <c>0x00010000</c> for version 1.0.</summary>
    public uint FileFormatVersion { get; }

    /// <summary>Creation time, to one-second resolution, relative to <see cref="Epoch"/>.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>The four-character <c>Creator Application</c> code.</summary>
    public string CreatorApplication { get; }

    /// <summary>The creator's version, packed as major:minor in two 16-bit halves.</summary>
    public uint CreatorVersion { get; }

    /// <summary>The four-character <c>Creator Host OS</c> code.</summary>
    public string CreatorHostOs { get; }

    /// <summary>The disk's unique identifier, written in RFC 4122 big-endian byte order.</summary>
    public Guid UniqueId { get; }

    /// <summary>Whether the disk is in a saved state. Always false for a VHD this tool writes.</summary>
    public bool SavedState { get; }

    /// <summary>The one's-complement checksum stored at offset 64.</summary>
    public uint Checksum { get; }

    /// <summary>The disk's capacity expressed in 512-byte sectors.</summary>
    public long TotalSectors => DiskSize / SectorSize;

    /// <summary>
    /// Builds the footer for a fixed VHD of <paramref name="diskSizeInBytes"/> bytes.
    /// </summary>
    /// <param name="diskSizeInBytes">
    /// The payload size. Must be a positive whole number of 512-byte sectors and no
    /// larger than <see cref="MaxDiskSize"/>.
    /// </param>
    /// <param name="uniqueId">
    /// The disk's identifier. It matters only for differencing chains, but Windows
    /// stores it, so it should be stable for a given VHD and distinct between VHDs.
    /// </param>
    /// <param name="createdUtc">
    /// The creation timestamp. Truncated to whole seconds and must not precede
    /// <see cref="Epoch"/>.
    /// </param>
    /// <param name="creatorApplication">A four-character creator code.</param>
    /// <param name="creatorHostOs">A four-character host-OS code.</param>
    /// <returns>The footer, or a failure describing why the request cannot be honoured.</returns>
    public static Result<VhdFooter> ForFixedDisk(
        long diskSizeInBytes,
        Guid uniqueId,
        DateTimeOffset createdUtc,
        string creatorApplication = DefaultCreatorApplication,
        string creatorHostOs = WindowsHostOs)
    {
        if (diskSizeInBytes <= 0)
        {
            return Result<VhdFooter>.Failure(
                DmgExitCode.UnsupportedFormat,
                "A VHD must hold at least one 512-byte sector.",
                $"requested disk size {diskSizeInBytes} bytes");
        }

        if (diskSizeInBytes % SectorSize != 0)
        {
            return Result<VhdFooter>.Failure(
                DmgExitCode.UnsupportedFormat,
                "A VHD's size must be a whole number of 512-byte sectors.",
                $"requested disk size {diskSizeInBytes} bytes leaves {diskSizeInBytes % SectorSize} over");
        }

        if (diskSizeInBytes > MaxDiskSize)
        {
            return Result<VhdFooter>.Failure(
                DmgExitCode.UnsupportedFormat,
                "The image is larger than the 2040 GB a VHD can describe.",
                $"requested disk size {diskSizeInBytes} bytes, maximum {MaxDiskSize}");
        }

        DateTimeOffset created = createdUtc.ToUniversalTime();

        if (created < Epoch)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Internal(
                    "A VHD footer cannot record a creation time before the year 2000.",
                    $"timestamp {created:O}"));
        }

        long secondsSinceEpoch = (long)(created - Epoch).TotalSeconds;

        if (secondsSinceEpoch > uint.MaxValue)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Internal(
                    "A VHD footer cannot record a creation time that far in the future.",
                    $"timestamp {created:O}"));
        }

        if (!IsFourCharCode(creatorApplication))
        {
            return Result<VhdFooter>.Failure(
                DmgError.Internal(
                    "The VHD creator application code must be four printable ASCII characters.",
                    $"got '{creatorApplication}'"));
        }

        if (!IsFourCharCode(creatorHostOs))
        {
            return Result<VhdFooter>.Failure(
                DmgError.Internal(
                    "The VHD creator host OS code must be four printable ASCII characters.",
                    $"got '{creatorHostOs}'"));
        }

        VhdFooter footer = new(
            diskSizeInBytes,
            VhdGeometry.ForDiskSize(diskSizeInBytes),
            VhdDiskType.Fixed,
            NoDataOffset,
            ReservedFeature,
            FileFormatVersion1,
            Epoch.AddSeconds(secondsSinceEpoch),
            creatorApplication,
            FileFormatVersion1,
            creatorHostOs,
            uniqueId,
            savedState: false,
            checksum: 0);

        Span<byte> bytes = stackalloc byte[Length];
        footer.WriteFields(bytes);

        return Result<VhdFooter>.Success(footer.WithChecksum(ComputeChecksum(bytes)));
    }

    /// <summary>
    /// Builds the footer for a fixed VHD, generating a fresh identifier and stamping
    /// the current time.
    /// </summary>
    public static Result<VhdFooter> ForFixedDisk(long diskSizeInBytes) =>
        ForFixedDisk(diskSizeInBytes, Guid.NewGuid(), DateTimeOffset.UtcNow);

    /// <summary>
    /// Serialises the footer into <paramref name="destination"/>, which must be
    /// exactly <see cref="Length"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A VHD footer is exactly {Length} bytes; the destination is {destination.Length}.",
                nameof(destination));
        }

        WriteFields(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[ChecksumOffset..], Checksum);
    }

    /// <summary>Serialises the footer into a fresh 512-byte array.</summary>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[Length];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>
    /// The one's-complement checksum of a serialised footer: every byte summed with
    /// the four checksum bytes treated as zero, then bitwise inverted.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="footer"/> is not <see cref="Length"/> bytes.</exception>
    public static uint ComputeChecksum(ReadOnlySpan<byte> footer)
    {
        if (footer.Length != Length)
        {
            throw new ArgumentException(
                $"A VHD footer is exactly {Length} bytes; got {footer.Length}.",
                nameof(footer));
        }

        uint sum = 0;

        for (int index = 0; index < Length; index++)
        {
            if (index >= ChecksumOffset && index < ChecksumOffset + sizeof(uint))
            {
                continue;
            }

            sum += footer[index];
        }

        return ~sum;
    }

    /// <summary>
    /// Reads a footer back off disk, rejecting anything that is not a well-formed
    /// VHD footer.
    /// </summary>
    /// <remarks>
    /// Used to verify what this tool wrote and to explain what someone else wrote.
    /// It does not insist on a fixed disk - the caller decides whether the disk type
    /// it found is one it can work with.
    /// </remarks>
    public static Result<VhdFooter> Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length != Length)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Corrupt(
                    "That is not a VHD footer: the wrong number of bytes.",
                    $"expected {Length}, got {source.Length}"));
        }

        for (int index = 0; index < Cookie.Length; index++)
        {
            if (source[CookieOffset + index] != (byte)Cookie[index])
            {
                return Result<VhdFooter>.Failure(
                    DmgError.Corrupt(
                        "That is not a VHD footer: the 'conectix' cookie is missing.",
                        $"found '{ReadAscii(source[CookieOffset..(CookieOffset + Cookie.Length)])}'"));
            }
        }

        uint storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(source[ChecksumOffset..]);
        uint expectedChecksum = ComputeChecksum(source);

        if (storedChecksum != expectedChecksum)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Corrupt(
                    "The VHD footer's checksum does not match its contents.",
                    $"stored 0x{storedChecksum:X8}, computed 0x{expectedChecksum:X8}"));
        }

        uint fileFormatVersion = BinaryPrimitives.ReadUInt32BigEndian(source[FileFormatVersionOffset..]);

        if (fileFormatVersion >> 16 != 1)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Unsupported(
                    "This VHD uses a file format version this build does not understand.",
                    $"version 0x{fileFormatVersion:X8}"));
        }

        uint rawDiskType = BinaryPrimitives.ReadUInt32BigEndian(source[DiskTypeOffset..]);

        if (rawDiskType is not ((uint)VhdDiskType.Fixed or (uint)VhdDiskType.Dynamic or (uint)VhdDiskType.Differencing))
        {
            return Result<VhdFooter>.Failure(
                DmgError.Corrupt(
                    "The VHD footer names a disk type that no implementation writes.",
                    $"disk type {rawDiskType}"));
        }

        VhdDiskType diskType = (VhdDiskType)rawDiskType;
        ulong dataOffset = BinaryPrimitives.ReadUInt64BigEndian(source[DataOffsetOffset..]);

        if (diskType == VhdDiskType.Fixed && dataOffset != NoDataOffset)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Corrupt(
                    "A fixed VHD must have no dynamic-header offset.",
                    $"data offset 0x{dataOffset:X16}"));
        }

        long currentSize = BinaryPrimitives.ReadInt64BigEndian(source[CurrentSizeOffset..]);

        if (currentSize <= 0 || currentSize % SectorSize != 0)
        {
            return Result<VhdFooter>.Failure(
                DmgError.Corrupt(
                    "The VHD footer's disk size is not a whole number of 512-byte sectors.",
                    $"current size {currentSize} bytes"));
        }

        uint timeStamp = BinaryPrimitives.ReadUInt32BigEndian(source[TimeStampOffset..]);

        return Result<VhdFooter>.Success(new VhdFooter(
            currentSize,
            VhdGeometry.ReadFrom(source[DiskGeometryOffset..]),
            diskType,
            dataOffset,
            BinaryPrimitives.ReadUInt32BigEndian(source[FeaturesOffset..]),
            fileFormatVersion,
            Epoch.AddSeconds(timeStamp),
            ReadAscii(source[CreatorApplicationOffset..(CreatorApplicationOffset + FourCharCodeLength)]),
            BinaryPrimitives.ReadUInt32BigEndian(source[CreatorVersionOffset..]),
            ReadAscii(source[CreatorHostOsOffset..(CreatorHostOsOffset + FourCharCodeLength)]),
            new Guid(source[UniqueIdOffset..(UniqueIdOffset + 16)], bigEndian: true),
            source[SavedStateOffset] != 0,
            storedChecksum));
    }

    /// <summary>A one-line summary for verbose output and diagnostics.</summary>
    public override string ToString() =>
        $"VHD {DiskType} {DiskSize} bytes, geometry {Geometry}, id {UniqueId:D}, checksum 0x{Checksum:X8}";

    private VhdFooter WithChecksum(uint checksum) => new(
        DiskSize,
        Geometry,
        DiskType,
        DataOffset,
        Features,
        FileFormatVersion,
        CreatedUtc,
        CreatorApplication,
        CreatorVersion,
        CreatorHostOs,
        UniqueId,
        SavedState,
        checksum);

    /// <summary>
    /// Writes every field except the checksum, leaving those four bytes zero. Both
    /// the checksum calculation and the final serialisation start here, so they can
    /// never disagree about what was summed.
    /// </summary>
    private void WriteFields(Span<byte> destination)
    {
        destination.Clear();

        for (int index = 0; index < Cookie.Length; index++)
        {
            destination[CookieOffset + index] = (byte)Cookie[index];
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination[FeaturesOffset..], Features);
        BinaryPrimitives.WriteUInt32BigEndian(destination[FileFormatVersionOffset..], FileFormatVersion);
        BinaryPrimitives.WriteUInt64BigEndian(destination[DataOffsetOffset..], DataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[TimeStampOffset..],
            (uint)(CreatedUtc - Epoch).TotalSeconds);

        WriteAscii(destination[CreatorApplicationOffset..], CreatorApplication);
        BinaryPrimitives.WriteUInt32BigEndian(destination[CreatorVersionOffset..], CreatorVersion);
        WriteAscii(destination[CreatorHostOsOffset..], CreatorHostOs);

        BinaryPrimitives.WriteInt64BigEndian(destination[OriginalSizeOffset..], DiskSize);
        BinaryPrimitives.WriteInt64BigEndian(destination[CurrentSizeOffset..], DiskSize);

        Geometry.WriteTo(destination[DiskGeometryOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[DiskTypeOffset..], (uint)DiskType);

        // Offsets 64..68 stay zero: the checksum is stamped by the caller once the
        // rest of the footer is in place.

        if (!UniqueId.TryWriteBytes(destination[UniqueIdOffset..], bigEndian: true, out _))
        {
            throw new InvalidOperationException("A 16-byte GUID did not fit the VHD footer's Unique Id field.");
        }

        destination[SavedStateOffset] = SavedState ? (byte)1 : (byte)0;
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        for (int index = 0; index < FourCharCodeLength; index++)
        {
            destination[index] = index < value.Length ? (byte)value[index] : (byte)' ';
        }
    }

    private static string ReadAscii(ReadOnlySpan<byte> source)
    {
        Span<char> characters = stackalloc char[source.Length];

        for (int index = 0; index < source.Length; index++)
        {
            characters[index] = (char)source[index];
        }

        return new string(characters);
    }

    private static bool IsFourCharCode(string value)
    {
        if (value is not { Length: FourCharCodeLength })
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
