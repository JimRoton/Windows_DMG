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

        return Result<VhdWriteOptions>.Success(this);
    }
}
