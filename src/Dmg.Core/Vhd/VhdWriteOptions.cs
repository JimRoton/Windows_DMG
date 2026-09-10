namespace Dmg.Core.Vhd;

/// <summary>
/// The knobs on <see cref="VhdWriter"/>: how much is copied at a time, and what
/// goes in the footer's identifying fields.
/// </summary>
/// <remarks>
/// <para>
/// Every property has a default that is right for the product, so a caller that
/// does not care writes <c>VhdWriteOptions.Default</c> - or passes null - and gets
/// a 1 MiB copy buffer, a fresh identifier and the current time.
/// </para>
/// <para>
/// <b>The identifier and the timestamp are inputs, not side effects.</b> A writer
/// that called <see cref="Guid.NewGuid"/> internally could not be tested for a
/// byte-exact output, and could not write the same VHD twice. Both are therefore
/// settable, and the round-trip suite pins them.
/// </para>
/// </remarks>
public sealed record VhdWriteOptions
{
    /// <summary>
    /// The copy buffer's size: 1 MiB, which is a UDIF chunk, not a 4 KiB page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This number is the difference between a fast conversion and a slow one. The
    /// source is a <c>DmgBlockStream</c>, which decodes a whole chunk at a time and
    /// keeps exactly one decoded chunk alive. Copying in 4 KiB slices asks that
    /// stream for the same chunk two hundred and fifty-six times and does
    /// two hundred and fifty-six times the bookkeeping, for no benefit; copying a
    /// chunk at a time consumes each decode in one call.
    /// </para>
    /// <para>
    /// hdiutil's own chunk is 1 MiB (2048 sectors) for the compressed formats, so
    /// this is the size that lines up with real images. It is not required to line
    /// up - a mismatch costs a little extra copying, never correctness.
    /// </para>
    /// </remarks>
    public const int DefaultBufferSize = 1024 * 1024;

    /// <summary>The smallest buffer that may be requested: one sector.</summary>
    public const int MinimumBufferSize = VhdFooter.SectorSize;

    /// <summary>
    /// The largest buffer that may be requested: 64 MiB, matching the ceiling the
    /// block stream holds a single compressed chunk to.
    /// </summary>
    public const int MaximumBufferSize = 64 * 1024 * 1024;

    /// <summary>The settings used when a caller expresses no preference.</summary>
    public static VhdWriteOptions Default { get; } = new();

    /// <summary>
    /// How many bytes are copied per read/write pair. Must be a whole number of
    /// 512-byte sectors between <see cref="MinimumBufferSize"/> and
    /// <see cref="MaximumBufferSize"/>.
    /// </summary>
    public int BufferSize { get; init; } = DefaultBufferSize;

    /// <summary>
    /// Which kind of VHD to write. <see cref="VhdDiskType.Fixed"/> by default, and
    /// that is the only value v1 ships with on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fixed is the default because fixed is the boring one.</b> The payload is
    /// laid down verbatim with a footer on the end: there is no allocation table to
    /// get wrong, nothing for the Windows VHD provider to misinterpret, and a
    /// half-written file is obviously a half-written file. That is worth a great
    /// deal more in a tool that mounts untrusted images than the scratch space a
    /// dynamic disk saves.
    /// </para>
    /// <para>
    /// <see cref="VhdDiskType.Dynamic"/> is behind this flag for images where the
    /// saving is the point - a 48 GB volume with 400 MB in it costs 400 MB of
    /// scratch instead of 48 GB. It is opt-in until it has been exercised against
    /// a real Windows VHD provider on real images.
    /// </para>
    /// </remarks>
    public VhdDiskType DiskType { get; init; } = VhdDiskType.Fixed;

    /// <summary>
    /// The dynamic disk's block size. Ignored for a fixed disk. 2 MiB by default,
    /// which is what every VHD in the wild uses.
    /// </summary>
    /// <remarks>
    /// Settable so the tests can reach the interesting cases - a partly allocated
    /// table, a short final block - in a kilobyte-sized image rather than a
    /// gigabyte-sized one. Changing it in the product is not something a user
    /// should be offered: the specification permits any power of two, but 2 MiB is
    /// the size Windows is actually exercised against.
    /// </remarks>
    public int BlockSize { get; init; } = VhdDynamicHeader.DefaultBlockSize;

    /// <summary>
    /// The disk's unique identifier, or null to generate a fresh one per write.
    /// </summary>
    public Guid? UniqueId { get; init; }

    /// <summary>
    /// The footer's creation timestamp, or null to stamp the current UTC time.
    /// </summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>The four-character <c>Creator Application</c> code.</summary>
    public string CreatorApplication { get; init; } = VhdFooter.DefaultCreatorApplication;

    /// <summary>
    /// The four-character <c>Creator Host OS</c> code. Windows by default: these
    /// VHDs exist to be attached by the Windows VHD provider, whatever machine
    /// wrote them.
    /// </summary>
    public string CreatorHostOs { get; init; } = VhdFooter.WindowsHostOs;

    /// <summary>
    /// Whether the destination is flushed to the operating system before the write
    /// is reported as successful. On by default.
    /// </summary>
    /// <remarks>
    /// The next thing that happens to a finished VHD is that Windows attaches it,
    /// which is a different handle onto the same file. Returning success with the
    /// footer still sitting in a managed buffer invites an attach against a
    /// truncated file.
    /// </remarks>
    public bool FlushWhenDone { get; init; } = true;

    /// <summary>
    /// Validates the settings, returning the failure a caller should propagate.
    /// </summary>
    internal Result<VhdWriteOptions> Validate()
    {
        if (BufferSize < MinimumBufferSize || BufferSize > MaximumBufferSize)
        {
            return Result<VhdWriteOptions>.Failure(DmgError.Internal(
                "The VHD copy buffer is outside the range this writer accepts.",
                $"buffer {BufferSize} bytes, allowed {MinimumBufferSize}..{MaximumBufferSize}"));
        }

        if (BufferSize % VhdFooter.SectorSize != 0)
        {
            return Result<VhdWriteOptions>.Failure(DmgError.Internal(
                "The VHD copy buffer must be a whole number of 512-byte sectors.",
                $"buffer {BufferSize} bytes leaves {BufferSize % VhdFooter.SectorSize} over"));
        }

        if (DiskType is not (VhdDiskType.Fixed or VhdDiskType.Dynamic))
        {
            return Result<VhdWriteOptions>.Failure(DmgError.Internal(
                "This writer produces fixed and dynamic VHDs and nothing else.",
                $"requested disk type {DiskType}"));
        }

        Result blockSize = VhdDynamicHeader.ValidateBlockSize(BlockSize);

        if (!blockSize.Ok)
        {
            return blockSize.CastFailure<VhdWriteOptions>();
        }

        return Result<VhdWriteOptions>.Success(this);
    }
}
