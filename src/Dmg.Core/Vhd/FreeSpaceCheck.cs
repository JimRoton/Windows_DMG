namespace Dmg.Core.Vhd;

/// <summary>
/// The precheck that runs before a single byte of VHD is written: is there room
/// for the whole thing?
/// </summary>
/// <remarks>
/// <para>
/// A DMG compresses. A 900 MB download can decode to 40 GB, and the user has no
/// way of knowing that before they run the command. Without this check the
/// failure mode is the worst one available: twenty minutes of decoding, a scratch
/// volume filled to zero bytes free - which is enough to destabilise Windows
/// itself - and then a write error. With it, the command refuses in a
/// millisecond and says what it needed and what there was.
/// </para>
/// <para>
/// <b>The failure is exit code 8, and it reports both figures.</b>
/// "Not enough space" on its own is useless; "needs 40.2 GB, 12.6 GB available"
/// tells the user how much to clear or that they want <c>--scratch D:\</c>.
/// </para>
/// <para>
/// <b>A probe that cannot answer does not stop the conversion.</b> An exotic
/// filesystem or a permission quirk that hides the free-space figure is not a
/// reason to refuse work that might well succeed - and if it does not, the writer
/// still reports the full disk as exit code 8 when it happens. The precheck is an
/// early, cheap answer to a common problem, not a gate.
/// </para>
/// </remarks>
public static class FreeSpaceCheck
{
    /// <summary>
    /// The margin insisted on beyond the file itself: 64 MiB.
    /// </summary>
    /// <remarks>
    /// Windows behaves badly on a volume with nothing left - the page file cannot
    /// grow, and a VHD attached from a completely full volume is a bad place to be
    /// - so the last sixty-four megabytes are treated as not ours.
    /// </remarks>
    public const long DefaultMarginBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Checks that <paramref name="requiredBytes"/> will fit on the volume holding
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Where the file is to be written. Need not exist yet.</param>
    /// <param name="requiredBytes">
    /// The size of the finished file. Use <see cref="VhdWriter.FixedFileSizeFor"/>
    /// so the footer and the sector padding are counted too.
    /// </param>
    /// <param name="probe">The volume probe; null for the real one.</param>
    /// <param name="marginBytes">
    /// Extra bytes to insist on beyond the file itself, so a conversion does not
    /// leave the volume at absolute zero. Defaults to
    /// <see cref="DefaultMarginBytes"/>.
    /// </param>
    /// <returns>
    /// Success when there is room, or when the volume could not be measured;
    /// <see cref="DmgExitCode.InsufficientSpace"/> when there is not.
    /// </returns>
    public static Result Require(
        string path,
        long requiredBytes,
        IFreeSpaceProbe? probe = null,
        long marginBytes = DefaultMarginBytes)
    {
        Result<FreeSpaceReport> measured = Measure(path, requiredBytes, probe, marginBytes);

        if (!measured.TryGetValue(out FreeSpaceReport? report))
        {
            // Unmeasurable, not insufficient. Say nothing and let the write proceed.
            return Result.Success();
        }

        return report.HasRoom
            ? Result.Success()
            : Result.Failure(report.ToError());
    }

    /// <summary>
    /// Measures the volume and reports what was needed against what there is,
    /// whether or not it fits.
    /// </summary>
    /// <returns>
    /// The report, or the probe's failure when the volume could not be measured at
    /// all. A report is produced for a volume with too little room; that is not a
    /// failure of measurement.
    /// </returns>
    public static Result<FreeSpaceReport> Measure(
        string path,
        long requiredBytes,
        IFreeSpaceProbe? probe = null,
        long marginBytes = DefaultMarginBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(marginBytes);

        Result<long> available = (probe ?? DriveFreeSpaceProbe.Instance).AvailableBytes(path);

        if (!available.TryGetValue(out long free))
        {
            return available.CastFailure<FreeSpaceReport>();
        }

        return Result<FreeSpaceReport>.Success(
            new FreeSpaceReport(path, requiredBytes, marginBytes, free));
    }
}

/// <summary>
/// What the precheck found: what the write needs, and what the volume has.
/// </summary>
/// <param name="Path">The path that was measured.</param>
/// <param name="RequiredBytes">The size of the file to be written.</param>
/// <param name="MarginBytes">The headroom insisted on beyond it.</param>
/// <param name="AvailableBytes">What the volume reported as available to this user.</param>
public sealed record FreeSpaceReport(
    string Path,
    long RequiredBytes,
    long MarginBytes,
    long AvailableBytes)
{
    /// <summary>Everything the write must find free: the file plus the margin.</summary>
    public long TotalRequiredBytes => RequiredBytes + MarginBytes;

    /// <summary>True when the volume has room for the file and the margin.</summary>
    public bool HasRoom => AvailableBytes >= TotalRequiredBytes;

    /// <summary>How many bytes short the volume is, or zero when it is not short.</summary>
    public long ShortfallBytes => HasRoom ? 0 : TotalRequiredBytes - AvailableBytes;

    /// <summary>
    /// The failure to return when there is not enough room: exit code 8, naming
    /// both figures.
    /// </summary>
    /// <exception cref="InvalidOperationException">There was enough room after all.</exception>
    public DmgError ToError()
    {
        if (HasRoom)
        {
            throw new InvalidOperationException(
                "A free-space report with room to spare does not describe a failure.");
        }

        return new DmgError(
            DmgExitCode.InsufficientSpace,
            $"Not enough space to write the image: {Describe(TotalRequiredBytes)} needed, "
            + $"{Describe(AvailableBytes)} available.",
            $"'{Path}': requires {RequiredBytes} bytes plus a {MarginBytes}-byte margin, "
            + $"{AvailableBytes} available, {ShortfallBytes} short");
    }

    /// <summary>A one-line summary for verbose output.</summary>
    public override string ToString() =>
        $"{Describe(AvailableBytes)} available at '{Path}', {Describe(TotalRequiredBytes)} needed";

    /// <summary>
    /// Renders a byte count the way a person reads one, in binary units.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>ToString("N0")</c>: "43,184,520,704 bytes" is a number
    /// the user has to count the digits of. "40.2 GiB" is one they can compare with
    /// what Explorer told them.
    /// </remarks>
    internal static string Describe(long bytes)
    {
        string[] units = ["bytes", "KiB", "MiB", "GiB", "TiB", "PiB"];

        if (bytes < 1024)
        {
            return $"{bytes} {units[0]}";
        }

        double scaled = bytes;
        int unit = 0;

        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return $"{scaled:0.#} {units[unit]}";
    }
}
