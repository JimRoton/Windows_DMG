namespace Dmg.Windows.Volumes;

/// <summary>
/// How long drive-letter discovery keeps asking before it concludes the answer is
/// not coming.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why any waiting at all.</b> <c>AttachVirtualDisk</c> returns as soon as the
/// disk exists, not when Windows has finished with it. The volume behind it is
/// surfaced by the storage stack, the mount manager assigns a letter, and
/// Explorer is notified - all after the call this tool made has already returned.
/// Asking once, immediately, finds nothing on a busy machine perhaps one time in
/// twenty, and the user sees a mount that worked but reported no drive letter.
/// </para>
/// <para>
/// <b>Why it is bounded.</b> The other half of the same fact: there are disks that
/// never get a letter, because Windows has no driver for the filesystem on them -
/// which is the expected outcome for an HFS+ or APFS payload, not a rare one. An
/// unbounded wait would hang the command line on the case this tool exists to
/// report clearly. So the loop counts its attempts, and running out is an answer
/// rather than a hang.
/// </para>
/// </remarks>
/// <param name="Attempts">How many times to look, including the first. At least one.</param>
/// <param name="Delay">How long to wait between looks. Not waited after the last one.</param>
public sealed record VolumeWaitPolicy(int Attempts, TimeSpan Delay)
{
    /// <summary>How many times to look. Guaranteed to be at least one.</summary>
    public int Attempts { get; } = Attempts >= 1
        ? Attempts
        : throw new ArgumentOutOfRangeException(
            nameof(Attempts),
            Attempts,
            "Discovery has to look at least once.");

    /// <summary>How long to wait between looks. Never negative.</summary>
    public TimeSpan Delay { get; } = Delay >= TimeSpan.Zero
        ? Delay
        : throw new ArgumentOutOfRangeException(
            nameof(Delay),
            Delay,
            "A wait cannot be negative.");

    /// <summary>
    /// What <c>dmg mount</c> uses: twenty looks a quarter of a second apart, so
    /// roughly five seconds in the worst case.
    /// </summary>
    /// <remarks>
    /// Long enough that a machine busy enough to take a second still works, short
    /// enough that the far more common "Windows will never mount this filesystem"
    /// answer arrives while the user is still watching.
    /// </remarks>
    public static VolumeWaitPolicy Default { get; } = new(20, TimeSpan.FromMilliseconds(250));

    /// <summary>One look and no waiting - for callers asking about a settled machine.</summary>
    public static VolumeWaitPolicy Once { get; } = new(1, TimeSpan.Zero);

    /// <summary>The longest this policy can spend, for the message when it runs out.</summary>
    public TimeSpan MaximumWait => Delay * (Attempts - 1);
}
