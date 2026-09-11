using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dmg.Core;
using Microsoft.Win32.SafeHandles;

namespace Dmg.Windows.Volumes;

/// <summary>
/// The real <see cref="IVolumeService"/>: four calls into <c>kernel32.dll</c>,
/// every failure translated before it leaves the class.
/// </summary>
/// <remarks>
/// <para>
/// The judgement - which volumes are ours, how long to wait, what a failure means -
/// lives in <see cref="DriveLetterDiscovery"/> and <see cref="VolumeErrors"/>, both
/// of which run and are tested on any machine. What is left here is only the part
/// that genuinely needs Windows: opening a device, issuing an IOCTL, walking a
/// volume enumeration, and setting a mount point. It is deliberately as small and
/// as dull as it can be made.
/// </para>
/// <para>
/// Nothing here throws for an expected failure. A native error becomes a
/// <see cref="DmgError"/> via <see cref="VolumeErrors"/>; a growing buffer that
/// still is not big enough after Windows told it exactly how big to be is the one
/// case that is genuinely a bug in this tool, and even that is returned rather than
/// thrown.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeService : IVolumeService
{
    /// <summary>
    /// The first guess at a mount-point buffer, in characters. One drive letter and
    /// a couple of folder mount points fit comfortably; growing is cheap and rare.
    /// </summary>
    private const int InitialPathNamesBufferLength = 512;

    /// <summary>A ceiling, so a misbehaving driver cannot make the growth loop run forever.</summary>
    private const int MaximumPathNamesBufferLength = 65536;

    /// <inheritdoc />
    public Result<StorageDeviceNumber> GetDeviceNumber(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        // A volume name from EnumerateVolumes always carries the trailing
        // backslash SetVolumeMountPointW requires - and CreateFileW just as firmly
        // refuses, reading it as "open the volume's root directory" rather than
        // "open the volume". A physical device path has no trailing backslash to
        // begin with, so stripping one here is always correct.
        string openPath = devicePath.TrimEnd('\\');

        nint rawHandle = VolumeNative.CreateFileW(
            openPath,
            VolumeNative.GenericRead,
            VolumeNative.FileShareReadWrite,
            nint.Zero,
            VolumeNative.OpenExisting,
            VolumeNative.FileAttributeNormal,
            nint.Zero);

        using SafeFileHandle handle = new(rawHandle, ownsHandle: true);

        if (handle.IsInvalid)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();

            return Result<StorageDeviceNumber>.Failure(
                VolumeErrors.FromNative(error, VolumeOperation.OpenDevice, devicePath));
        }

        VolumeNative.StorageDeviceNumberNative native = default;

        bool ok = VolumeNative.DeviceIoControl(
            handle,
            VolumeNative.IoctlStorageGetDeviceNumber,
            nint.Zero,
            0,
            ref native,
            (uint)Marshal.SizeOf<VolumeNative.StorageDeviceNumberNative>(),
            out _,
            nint.Zero);

        if (!ok)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();

            return Result<StorageDeviceNumber>.Failure(
                VolumeErrors.FromNative(error, VolumeOperation.GetDeviceNumber, devicePath));
        }

        return Result<StorageDeviceNumber>.Success(
            new StorageDeviceNumber(native.DeviceType, native.DeviceNumber, native.PartitionNumber));
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<string>> EnumerateVolumes()
    {
        char[] buffer = new char[VolumeNative.VolumeNameBufferLength];

        nint rawHandle = FindFirstVolume(buffer);
        using SafeFindVolumeHandle handle = new(rawHandle);

        if (handle.IsInvalid)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();

            return Result<IReadOnlyList<string>>.Failure(
                VolumeErrors.FromNative(error, VolumeOperation.EnumerateVolumes, "the machine's volumes"));
        }

        List<string> volumes = [TrimAtNul(buffer)];

        while (true)
        {
            Array.Clear(buffer);

            if (FindNextVolume(handle, buffer))
            {
                volumes.Add(TrimAtNul(buffer));
                continue;
            }

            uint error = (uint)Marshal.GetLastPInvokeError();

            if (VolumeErrors.IsEndOfEnumeration(error))
            {
                return Result<IReadOnlyList<string>>.Success(volumes);
            }

            return Result<IReadOnlyList<string>>.Failure(
                VolumeErrors.FromNative(error, VolumeOperation.EnumerateVolumes, "the machine's volumes"));
        }
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<string>> GetVolumePathNames(string volumeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        int bufferLength = InitialPathNamesBufferLength;

        while (true)
        {
            char[] buffer = new char[bufferLength];
            Span<ushort> codeUnits = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());

            bool ok = VolumeNative.GetVolumePathNamesForVolumeNameW(
                volumeName,
                ref MemoryMarshal.GetReference(codeUnits),
                (uint)bufferLength,
                out uint returnLengthChars);

            if (ok)
            {
                return Result<IReadOnlyList<string>>.Success(SplitMultiString(buffer));
            }

            uint error = (uint)Marshal.GetLastPInvokeError();

            if (error != VolumeErrors.MoreData)
            {
                return Result<IReadOnlyList<string>>.Failure(
                    VolumeErrors.FromNative(error, VolumeOperation.GetVolumePathNames, volumeName));
            }

            if (returnLengthChars <= bufferLength || returnLengthChars > MaximumPathNamesBufferLength)
            {
                return Result<IReadOnlyList<string>>.Failure(DmgError.Internal(
                    $"Windows asked for an impossible mount-point buffer for '{volumeName}'.",
                    $"GetVolumePathNamesForVolumeNameW asked for {returnLengthChars} characters"));
            }

            bufferLength = (int)returnLengthChars;
        }
    }

    /// <inheritdoc />
    public Result AssignDriveLetter(string volumeName, string driveLetter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        string mountPoint = DriveLetter.Root(driveLetter);

        bool ok = VolumeNative.SetVolumeMountPointW(mountPoint, volumeName);

        if (ok)
        {
            return Result.Success();
        }

        uint error = (uint)Marshal.GetLastPInvokeError();

        return Result.Failure(VolumeErrors.FromNative(error, VolumeOperation.SetMountPoint, mountPoint));
    }

    private static nint FindFirstVolume(char[] buffer)
    {
        Span<ushort> codeUnits = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());

        return VolumeNative.FindFirstVolumeW(ref MemoryMarshal.GetReference(codeUnits), (uint)buffer.Length);
    }

    private static bool FindNextVolume(SafeFindVolumeHandle handle, char[] buffer)
    {
        Span<ushort> codeUnits = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());

        return VolumeNative.FindNextVolumeW(handle, ref MemoryMarshal.GetReference(codeUnits), (uint)buffer.Length);
    }

    private static string TrimAtNul(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');

        return end < 0 ? new string(buffer) : new string(buffer, 0, end);
    }

    /// <summary>
    /// Splits a <c>REG_MULTI_SZ</c> buffer - strings back to back, each ended by a
    /// null, the whole thing ended by one more - into the strings it holds.
    /// </summary>
    private static List<string> SplitMultiString(char[] buffer)
    {
        List<string> values = [];
        int start = 0;

        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                // The terminating empty string: two nulls in a row.
                break;
            }

            values.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        return values;
    }
}
