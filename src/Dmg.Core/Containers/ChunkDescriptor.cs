using System.Globalization;

namespace Dmg.Core.Containers;

/// <summary>
/// How a chunk's bytes are stored, and therefore which codec rebuilds them.
/// </summary>
/// <remarks>
/// An entry type this build cannot decode is not a parse error. <c>dmg info</c>
/// must be able to report that an image is bzip2-compressed without failing to
/// read it; the refusal belongs at decode time, and only if the mount actually
/// touches such a chunk.
/// </remarks>
public enum ChunkEntryType : uint
{
    /// <summary>Emit zeros; nothing is stored.</summary>
    ZeroFill = 0x00000000,

    /// <summary>Stored uncompressed; copy straight through.</summary>
    Raw = 0x00000001,

    /// <summary>Free or ignored space; emit zeros.</summary>
    Ignore = 0x00000002,

    /// <summary>Apple ADC.</summary>
    AppleAdc = 0x80000004,

    /// <summary>zlib deflate.</summary>
    Zlib = 0x80000005,

    /// <summary>bzip2. Recognised, not decoded.</summary>
    Bzip2 = 0x80000006,

    /// <summary>LZFSE. Recognised, not decoded.</summary>
    Lzfse = 0x80000007,

    /// <summary>LZMA. Recognised, not decoded.</summary>
    Lzma = 0x80000008,

    /// <summary>A comment entry: carries no sectors and is skipped.</summary>
    Comment = 0x7FFFFFFE,

    /// <summary>The end of the chunk table.</summary>
    Terminator = 0xFFFFFFFF,
}

/// <summary>
/// One 40-byte entry of a mish block's chunk table: a run of sectors and where
/// the bytes for them live in the data fork.
/// </summary>
/// <param name="EntryType">The codec, or one of the two control entries.</param>
/// <param name="Comment">
/// The second word. Zero on real chunks; <c>+beg</c>/<c>+end</c> on comment
/// entries. hdiutil also writes junk here on ordinary chunks, so nothing may be
/// inferred from it.
/// </param>
/// <param name="SectorNumber">
/// Start sector <b>relative to the mish block's FirstSectorNumber</b>, not to the
/// disk. See <see cref="AbsoluteStartSector"/>.
/// </param>
/// <param name="SectorCount">Length of the run, in 512-byte sectors, once decoded.</param>
/// <param name="CompressedOffset">Byte offset into the data fork.</param>
/// <param name="CompressedLength">Bytes to read and hand to the codec.</param>
public readonly record struct ChunkDescriptor(
    ChunkEntryType EntryType,
    uint Comment,
    ulong SectorNumber,
    ulong SectorCount,
    ulong CompressedOffset,
    ulong CompressedLength)
{
    /// <summary>The size of one descriptor.</summary>
    public const int Size = 40;

    private const int EntryTypeOffset = 0x00;
    private const int CommentOffset = 0x04;
    private const int SectorNumberOffset = 0x08;
    private const int SectorCountOffset = 0x10;
    private const int CompressedOffsetOffset = 0x18;
    private const int CompressedLengthOffset = 0x20;

    /// <summary>True for the entry that ends the table.</summary>
    public bool IsTerminator => EntryType == ChunkEntryType.Terminator;

    /// <summary>True for a comment entry, which describes no sectors.</summary>
    public bool IsComment => EntryType == ChunkEntryType.Comment;

    /// <summary>True when this chunk's bytes come from the data fork rather than from nowhere.</summary>
    public bool IsStored => EntryType is ChunkEntryType.Raw
        or ChunkEntryType.AppleAdc
        or ChunkEntryType.Zlib
        or ChunkEntryType.Bzip2
        or ChunkEntryType.Lzfse
        or ChunkEntryType.Lzma;

    /// <summary>True when this build has a decoder for this entry type.</summary>
    public bool IsDecodable => EntryType is ChunkEntryType.ZeroFill
        or ChunkEntryType.Raw
        or ChunkEntryType.Ignore
        or ChunkEntryType.AppleAdc
        or ChunkEntryType.Zlib;

    /// <summary>
    /// This chunk's start sector on the decoded disk.
    /// </summary>
    /// <remarks>
    /// <c>SectorNumber</c> is relative to the mish block that contains the chunk.
    /// Treating it as an absolute sector puts every region except the first one at
    /// the wrong place on the disk - and the first region usually starts at sector
    /// 0, so the mistake looks harmless right up until the partition table does not
    /// parse. There is a test named for this.
    /// </remarks>
    /// <param name="firstSectorNumber">The owning mish block's FirstSectorNumber.</param>
    public Result<ulong> AbsoluteStartSector(ulong firstSectorNumber) =>
        BigEndian.Add(firstSectorNumber, SectorNumber, "chunk start sector");

    /// <summary>The byte just past this chunk's compressed bytes in the data fork.</summary>
    public Result<ulong> CompressedEndOffset() =>
        BigEndian.Add(CompressedOffset, CompressedLength, "chunk compressed extent");

    /// <summary>Parses one descriptor at <paramref name="offset"/>.</summary>
    public static Result<ChunkDescriptor> Parse(ReadOnlySpan<byte> block, int offset)
    {
        if (!BigEndian.TryReadUInt32(block, offset + EntryTypeOffset, out uint entryType)
            || !BigEndian.TryReadUInt32(block, offset + CommentOffset, out uint comment)
            || !BigEndian.TryReadUInt64(block, offset + SectorNumberOffset, out ulong sectorNumber)
            || !BigEndian.TryReadUInt64(block, offset + SectorCountOffset, out ulong sectorCount)
            || !BigEndian.TryReadUInt64(block, offset + CompressedOffsetOffset, out ulong compressedOffset)
            || !BigEndian.TryReadUInt64(block, offset + CompressedLengthOffset, out ulong compressedLength))
        {
            return Result<ChunkDescriptor>.Failure(
                DmgExitCode.CorruptImage,
                "A chunk descriptor runs past the end of its block map.",
                $"Wanted {Size} bytes at offset {offset} of a {block.Length}-byte block.");
        }

        return Result<ChunkDescriptor>.Success(new ChunkDescriptor(
            (ChunkEntryType)entryType,
            comment,
            sectorNumber,
            sectorCount,
            compressedOffset,
            compressedLength));
    }

    /// <summary>
    /// The entry type as text, naming the known ones and falling back to hex - so
    /// an unsupported codec can be reported rather than merely refused.
    /// </summary>
    public string DescribeEntryType() => Enum.IsDefined(EntryType)
        ? EntryType.ToString()
        : "0x" + ((uint)EntryType).ToString("X8", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() =>
        $"{DescribeEntryType()} +{SectorNumber}x{SectorCount} @{CompressedOffset}:{CompressedLength}";
}
