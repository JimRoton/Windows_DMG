using Dmg.Core;
using Dmg.Core.Diagnostics;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Mounts;

/// <summary>
/// What <c>dmg unmount</c> calls: detaches a disk by mount id, by drive letter, or
/// all of them, deletes the scratch <c>.vhd</c> behind it, and updates the
/// registry - the three things S8.9 asks for, in the order it asks for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Looks through the reconciled registry, not the raw one.</b> Every lookup
/// here goes through <see cref="MountRegistry.Read(IVirtualDiskService)"/> (S8.8),
/// so a ghost left by a reboot is never something this service tries to detach -
/// it was already dropped from the list before the id or drive letter is matched.
/// </para>
/// <para>
/// <b>Do not force.</b> If <see cref="IVirtualDiskService.Detach"/> fails - most
/// often because something still has a handle open on the mounted volume - that
/// failure is returned as-is and nothing else happens: the scratch file is not
/// deleted and the registry entry is not removed, so the user can see what is
/// holding the disk and try again once it lets go. There is no retry and no
/// forced detach; <see cref="IVirtualDiskService"/> does not even expose one.
/// </para>
/// <para>
/// <b>Asking to detach something already gone is not an error.</b> Matches
/// <see cref="MountRegistry.Remove"/>: an id or drive letter that is not currently
/// mounted has already got the user what they wanted, so it comes back as a
/// successful <see cref="DetachOutcome.NothingToDo"/> rather than a failure.
/// </para>
/// </remarks>
public sealed class DetachService
{
    private readonly MountRegistry _registry;
    private readonly IVirtualDiskService _virtualDiskService;
    private readonly IOutput? _output;

    /// <param name="registry">Where mounts are recorded.</param>
    /// <param name="virtualDiskService">Used to detach and, first, to reconcile the registry.</param>
    /// <param name="output">Where to report non-fatal trouble. Null says nothing.</param>
    public DetachService(MountRegistry registry, IVirtualDiskService virtualDiskService, IOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(virtualDiskService);

        _registry = registry;
        _virtualDiskService = virtualDiskService;
        _output = output;
    }

    /// <summary>Detaches the mount with the given id.</summary>
    /// <param name="id">A <see cref="MountRecord.Id"/>, matched case-insensitively.</param>
    /// <param name="keepScratch">Leave the scratch <c>.vhd</c> in place instead of deleting it.</param>
    public Result<DetachOutcome> DetachById(string id, bool keepScratch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        MountRecord? record = Find(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));

