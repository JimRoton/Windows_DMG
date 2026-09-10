using System.Security.Cryptography;
using Dmg.Core.Codecs;
using Dmg.Core.Containers;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// What decoding a whole image produced: the hash of the sector stream and an
/// inventory of the chunk types that went into it.
/// </summary>
/// <param name="Sha256">
/// SHA-256 of the decoded sector stream, lower-case hex - directly comparable with
/// the manifest's <c>decoded_sha256</c>, which came from hdiutil. Covers the first
/// <see cref="HashedLength"/> bytes.
/// </param>
/// <param name="Length">Length of the whole decoded sector stream in bytes.</param>
/// <param name="HashedLength">
/// How many bytes went into <see cref="Sha256"/>. Equal to <see cref="Length"/>
/// unless the caller asked for a prefix.
/// </param>
/// <param name="TailIsAllZero">
/// True when every byte past <see cref="HashedLength"/> is zero. Trivially true
/// when the whole stream was hashed.
/// </param>
/// <param name="SectorsByEntryType">
/// How many sectors each chunk entry type contributed. The proof that a given
/// decoder was actually exercised, rather than the image happening to contain
/// nothing but zero fill.
/// </param>
internal sealed record DecodedImage(
    string Sha256,
    long Length,
    long HashedLength,
    bool TailIsAllZero,
    IReadOnlyDictionary<uint, long> SectorsByEntryType);

/// <summary>
/// Walks a UDIF image end to end with nothing but <c>Dmg.Core</c>: koly trailer,
/// property list, blkx regions, mish chunk tables, extent index, and every chunk
/// through <see cref="ChunkDecoderRegistry.Default"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the smallest thing that can put our decoders in front of a real Apple
/// image. It is deliberately a test helper and not a product type: the real reader
/// arrives with the mount path, streams sectors on demand, and has no business
/// materialising an entire disk. What it needs to inherit from here is only the
/// order of operations.
/// </para>
/// <para>
/// The data fork is read at <c>koly.DataForkOffset + chunk.CompressedOffset</c>. A
/// mish block may also declare its own <c>DataOffset</c> to be added on top; every
/// image hdiutil writes leaves it zero, and rather than implement a path no fixture
/// exercises, a non-zero value is reported as an explicit failure so that whoever
/// first meets one is told exactly what is unimplemented.
/// </para>
/// </remarks>
internal static class UdifImageDecoder
{
    /// <summary>
    /// The most compressed bytes one chunk may claim. The decoded side has its own
    /// ceiling inside the registry; this one only stops a corrupt
    /// <c>CompressedLength</c> from sizing a read buffer.
    /// </summary>
    private const long MaxCompressedChunkBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Decodes the whole image and hashes the result.
    /// </summary>
    /// <param name="path">The .dmg file.</param>
    /// <param name="hashLimit">
    /// Hash only the first this-many bytes, and report whether the rest is zeros.
    /// Negative means hash everything. This exists for one specific reason:
    /// <c>hdiutil convert -format UDTO</c> stops writing at the last non-zero
    /// sector, so its ground-truth hash for an image with trailing free space
    /// covers a prefix of the disk rather than the whole of it. Everything past the
    /// limit is still decoded and still checked - it just has to be zeros.
    /// </param>
    internal static Result<DecodedImage> Decode(string path, long hashLimit = -1)
    {
        ArgumentNullException.ThrowIfNull(path);

        using FileStream stream = File.OpenRead(path);

        Result<ExtentIndex> opened = Open(stream, out KolyTrailer? koly);

        return opened.TryGetValue(out ExtentIndex? index)
            ? DecodeExtents(stream, koly!, index, hashLimit)
            : opened.CastFailure<DecodedImage>();
    }

    /// <summary>
    /// The distinct chunk entry types an image uses, read from the chunk table
    /// alone - no data fork, no decoding. This is what <c>dmg info</c> will do, and
    /// it is how a test can tell "an image we must refuse" from "an image we must
    /// decode" before attempting either.
    /// </summary>
    internal static Result<IReadOnlyList<uint>> SurveyEntryTypes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using FileStream stream = File.OpenRead(path);

        Result<ExtentIndex> opened = Open(stream, out _);

        if (!opened.TryGetValue(out ExtentIndex? index))
        {
            return opened.CastFailure<IReadOnlyList<uint>>();
        }

