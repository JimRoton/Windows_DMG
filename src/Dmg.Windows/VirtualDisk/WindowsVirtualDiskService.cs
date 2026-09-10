using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dmg.Core;

namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// The real <see cref="IVirtualDiskService"/>: four calls into
/// <c>virtdisk.dll</c>, every failure translated before it leaves the class.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this class's judgement is in choosing flags and deciding when to
/// stop. The interesting decisions - read-only by default, what a failure means,
/// what to tell the user - live in <see cref="VirtualDiskAccessMode"/>,
/// <see cref="VirtualDiskErrors"/> and the mount sequence above, all of which run
/// and are tested on any machine. What is left here is the part that genuinely
/// needs Windows, and it is deliberately as small and as dull as it can be made.
/// </para>
/// <para>
/// Nothing here throws for an expected failure. A native error becomes a
/// <see cref="DmgError"/>; only a broken invariant - a handle from somewhere else,
/// a handle already closed - produces an <see cref="DmgExitCode.InternalError"/>,
/// and even that is returned rather than thrown.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDiskService : IVirtualDiskService
{
    /// <summary>
    /// The first guess at a device-path buffer, in characters. <c>\\.\PhysicalDrive7</c>
    /// is nineteen characters; 260 is generous and one call is cheaper than two.
    /// </summary>
    private const int InitialPathBufferLength = 260;

    /// <summary>A ceiling, so a misbehaving driver cannot make this loop forever.</summary>
    private const int MaximumPathBufferLength = 32768;

    /// <inheritdoc />
    public Result<IVirtualDiskHandle> Open(string vhdPath, VirtualDiskAccessMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);

        VirtDiskNative.VirtualStorageType storageType = new()
        {
            DeviceId = VirtDiskNative.StorageTypeDeviceVhd,
            VendorId = VirtDiskNative.MicrosoftVendor,
        };

        // The access mask must already allow what the caller will go on to do:
        // asking for a read-write attach through a read-only handle fails, which is
        // exactly why the mode is fixed here and carried on the handle.
        uint accessMask = VirtDiskNative.AccessGetInfo | VirtDiskNative.AccessDetach | (
            mode == VirtualDiskAccessMode.ReadWrite
                ? VirtDiskNative.AccessAttachReadWrite
                : VirtDiskNative.AccessAttachReadOnly);

        uint error = VirtDiskNative.OpenVirtualDisk(
            in storageType,
            vhdPath,
            accessMask,
            VirtDiskNative.OpenFlagNone,
            nint.Zero,
            out nint rawHandle);

        // Wrapped before anything else can happen, so there is no window in which
        // the handle exists and nothing owns it.
        SafeVirtualDiskHandle handle = new(rawHandle);

        if (!VirtualDiskErrors.Succeeded(error))
        {
            handle.Dispose();

            return Result<IVirtualDiskHandle>.Failure(
                VirtualDiskErrors.FromNative(error, VirtualDiskOperation.Open, vhdPath));
        }

        return Result<IVirtualDiskHandle>.Success(new WindowsVirtualDiskHandle(handle, vhdPath, mode));
    }

    /// <inheritdoc />
    public Result<VirtualDiskAttachment> Attach(IVirtualDiskHandle handle, VirtualDiskAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(options);

        Result<WindowsVirtualDiskHandle> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out WindowsVirtualDiskHandle? native))
        {
            return resolved.CastFailure<VirtualDiskAttachment>();
        }

        uint flags = VirtDiskNative.AttachFlagNone;

        if (native.Mode == VirtualDiskAccessMode.ReadOnly)
        {
            flags |= VirtDiskNative.AttachFlagReadOnly;
        }

        if (options.PermanentLifetime)
        {
            flags |= VirtDiskNative.AttachFlagPermanentLifetime;
        }

        if (options.NoDriveLetter)
        {
            flags |= VirtDiskNative.AttachFlagNoDriveLetter;
        }

        VirtDiskNative.AttachVirtualDiskParameters parameters = new()
        {
            Version = VirtDiskNative.AttachParametersVersion1,
            Reserved = 0,
        };

        uint error = VirtDiskNative.AttachVirtualDisk(
            native.Handle,
            nint.Zero,
            flags,
            providerSpecificFlags: 0,
            in parameters,
            nint.Zero);

        if (!VirtualDiskErrors.Succeeded(error))
        {
            return Result<VirtualDiskAttachment>.Failure(
                VirtualDiskErrors.FromNative(error, VirtualDiskOperation.Attach, native.VhdPath));
        }

        // An attach with no discoverable device is not a usable mount, so the
        // physical path is fetched here and failure is reported as an attach
        // failure rather than left for the caller to trip over later.
        Result<string> physicalPath = GetPhysicalPath(native);

        if (!physicalPath.TryGetValue(out string? path))
        {
            return physicalPath.CastFailure<VirtualDiskAttachment>();
        }

        return Result<VirtualDiskAttachment>.Success(new VirtualDiskAttachment(
            native.VhdPath,
            path,
            native.Mode,
            options.PermanentLifetime));
    }

    /// <inheritdoc />
    public Result Detach(IVirtualDiskHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        Result<WindowsVirtualDiskHandle> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out WindowsVirtualDiskHandle? native))
        {
            return resolved.Discard();
        }

        uint error = VirtDiskNative.DetachVirtualDisk(
            native.Handle,
            VirtDiskNative.DetachFlagNone,
            providerSpecificFlags: 0);

        return VirtualDiskErrors.Succeeded(error)
            ? Result.Success()
            : Result.Failure(VirtualDiskErrors.FromNative(error, VirtualDiskOperation.Detach, native.VhdPath));
    }

    /// <inheritdoc />
    public Result<string> GetPhysicalPath(IVirtualDiskHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        Result<WindowsVirtualDiskHandle> resolved = Resolve(handle);

        if (!resolved.TryGetValue(out WindowsVirtualDiskHandle? native))
        {
            return resolved.CastFailure<string>();
        }

        int bufferLength = InitialPathBufferLength;

        while (true)
        {
            char[] buffer = new char[bufferLength];
            uint sizeInBytes = checked((uint)(bufferLength * sizeof(char)));

            Span<ushort> codeUnits = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());

            uint error = VirtDiskNative.GetVirtualDiskPhysicalPath(
                native.Handle,
                ref sizeInBytes,
                ref MemoryMarshal.GetReference(codeUnits));

            if (VirtualDiskErrors.Succeeded(error))
            {
                return Result<string>.Success(TrimAtNul(buffer));
            }

            if (error != VirtualDiskErrors.InsufficientBuffer)
            {
                return Result<string>.Failure(
                    VirtualDiskErrors.FromNative(error, VirtualDiskOperation.GetPhysicalPath, native.VhdPath));
            }

            // The call writes the size it wants, in bytes, on the way out.
            int wanted = (int)(sizeInBytes / sizeof(char));

            if (wanted <= bufferLength || wanted > MaximumPathBufferLength)
            {
                return Result<string>.Failure(DmgError.Internal(
                    $"Windows asked for an impossible device-path buffer for '{native.VhdPath}'.",
                    $"GetVirtualDiskPhysicalPath asked for {sizeInBytes} bytes"));
            }

            bufferLength = wanted;
        }
    }

    private static string TrimAtNul(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');

        return end < 0 ? new string(buffer) : new string(buffer, 0, end);
    }

    private static Result<WindowsVirtualDiskHandle> Resolve(IVirtualDiskHandle handle)
    {
        if (handle is not WindowsVirtualDiskHandle native)
        {
            return Result<WindowsVirtualDiskHandle>.Failure(DmgError.Internal(
                "That virtual disk handle did not come from the Windows virtual disk service.",
                $"handle type {handle.GetType().Name}"));
        }

        if (!native.IsOpen)
        {
            return Result<WindowsVirtualDiskHandle>.Failure(DmgError.Internal(
                "That virtual disk handle has already been closed.",
                $"handle for '{native.VhdPath}'"));
        }

        return Result<WindowsVirtualDiskHandle>.Success(native);
    }
}

/// <summary>The real handle: a <see cref="SafeVirtualDiskHandle"/> plus the two facts callers need.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVirtualDiskHandle : IVirtualDiskHandle
{
    internal WindowsVirtualDiskHandle(SafeVirtualDiskHandle handle, string vhdPath, VirtualDiskAccessMode mode)
    {
        Handle = handle;
        VhdPath = vhdPath;
        Mode = mode;
    }

    internal SafeVirtualDiskHandle Handle { get; }

    public string VhdPath { get; }

    public VirtualDiskAccessMode Mode { get; }

    public bool IsOpen => !Handle.IsClosed && !Handle.IsInvalid;

    public void Dispose()
    {
        Handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