        return record is null
            ? Result<DetachOutcome>.Success(DetachOutcome.NothingToDo)
            : Detach(record, keepScratch);
    }

    /// <summary>Detaches whatever mount is using the given drive letter.</summary>
    /// <param name="driveLetter">Any of the forms a letter comes in - <c>E</c>, <c>e</c>, <c>E:</c>.</param>
    /// <param name="keepScratch">Leave the scratch <c>.vhd</c> in place instead of deleting it.</param>
    public Result<DetachOutcome> DetachByDriveLetter(string driveLetter, bool keepScratch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveLetter);

        string? normalised = MountRecord.NormaliseDriveLetter(driveLetter);

        if (normalised is null)
        {
            return Result<DetachOutcome>.Failure(DmgError.Usage(
                $"'{driveLetter}' is not a drive letter.",
                "dmg unmount expects a single letter A-Z, with or without a trailing colon."));
        }

        MountRecord? record = Find(record => record.DriveLetter == normalised);

        return record is null
            ? Result<DetachOutcome>.Success(DetachOutcome.NothingToDo)
            : Detach(record, keepScratch);
    }

    /// <summary>
    /// Detaches every mount currently on record. Best-effort across the list: one
    /// mount with a handle still open does not stop the rest from being detached.
    /// </summary>
    /// <param name="keepScratch">Leave every scratch <c>.vhd</c> in place instead of deleting it.</param>
    public DetachAllOutcome DetachAll(bool keepScratch = false)
    {
        IReadOnlyList<MountRecord> records = _registry.Read(_virtualDiskService);

        List<DetachOutcome> detached = new(records.Count);
        List<DetachFailure> failed = [];

        foreach (MountRecord record in records)
        {
            Result<DetachOutcome> result = Detach(record, keepScratch);

            if (result.TryGetValue(out DetachOutcome? outcome))
            {
                detached.Add(outcome);
            }
            else
            {
                failed.Add(new DetachFailure(record, result.Error));
            }
        }

        return new DetachAllOutcome(detached, failed);
    }

    private MountRecord? Find(Func<MountRecord, bool> matches) =>
        _registry.Read(_virtualDiskService).FirstOrDefault(matches);

    /// <summary>
    /// Detach, delete the VHD unless <paramref name="keepScratch"/>, update the
    /// registry - in that order, so a failure at any step leaves the state before
    /// it untouched.
    /// </summary>
    private Result<DetachOutcome> Detach(MountRecord record, bool keepScratch)
    {
        Result<IVirtualDiskHandle> opened =
            _virtualDiskService.Open(record.VhdPath, VirtualDiskAccessMode.ReadOnly);

        if (!opened.TryGetValue(out IVirtualDiskHandle? handle))
        {
            // Cannot even open it - gone by every measure that matters. There is
            // nothing left to detach, so this is the same "already done" outcome
            // as not finding it in the registry at all, not a failure.
            RemoveFromRegistry(record);

            return Result<DetachOutcome>.Success(DetachOutcome.Detached(record, scratchDeleted: false));
        }

        Result detachResult;

        using (handle)
        {
            detachResult = _virtualDiskService.Detach(handle);
        }

        if (!detachResult.Ok)
        {
            // Do not force. Report exactly what Windows said - most often that a
            // handle is still open on the volume - and leave the mount as it was
            // so the user can see it and retry.
            return detachResult.CastFailure<DetachOutcome>();
        }

        bool scratchDeleted = !keepScratch && TryDeleteScratch(record.VhdPath);

        RemoveFromRegistry(record);

        return Result<DetachOutcome>.Success(DetachOutcome.Detached(record, scratchDeleted));
    }

    /// <summary>
    /// Removes the record now that the disk is actually detached. Best-effort: if
    /// this loses a race with another writer, the next reconciled read (S8.8)
    /// notices the disk is gone and drops the ghost itself, so nothing here can
    /// leave a stale entry stuck forever.
    /// </summary>
    private void RemoveFromRegistry(MountRecord record)
    {
        Result<bool> removed = _registry.Remove(record.Id);

        if (!removed.Ok)
        {
            _output?.Trace(
                $"Detached '{record.Id}' but could not update the registry immediately: "
                + $"{removed.Error.Message} It will be cleaned up on the next read.");
        }
    }

    private bool TryDeleteScratch(string vhdPath)
    {
        try
        {
            File.Delete(vhdPath);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _output?.Warning(
                $"dmg detached the disk but could not delete the scratch file '{vhdPath}': "
                + $"{exception.Message} Delete it by hand when convenient.");

            return false;
        }
    }
}

/// <summary>What happened to one mount asked to detach.</summary>
/// <param name="WasMounted">
/// False when there was nothing to do - the id or drive letter did not match any
/// current mount. <see cref="Record"/> and <see cref="ScratchDeleted"/> are
/// meaningless when this is false.
/// </param>
/// <param name="Record">The mount that was detached.</param>
/// <param name="ScratchDeleted">
/// True when the scratch <c>.vhd</c> was deleted. Always false when the caller
/// asked to keep it, and false (with a warning already reported) if deletion was
/// attempted but failed.
/// </param>
public sealed record DetachOutcome(bool WasMounted, MountRecord? Record, bool ScratchDeleted)
{
    /// <summary>Nothing matched the id or drive letter asked for. Not an error - see the class remarks.</summary>
    public static DetachOutcome NothingToDo { get; } = new(false, null, false);

    /// <summary>One mount was detached.</summary>
    public static DetachOutcome Detached(MountRecord record, bool scratchDeleted) =>
        new(true, record, scratchDeleted);
}

/// <summary>One mount that <see cref="DetachService.DetachAll"/> could not detach, and why.</summary>
public sealed record DetachFailure(MountRecord Record, DmgError Error);

/// <summary>The result of asking to detach every current mount.</summary>
public sealed record DetachAllOutcome(
    IReadOnlyList<DetachOutcome> Detached,
    IReadOnlyList<DetachFailure> Failed);
