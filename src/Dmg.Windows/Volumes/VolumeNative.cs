using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dmg.Windows.Volumes;

/// <summary>
/// The <c>kernel32.dll</c> surface drive-letter discovery and assignment need, and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Declared with <see cref="LibraryImportAttribute"/>, the same choice
/// <c>VirtDiskNative</c> makes and for the same reason: the source generator emits
/// the marshalling code at compile time, which is what survives NativeAOT
/// publishing.
/// </para>
/// <para>
/// Every function here reports failure the way its own Win32 signature does - a
/// <c>BOOL</c> plus <c>GetLastError</c>, or a sentinel handle plus
/// <c>GetLastError</c> - and nothing here interprets what a code means. That is
/// <see cref="VolumeErrors"/>'s job, kept separate so the interpretation can be
/// tested without Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class VolumeNative
{
    /// <summary><c>GENERIC_READ</c>. Enough to issue <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>.</summary>
    internal const uint GenericRead = 0x8000_0000;

    /// <summary>
    /// <c>FILE_SHARE_READ | FILE_SHARE_WRITE</c>. A device already open elsewhere -
    /// which is the common case, since this tool is not the only thing that has the
    /// disk open - must not block discovery from opening it too.
    /// </summary>
    internal const uint FileShareReadWrite = 0x1 | 0x2;

    /// <summary><c>OPEN_EXISTING</c>. A device or volume is never created here.</summary>
    internal const uint OpenExisting = 3;

    /// <summary><c>FILE_ATTRIBUTE_NORMAL</c>.</summary>
    internal const uint FileAttributeNormal = 0x80;

    /// <summary><c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>.</summary>
    internal const uint IoctlStorageGetDeviceNumber = 0x002D_1080;

    /// <summary>
    /// The buffer size <c>FindFirstVolumeW</c>/<c>FindNextVolumeW</c> document as
    /// sufficient for a volume GUID path, <c>\\?\Volume{GUID}\</c>, with room to
    /// spare.
    /// </summary>
    internal const int VolumeNameBufferLength = 64;

    /// <summary><c>STORAGE_DEVICE_NUMBER</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StorageDeviceNumberNative
    {
        internal uint DeviceType;
        internal uint DeviceNumber;

        // Windows documents this field as a DWORD and then writes 0xFFFFFFFF into
        // it for "this is not a partition" - the same trade
        // Dmg.Windows.Volumes.StorageDeviceNumber makes, and for the same reason:
        // read back as signed, that value is simply -1.
        internal int PartitionNumber;
    }

    /// <summary>
    /// Opens a device or volume for the one thing this tool ever does with the
    /// handle: issue <see cref="IoctlStorageGetDeviceNumber"/>. No write access and
    /// no creation - <c>OPEN_EXISTING</c> against something that already exists or
    /// this call has nothing to do.
    /// </summary>
    /// <remarks>
    /// The handle comes back as a raw <see cref="nint"/>, wrapped by the caller
    /// immediately into a <see cref="SafeFileHandle"/> - the same reason
    /// <c>OpenVirtualDisk</c> is declared with an <c>out nint</c> rather than an
    /// <c>out SafeHandle</c>: the source-generated marshaller only travels
    /// managed-to-unmanaged, so producing a <see cref="SafeHandle"/> from a return
    /// value is not something <see cref="LibraryImportAttribute"/> can do.
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>Issues an IOCTL. Used here only for <see cref="IoctlStorageGetDeviceNumber"/>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        nint inBuffer,
        uint inBufferSize,
        ref StorageDeviceNumberNative outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    /// <summary>
    /// Starts enumerating the volumes on the machine. Failure is a sentinel handle,
    /// not null, and is wrapped by the caller the same way <c>CreateFileW</c>'s
    /// result is.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint FindFirstVolumeW(ref ushort volumeName, uint bufferLengthChars);

    /// <summary>Advances a search started by <see cref="FindFirstVolumeW"/>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolumeW(
        SafeFindVolumeHandle findVolumeHandle,
        ref ushort volumeName,
        uint bufferLengthChars);

    /// <summary>Closes a search started by <see cref="FindFirstVolumeW"/>. Not <c>CloseHandle</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(nint findVolumeHandle);

    /// <summary>
    /// Asks where a volume is mounted. The buffer Windows fills in is a
    /// <c>REG_MULTI_SZ</c>: zero or more null-terminated strings back to back,
    /// followed by one more null - the caller splits it.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNamesForVolumeNameW(
        string volumeName,
        ref ushort volumePathNames,
        uint bufferLengthChars,
        out uint returnLengthChars);

    /// <summary>Gives a volume a drive letter (or an empty NTFS folder) as its mount point.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetVolumeMountPointW(string volumeMountPoint, string volumeName);
}

/// <summary>
/// A search handle from <see cref="VolumeNative.FindFirstVolumeW"/>, closed by
/// <c>FindVolumeClose</c> - a different call from the <c>CloseHandle</c> every
/// other handle in this tool uses, which is why this is its own <see cref="SafeHandle"/>
/// rather than a reuse of one that wraps <c>CloseHandle</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeFindVolumeHandle : SafeHandleMinusOneIsInvalid
{
    internal SafeFindVolumeHandle(nint existingHandle)
        : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle() => VolumeNative.FindVolumeClose(handle);
}
