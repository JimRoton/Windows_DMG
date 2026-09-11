using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Windows.Volumes;

/// <summary>
/// Works out which drive letter the disk dmg just attached was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>The obvious implementation is wrong.</b> Reading <c>GetLogicalDrives</c>
/// before the attach and again after it, and taking the bit that appeared, is one
/// call and four lines - and it silently picks the wrong letter whenever anything
/// else on the machine gains a volume in the same window. A USB stick, a network
/// drive reconnecting, a second copy of dmg, an installer creating a RAM disk:
/// none of these is rare enough to ignore, and the failure is that dmg reports -
/// and then unmounts, and deletes the scratch VHD of - somebody else's disk. It
/// is also unreproducible by definition. This class does the slower thing that is
/// actually correct.
/// </para>
/// <para>
/// <b>The join.</b> Windows will not say "the disk you attached is E:". It will
/// say which physical device a handle sits on
/// (<c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>), it will list every volume on the
/// machine (<c>FindFirstVolume</c>/<c>FindNextVolume</c>), and it will say where a
/// volume is reachable from (<c>GetVolumePathNamesForVolumeName</c>). So: get our
/// device number from the physical path <c>AttachVirtualDisk</c> gave us, ask
/// every volume for its device number, keep the ones that match, and read the
/// letter off those. The match is on device <em>type and</em> number, because
/// numbers are only unique within a type.
/// </para>
/// <para>
/// <b>One volume's problem is not the machine's.</b> Enumeration walks everything
/// attached, including a card reader with no card and a locked BitLocker volume.
/// Those refuse to be opened. Discovery skips them and carries on, because the
/// alternative is a mount that fails on any machine with an empty SD slot. A
/// failure to identify <em>our own</em> disk is different, and is returned at
/// once: if that is broken, there is nothing to look for.
/// </para>
/// <para>
/// <b>Nothing here is Windows-specific.</b> Every native call is behind
/// <see cref="IVolumeService"/> and every wait is behind <see cref="IDelay"/>, so
/// the matching, the skipping, the retrying and every message can be exercised on
/// any machine. What cannot be exercised anywhere but real hardware is whether
/// the three native calls return what their documentation says - which is S10.4's
/// job.
/// </para>
/// </remarks>
public static class DriveLetterDiscovery
{
    /// <summary>
    /// Every volume that lives on the given physical device, right now, with no
    /// waiting and no retrying.
    /// </summary>
    /// <param name="volumes">The volume service.</param>
    /// <param name="physicalPath">
    /// The device path from <c>GetVirtualDiskPhysicalPath</c>, typically
    /// <c>\\.\PhysicalDrive3</c>.
    /// </param>
    /// <param name="output">Where to trace the volumes that were skipped. Optional.</param>
    /// <returns>
    /// The matching volumes, in enumeration order, possibly none - which is a
    /// normal answer while Windows is still surfacing them. A failure means our own
    /// device could not be identified or the machine's volumes could not be listed.
    /// </returns>
    public static Result<IReadOnlyList<VolumeOnDisk>> FindVolumesOn(
        IVolumeService volumes,
        string physicalPath,
        IOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        Result<StorageDeviceNumber> ours = volumes.GetDeviceNumber(physicalPath);

        if (!ours.TryGetValue(out StorageDeviceNumber? device))
        {
            // Our own disk. Not something to skip and not something to retry: if
            // Windows cannot say what device we just attached, there is no set of
            // volumes to search.
            return ours.CastFailure<IReadOnlyList<VolumeOnDisk>>();
        }

        Result<IReadOnlyList<string>> listed = volumes.EnumerateVolumes();

        if (!listed.TryGetValue(out IReadOnlyList<string>? volumeNames))
        {
            return listed.CastFailure<IReadOnlyList<VolumeOnDisk>>();
        }

        List<VolumeOnDisk> matches = [];

        foreach (string volumeName in volumeNames)
        {
            Result<StorageDeviceNumber> candidate = volumes.GetDeviceNumber(volumeName);

            if (!candidate.TryGetValue(out StorageDeviceNumber? candidateDevice))
            {
                output?.Trace($"Skipped '{volumeName}': {candidate.Error}");
                continue;
            }

            if (!device.IsSameDeviceAs(candidateDevice))
            {
                continue;
            }

            Result<IReadOnlyList<string>> pathNames = volumes.GetVolumePathNames(volumeName);

            if (!pathNames.TryGetValue(out IReadOnlyList<string>? paths))
            {
                // The volume is ours and we know where it is; we just cannot ask
                // where Windows mounted it. Recording it with no path names is
                // better than dropping it: it still proves the disk carries a
                // volume, which is what tells "no letter yet" apart from "Windows
                // cannot read this filesystem".
                output?.Trace($"Could not read the mount points of '{volumeName}': {pathNames.Error}");
                paths = [];
            }

            matches.Add(new VolumeOnDisk(volumeName, candidateDevice, paths));
        }

        return Result<IReadOnlyList<VolumeOnDisk>>.Success(matches);
    }