        return Result<IReadOnlyList<uint>>.Success(
            [.. index.Extents.Select(extent => (uint)extent.EntryType).Distinct().Order()]);
    }

    private static Result<ExtentIndex> Open(FileStream stream, out KolyTrailer? koly)
    {
        koly = null;

        Result<KolyTrailer> trailer = KolyTrailer.Read(stream);

        if (!trailer.TryGetValue(out KolyTrailer? read))
        {
            return trailer.CastFailure<ExtentIndex>();
        }

        koly = read;

        Result<PlistValue> plist = PlistReader.Read(stream, koly.XmlOffset, koly.XmlLength);

        if (!plist.TryGetValue(out PlistValue? root))
        {
            return plist.CastFailure<ExtentIndex>();
        }

        Result<IReadOnlyList<BlkxEntry>> extracted = BlkxReader.Extract(root);

        if (!extracted.TryGetValue(out IReadOnlyList<BlkxEntry>? entries))
        {
            return extracted.CastFailure<ExtentIndex>();
        }

        List<MishBlock> regions = new(entries.Count);

        foreach (BlkxEntry entry in entries)
        {
            Result<MishBlock> parsed = MishBlock.Parse(entry.Data.Span, entry.DisplayName);

            if (!parsed.TryGetValue(out MishBlock? region))
            {
                return parsed.CastFailure<ExtentIndex>();
            }

            if (region.DataOffset != 0)
            {
                return Result<ExtentIndex>.Failure(DmgError.Internal(
                    $"The region {entry.DisplayName} declares a non-zero mish DataOffset, which this "
                    + "test decoder does not implement.",
                    $"mish.DataOffset = {region.DataOffset}."));
            }

            regions.Add(region);
        }

        return ExtentIndex.Build(regions, koly.SectorCount);
    }

    private static Result<DecodedImage> DecodeExtents(
        FileStream stream,
        KolyTrailer koly,
        ExtentIndex index,
        long hashLimit)
    {
        ChunkDecoderRegistry registry = ChunkDecoderRegistry.Default;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Dictionary<uint, long> sectorsByEntryType = [];
        byte[] destination = [];
        byte[] source = [];
        long total = 0;
        long hashed = 0;
        bool tailIsAllZero = true;

        foreach (Extent extent in index.Extents)
        {
            uint entryType = (uint)extent.EntryType;

            Result<int> capacity = ChunkDecoderRegistry.DecodedLength((long)extent.SectorCount);

            if (!capacity.TryGetValue(out int declaredLength))
            {
                return capacity.CastFailure<DecodedImage>();
            }

            if (destination.Length < declaredLength)
            {
                destination = new byte[declaredLength];
            }

            int sourceLength = 0;

            if (registry.TryGetDecoder(entryType, out IChunkDecoder? decoder)
                && decoder.ReadsDataFork
                && extent.CompressedLength > 0)
            {
                if (extent.CompressedLength > MaxCompressedChunkBytes)
                {
                    return Result<DecodedImage>.Failure(DmgError.Corrupt(
                        "A chunk declares more compressed bytes than this build will read at once.",
                        $"CompressedLength={extent.CompressedLength}, ceiling {MaxCompressedChunkBytes}."));
                }

                sourceLength = (int)extent.CompressedLength;

                if (source.Length < sourceLength)
                {
                    source = new byte[sourceLength];
                }

                Result read = ReadDataFork(stream, koly, extent, source.AsSpan(0, sourceLength));

                if (!read.Ok)
                {
                    return read.CastFailure<DecodedImage>();
                }
            }

            Result<int> decoded = registry.Decode(
                entryType,
                source.AsSpan(0, sourceLength),
                destination.AsSpan(0, declaredLength),
                (long)extent.SectorCount);

            if (!decoded.TryGetValue(out int written))
            {
                return decoded.CastFailure<DecodedImage>();
            }

            // Split this chunk's output at the hash limit: what is below it goes
            // into the digest, what is above it only has to be zeros.
            int toHash = hashLimit < 0
                ? written
                : (int)Math.Clamp(hashLimit - total, 0, written);

            if (toHash > 0)
            {
                hash.AppendData(destination, 0, toHash);
                hashed += toHash;
            }

            if (toHash < written
                && destination.AsSpan(toHash, written - toHash).ContainsAnyExcept((byte)0))
            {
                tailIsAllZero = false;
            }

            total += written;

            sectorsByEntryType[entryType] =
                sectorsByEntryType.GetValueOrDefault(entryType) + (long)extent.SectorCount;
        }

        return Result<DecodedImage>.Success(new DecodedImage(
            Convert.ToHexStringLower(hash.GetHashAndReset()),
            total,
            hashed,
            tailIsAllZero,
            sectorsByEntryType));
    }

    private static Result ReadDataFork(
        FileStream stream,
        KolyTrailer koly,
        Extent extent,
        Span<byte> buffer)
    {
        ulong offset = koly.DataForkOffset + extent.CompressedOffset;

        try
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(DmgError.Corrupt(
                "A chunk's compressed bytes run past the end of the image.",
                $"Wanted {buffer.Length} bytes at offset {offset}: {exception.Message}"));
        }
    }
}
