namespace Dmg.Core.Containers;

/// <summary>
/// The <c>mish</c> block that a <c>blkx</c> entry's <c>Data</c> payload decodes
/// to: one region of the decoded disk, and the table of chunks that rebuilds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout, verified against an hdiutil image rather than taken from the
/// documentation.</b> The header is 0xCC bytes:
/// </para>
/// <code>
/// 0x00  4   'mish'
/// 0x04  4   Version (1)
/// 0x08  8   FirstSectorNumber
/// 0x10  8   SectorCount
/// 0x18  8   DataOffset
/// 0x20  4   BuffersNeeded
/// 0x24  4   BlockDescriptors
/// 0x28  24  Reserved
/// 0x40  136 Checksum: 4-byte type, 4-byte size in bits, 128 bytes of value
/// 0xC8  4   NumberOfBlockChunks   <-- the count lives here
/// 0xCC  40*N Chunk descriptors    <-- the table starts here
/// </code>
/// <para>
/// docs/03 previously put <c>NumberOfBlockChunks</c> at 0xCC while also saying the
/// table began at 0xCC, which cannot both be true. The count is at 0xC8. The
/// arithmetic settles it: the two mish blocks in a real UDZO image are 284 and 364
/// bytes, which is exactly 0xCC + 40x2 and 0xCC + 40x4, and the words at 0xC8 are
/// 2 and 4. Reading the count from 0xCC would instead read the first chunk's entry
/// type, 0x80000005.
/// </para>
/// </remarks>
/// <param name="Version">Block version. 1 is the only one written.</param>
/// <param name="FirstSectorNumber">Where this region starts on the decoded disk.</param>
/// <param name="SectorCount">How many sectors this region covers.</param>
/// <param name="DataOffset">Offset added to each chunk's compressed offset; 0 in practice.</param>
/// <param name="BuffersNeeded">Decompression buffer hint, passed through uninterpreted.</param>
/// <param name="BlockDescriptors">Descriptor index, passed through uninterpreted.</param>
/// <param name="ChecksumType">The region checksum algorithm, passed through uninterpreted.</param>
/// <param name="ChunkCount">How many 40-byte descriptors follow the header.</param>
public sealed record MishBlock(
    uint Version,
    ulong FirstSectorNumber,
    ulong SectorCount,
    ulong DataOffset,
    uint BuffersNeeded,
    uint BlockDescriptors,
    uint ChecksumType,
    uint ChunkCount)
{
    /// <summary>The block signature, <c>mish</c>.</summary>
    public const uint Magic = 0x6D697368;

    /// <summary>The only block version this build understands.</summary>
    public const uint SupportedVersion = 1;

    /// <summary>
    /// Size of the header, and therefore the offset of the first chunk descriptor:
    /// 204 bytes. Verified against an hdiutil-produced image.
    /// </summary>
    public const int ChunkTableOffset = 0xCC;

    /// <summary>Size of one chunk descriptor.</summary>
    public const int ChunkDescriptorSize = 40;

    private const int SignatureOffset = 0x00;
    private const int VersionOffset = 0x04;
    private const int FirstSectorNumberOffset = 0x08;
    private const int SectorCountOffset = 0x10;
    private const int DataOffsetOffset = 0x18;
    private const int BuffersNeededOffset = 0x20;
    private const int BlockDescriptorsOffset = 0x24;
    private const int ChecksumTypeOffset = 0x40;
    private const int ChunkCountOffset = 0xC8;

    /// <summary>The sector one past the end of this region, in decoded-disk terms.</summary>
    public Result<ulong> EndSectorExclusive() =>
        BigEndian.Add(FirstSectorNumber, SectorCount, "region extent");

    /// <summary>The size of this region in bytes.</summary>
    public Result<ulong> LengthInBytes() =>
        BigEndian.SectorsToBytes(SectorCount, "mish.SectorCount");

    /// <summary>
    /// Parses and validates a mish block header, including that its declared chunk
    /// table fits inside the payload it arrived in.
    /// </summary>
    /// <param name="block">The decoded <c>Data</c> payload of a blkx entry.</param>
    /// <param name="region">
    /// What to call this region in error messages - the blkx entry's display name.
    /// </param>
    public static Result<MishBlock> Parse(ReadOnlySpan<byte> block, string region)
    {
        ArgumentNullException.ThrowIfNull(region);

        if (block.Length < ChunkTableOffset)
        {
            return Result<MishBlock>.Failure(
                DmgExitCode.CorruptImage,
                $"The block map for {region} is truncated.",
                $"{block.Length} bytes; a mish header alone is {ChunkTableOffset}.");
        }

        if (!BigEndian.TryReadUInt32(block, SignatureOffset, out uint signature) || signature != Magic)
        {
            return Result<MishBlock>.Failure(
                DmgExitCode.CorruptImage,
                $"The block map for {region} is not a mish block.",
                $"Expected 'mish', found '{BigEndian.DescribeFourCharCode(signature)}'.");
        }

        if (!BigEndian.TryReadUInt32(block, VersionOffset, out uint version)
            || !BigEndian.TryReadUInt64(block, FirstSectorNumberOffset, out ulong firstSector)
            || !BigEndian.TryReadUInt64(block, SectorCountOffset, out ulong sectorCount)
            || !BigEndian.TryReadUInt64(block, DataOffsetOffset, out ulong dataOffset)
            || !BigEndian.TryReadUInt32(block, BuffersNeededOffset, out uint buffersNeeded)
            || !BigEndian.TryReadUInt32(block, BlockDescriptorsOffset, out uint blockDescriptors)
            || !BigEndian.TryReadUInt32(block, ChecksumTypeOffset, out uint checksumType)
            || !BigEndian.TryReadUInt32(block, ChunkCountOffset, out uint chunkCount))
        {
            return Result<MishBlock>.Failure(
                DmgExitCode.CorruptImage,
                $"The block map for {region} is truncated.",
                $"A field ran past the end of the {block.Length}-byte block.");
        }

        if (version != SupportedVersion)
        {
            return Result<MishBlock>.Failure(
                DmgExitCode.UnsupportedFormat,
                $"The block map for {region} is version {version}; this build reads version {SupportedVersion}.",
                "mish.Version");
        }

        Result<ulong> lengthInBytes = BigEndian.SectorsToBytes(sectorCount, "mish.SectorCount");

        if (!lengthInBytes.Ok)
        {
            return lengthInBytes.CastFailure<MishBlock>();
        }

        Result<ulong> endSector = BigEndian.Add(firstSector, sectorCount, "region extent");

        if (!endSector.Ok)
        {
            return endSector.CastFailure<MishBlock>();
        }

        // Bound the chunk table before anyone allocates for it. long arithmetic
        // cannot overflow here: chunkCount is a uint and the multiplier is 40.
        long tableBytes = (long)chunkCount * ChunkDescriptorSize;

        if (tableBytes > block.Length - ChunkTableOffset)
        {
            return Result<MishBlock>.Failure(
                DmgExitCode.CorruptImage,
                $"The block map for {region} declares more chunks than it contains.",
                $"{chunkCount} chunks need {tableBytes} bytes at offset {ChunkTableOffset}, " +
                $"but the block is {block.Length} bytes.");
        }

        return Result<MishBlock>.Success(new MishBlock(
            version,
            firstSector,
            sectorCount,
            dataOffset,
            buffersNeeded,
            blockDescriptors,
            checksumType,
            chunkCount));
    }
}