    /// <summary>
    /// Waits, boundedly, for a volume to appear on the given device.
    /// </summary>
    /// <remarks>
    /// What <c>--letter</c> needs (S8.5): the disk was attached with no letter on
    /// purpose, so there is nothing to wait for except the volume itself.
    /// </remarks>
    /// <param name="volumes">The volume service.</param>
    /// <param name="physicalPath">The device path from <c>GetVirtualDiskPhysicalPath</c>.</param>
    /// <param name="policy">How long to keep looking. Defaults to <see cref="VolumeWaitPolicy.Default"/>.</param>
    /// <param name="delay">How to wait. Defaults to really waiting.</param>
    /// <param name="output">Where to trace progress. Optional.</param>
    /// <returns>
    /// The first volume found on the device, or
    /// <see cref="DmgExitCode.FilesystemNotMountable"/> when none ever appears.
    /// </returns>
    public static Result<VolumeOnDisk> WaitForVolume(
        IVolumeService volumes,
        string physicalPath,
        VolumeWaitPolicy? policy = null,
        IDelay? delay = null,
        IOutput? output = null) =>
        Wait(
            volumes,
            physicalPath,
            candidates => candidates.Count > 0 ? candidates[0] : null,
            policy,
            delay,
            output);

    /// <summary>
    /// Waits, boundedly, for Windows to give the disk a drive letter.
    /// </summary>
    /// <param name="volumes">The volume service.</param>
    /// <param name="physicalPath">The device path from <c>GetVirtualDiskPhysicalPath</c>.</param>
    /// <param name="policy">How long to keep looking. Defaults to <see cref="VolumeWaitPolicy.Default"/>.</param>
    /// <param name="delay">How to wait. Defaults to really waiting.</param>
    /// <param name="output">Where to trace progress. Optional.</param>
    /// <returns>
    /// The canonical letter - <c>E</c>, not <c>E:</c> or <c>E:\</c> - or a failure
    /// that distinguishes the two ways this ends badly. No volume at all on the
    /// disk is <see cref="DmgExitCode.FilesystemNotMountable"/>: Windows attached
    /// it and found nothing it could read, which is what an HFS+ or APFS payload
    /// looks like from here and is a different problem with a different answer. A
    /// volume that never gets a letter is <see cref="DmgExitCode.MountFailed"/>,
    /// and the message points at <c>--letter</c>.
    /// </returns>
    public static Result<string> WaitForDriveLetter(
        IVolumeService volumes,
        string physicalPath,
        VolumeWaitPolicy? policy = null,
        IDelay? delay = null,
        IOutput? output = null)
    {
        Result<VolumeOnDisk> found = Wait(
            volumes,
            physicalPath,
            candidates => candidates.FirstOrDefault(volume => volume.HasDriveLetter),
            policy,
            delay,
            output);

        return found.TryGetValue(out VolumeOnDisk? volume)
            ? Result<string>.Success(volume.DriveLetter!)
            : found.CastFailure<string>();
    }

