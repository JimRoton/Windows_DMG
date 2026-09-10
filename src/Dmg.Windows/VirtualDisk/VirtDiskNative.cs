using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dmg.Windows.VirtualDisk;

/// <summary>
/// The <c>virtdisk.dll</c> surface this tool uses, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Declared with <see cref="LibraryImportAttribute"/> rather than
/// <c>DllImport</c>. The source generator emits the marshalling code at compile
/// time, which is what makes it survive NativeAOT publishing - a <c>DllImport</c>
/// with a <see cref="string"/> parameter needs the runtime marshaller, and the
/// runtime marshaller is exactly what AOT removes.
/// </para>
/// <para>
/// Every function here returns a Win32 <c>DWORD</c>, not an HRESULT and not a
/// <c>bool</c> with <c>GetLastError</c>. Nothing in this file interprets those
/// codes; that is <see cref="VirtualDiskErrors"/>'s job, and keeping it separate is
/// what lets the interpretation be tested without Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class VirtDiskNative
{
    /// <summary><c>VIRTUAL_STORAGE_TYPE_DEVICE_VHD</c>.</summary>
    internal const uint StorageTypeDeviceVhd = 2;

    /// <summary><c>VIRTUAL_DISK_ACCESS_ATTACH_RO</c>.</summary>
    internal const uint AccessAttachReadOnly = 0x0001_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_ATTACH_RW</c>.</summary>
    internal const uint AccessAttachReadWrite = 0x0002_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_DETACH</c>.</summary>
    internal const uint AccessDetach = 0x0004_0000;

    /// <summary><c>VIRTUAL_DISK_ACCESS_GET_INFO</c>.</summary>
    internal const uint AccessGetInfo = 0x0008_0000;

    /// <summary><c>OPEN_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    internal const uint OpenFlagNone = 0;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    internal const uint AttachFlagNone = 0;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY</c>.</summary>
    internal const uint AttachFlagReadOnly = 0x0000_0001;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER</c>.</summary>
    internal const uint AttachFlagNoDriveLetter = 0x0000_0002;

    /// <summary><c>ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME</c>.</summary>
    internal const uint AttachFlagPermanentLifetime = 0x0000_0004;

    /// <summary><c>DETACH_VIRTUAL_DISK_FLAG_NONE</c>.</summary>
    internal const uint DetachFlagNone = 0;

    /// <summary><c>ATTACH_VIRTUAL_DISK_VERSION_1</c>.</summary>
    internal const uint AttachParametersVersion1 = 1;

    /// <summary><c>VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT</c>.</summary>
    internal static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    /// <summary><c>VIRTUAL_STORAGE_TYPE</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualStorageType
    {
        internal uint DeviceId;
        internal Guid VendorId;
    }

    /// <summary>
    /// <c>ATTACH_VIRTUAL_DISK_PARAMETERS</c>, version 1 - which is a version number
    /// and a reserved word, and nothing else.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AttachVirtualDiskParameters
    {
        internal uint Version;
        internal uint Reserved;
    }

    /// <summary>
    /// Opens a virtual disk. <c>Parameters</c> is passed as <c>NULL</c>: it is
    /// documented as optional, and the defaults are what a plain fixed VHD wants.
    /// Passing a versioned union we cannot exercise on this machine would be a
    /// larger risk than taking the documented default.
    /// </summary>
    /// <remarks>
    /// The handle comes back as a raw <see cref="nint"/> and is wrapped by the
    /// caller. The source-generated <c>SafeHandle</c> marshaller only travels
    /// managed-to-unmanaged, so an <c>out SafeHandle</c> is not something
    /// <see cref="LibraryImportAttribute"/> can produce - the wrapping has to be
    /// done by hand, immediately, before anything can throw.
    /// </remarks>
    [LibraryImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint OpenVirtualDisk(
        in VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        nint parameters,
        out nint handle);

    /// <summary>
    /// Attaches an open virtual disk. The security descriptor and the overlapped
    /// structure are both <c>NULL</c>: the default descriptor is what Disk
    /// Management itself uses, and the call is made synchronously.
    /// </summary>
    [LibraryImport("virtdisk.dll", EntryPoint = "AttachVirtualDisk")]
    internal static partial uint AttachVirtualDisk(
        SafeVirtualDiskHandle virtualDiskHandle,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        in AttachVirtualDiskParameters parameters,
        nint overlapped);

    /// <summary>Detaches an attached virtual disk.</summary>
    [LibraryImport("virtdisk.dll", EntryPoint = "DetachVirtualDisk")]
    internal static partial uint DetachVirtualDisk(
        SafeVirtualDiskHandle virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);

    /// <summary>
    /// Asks what physical device an attached disk became. The size is in
    /// <em>bytes</em>, not characters - a distinction this API is unforgiving about.
    /// </summary>
    /// <remarks>
    /// The buffer is typed as <see cref="ushort"/> rather than <see cref="char"/>:
    /// marshalling a bare <c>ref char</c> would need runtime marshalling disabled
    /// assembly-wide, and a UTF-16 code unit is a <see cref="ushort"/> anyway. The
    /// caller reinterprets its <c>char</c> buffer, which costs nothing.
    /// </remarks>
    [LibraryImport("virtdisk.dll", EntryPoint = "GetVirtualDiskPhysicalPath")]
    internal static partial uint GetVirtualDiskPhysicalPath(
        SafeVirtualDiskHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes,
        ref ushort diskPath);
}

/// <summary>
/// A handle from <c>OpenVirtualDisk</c>, closed by <c>CloseHandle</c>.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a bare <c>IntPtr</c> so that a handle
/// cannot be leaked by an early return or a thrown exception, and cannot be used
/// after it has been closed. Both of those failure modes leave a virtual disk
/// attached with nothing holding it, which is a mess to clean up by hand.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class SafeVirtualDiskHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeVirtualDiskHandle(nint existingHandle)
        : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