    /// <summary>
    /// What <c>--letter</c> needs (S8.5): waits for the volume on a disk that was
    /// deliberately attached with no letter, then gives it the one the user asked
    /// for.
    /// </summary>
    /// <remarks>
    /// There is no letter to wait for here, only the volume itself - the disk was
    /// attached with <c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c> for exactly
    /// this reason, so that Windows cannot assign one before this call does. Once
    /// the volume exists, assigning the letter is a single call whose only
    /// interesting failure - the letter already being taken - is reported clearly
    /// rather than as a bare Win32 code.
    /// </remarks>
    /// <param name="volumes">The volume service.</param>
    /// <param name="physicalPath">The device path from <c>GetVirtualDiskPhysicalPath</c>.</param>
    /// <param name="driveLetter">
    /// The canonical letter to assign, as <see cref="DriveLetter.Parse"/> produces
    /// it. Command-line validation happens there, before any work is attempted;
    /// this method trusts its caller and treats anything else as a bug.
    /// </param>
    /// <param name="policy">How long to keep looking for the volume. Defaults to <see cref="VolumeWaitPolicy.Default"/>.</param>
    /// <param name="delay">How to wait. Defaults to really waiting.</param>
    /// <param name="output">Where to trace progress. Optional.</param>
    /// <returns>
    /// <paramref name="driveLetter"/> once assigned, or a failure: no volume ever
    /// appeared (<see cref="DmgExitCode.FilesystemNotMountable"/>), or Windows
    /// refused the letter - most commonly because another volume already has it
    /// (<see cref="DmgExitCode.MountFailed"/>).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="driveLetter"/> is not canonical.</exception>
    public static Result<string> AssignDriveLetter(
        IVolumeService volumes,
        string physicalPath,
        string driveLetter,
        VolumeWaitPolicy? policy = null,
        IDelay? delay = null,
        IOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        if (DriveLetter.Normalise(driveLetter) != driveLetter)
        {
            throw new ArgumentException(
                $"'{driveLetter}' is not a canonical drive letter. Pass the result of "
                + $"{nameof(DriveLetter)}.{nameof(DriveLetter.Parse)}.",
                nameof(driveLetter));
        }

        Result<VolumeOnDisk> found = WaitForVolume(volumes, physicalPath, policy, delay, output);

        if (!found.TryGetValue(out VolumeOnDisk? volume))
        {
            return found.CastFailure<string>();
        }

        Result assigned = volumes.AssignDriveLetter(volume.VolumeName, driveLetter);

        return assigned.Ok ? Result<string>.Success(driveLetter) : assigned.CastFailure<string>();
    }

    /// <summary>
    /// The retry loop both waits share: look, take the first volume the caller
    /// wants, otherwise wait and look again - a fixed number of times.
    /// </summary>
    private static Result<VolumeOnDisk> Wait(
        IVolumeService volumes,
        string physicalPath,
        Func<IReadOnlyList<VolumeOnDisk>, VolumeOnDisk?> choose,
        VolumeWaitPolicy? policy,
        IDelay? delay,
        IOutput? output)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        policy ??= VolumeWaitPolicy.Default;
        delay ??= ThreadDelay.Instance;

        bool sawAVolume = false;

        for (int attempt = 1; attempt <= policy.Attempts; attempt++)
        {
            Result<IReadOnlyList<VolumeOnDisk>> found = FindVolumesOn(volumes, physicalPath, output);

            if (!found.TryGetValue(out IReadOnlyList<VolumeOnDisk>? candidates))
            {
                return found.CastFailure<VolumeOnDisk>();
            }

            sawAVolume |= candidates.Count > 0;

            if (choose(candidates) is VolumeOnDisk chosen)
            {
                if (attempt > 1)
                {
                    output?.Trace($"Found {chosen} on attempt {attempt} of {policy.Attempts}.");
                }

                return Result<VolumeOnDisk>.Success(chosen);
            }

            if (attempt < policy.Attempts)
            {
                delay.Wait(policy.Delay);
            }
        }

        return Result<VolumeOnDisk>.Failure(sawAVolume
            ? NoDriveLetter(physicalPath, policy)
            : NoVolume(physicalPath, policy));
    }

    private static DmgError NoVolume(string physicalPath, VolumeWaitPolicy policy) => new(
        DmgExitCode.FilesystemNotMountable,
        $"Windows attached the image as '{physicalPath}' but never showed a volume on it, so there "
        + "is nothing to give a drive letter to. The most likely reason is that the image holds an "
        + "HFS+ or APFS filesystem, which Windows has no driver for. The disk is still attached; run "
        + "dmg unmount to take it away.",
        $"No volume reported the device behind '{physicalPath}' after {policy.Attempts} attempts "
        + $"over {(long)policy.MaximumWait.TotalMilliseconds}ms.");

    private static DmgError NoDriveLetter(string physicalPath, VolumeWaitPolicy policy) => new(
        DmgExitCode.MountFailed,
        $"Windows found a volume on '{physicalPath}' but never assigned it a drive letter. Run the "
        + "command again with --letter to choose one yourself.",
        $"The volume had no mount point after {policy.Attempts} attempts over "
        + $"{(long)policy.MaximumWait.TotalMilliseconds}ms.");
}
