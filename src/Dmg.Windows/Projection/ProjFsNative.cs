using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dmg.Windows.Projection;

/// <summary>
/// The <c>ProjectedFSLib.dll</c> surface this tool uses, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Declared with <see cref="LibraryImportAttribute"/> for the same reason as
/// <see cref="VirtualDisk.VirtDiskNative"/>: the source generator emits the
/// marshalling at compile time, which is what survives NativeAOT publishing.
/// </para>
/// <para>
/// <b>This file interprets nothing.</b> Every function returns a raw
/// <c>HRESULT</c>; deciding what one means is
/// <see cref="ProjectionErrors"/>'s job, so that the deciding can be tested on a
/// machine with no ProjFS on it.
/// </para>
/// <para>
/// <b>The callbacks are the hard part.</b> ProjFS does not poll: it calls back
/// into this process, on its own threads, for as long as a projection is running.
/// Under NativeAOT those entry points must be <see cref="UnmanagedCallersOnlyAttribute"/>
/// static methods, which cannot close over anything - so the instance a callback
/// belongs to is reached through the <c>instanceContext</c> pointer handed to
/// <c>PrjStartVirtualizing</c> and looked up in a table. See
/// <c>WindowsProjectionService</c> for that table and for the threading rules it
/// implies.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ProjFsNative
{
    /// <summary>File attribute for a directory.</summary>
    internal const uint FileAttributeDirectory = 0x00000010;

    /// <summary>File attribute for a read-only item.</summary>
    internal const uint FileAttributeReadOnly = 0x00000001;

    /// <summary>File attribute for a hidden item.</summary>
    internal const uint FileAttributeHidden = 0x00000002;

    /// <summary><c>PRJ_FILE_BASIC_INFO</c>: what a placeholder tells Windows before any data is read.</summary>
    /// <remarks>
    /// <para>
    /// The times are Windows file times - 100-nanosecond ticks since 1601 - and a
    /// zero means "no opinion", which is what an exFAT entry with no recorded stamp
    /// should produce rather than a date in 1601.
    /// </para>
    /// <para>
    /// <b><c>IsDirectory</c> is a <see cref="byte"/>, not a <see cref="bool"/>.</b>
    /// The native field is a <c>BOOLEAN</c>, one byte, and a <c>bool</c> field -
    /// even with <c>[MarshalAs(UnmanagedType.U1)]</c> - makes this struct
    /// non-blittable. <see cref="LibraryImportAttribute"/> then refuses it unless
    /// the whole assembly turns off runtime marshalling, which would change how
    /// every other P/Invoke in <c>Dmg.Windows</c> marshals - <c>virtdisk.dll</c>,
    /// <c>kernel32.dll</c> and <c>advapi32.dll</c> included. Keeping the struct
    /// blittable is a one-byte change here instead of an assembly-wide one there.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrjFileBasicInfo
    {
        internal byte IsDirectoryByte;
        internal long FileSize;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;

        /// <summary>The directory flag, as the rest of this codebase wants to read it.</summary>
        internal bool IsDirectory
        {
            readonly get => IsDirectoryByte != 0;
            set => IsDirectoryByte = value ? (byte)1 : (byte)0;
        }
    }

    /// <summary><c>PRJ_PLACEHOLDER_VERSION_INFO</c>.</summary>
    /// <remarks>
    /// Both arrays are fixed at <c>PRJ_PLACEHOLDER_ID_LENGTH</c> (128) bytes. This
    /// build writes zeros into both: the content and provider identifiers are for a
    /// provider that wants to detect its own stale placeholders across restarts, and
    /// a projection that lives only as long as the command does has no use for them.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PrjPlaceholderVersionInfo
    {
        internal fixed byte ProviderID[128];
        internal fixed byte ContentID[128];
    }

    /// <summary><c>PRJ_PLACEHOLDER_INFO</c>.</summary>
    /// <remarks>
    /// <para>
    /// The structure ends with a <c>VariableData[1]</c> flexible array for extended
    /// attributes, a security descriptor and stream information. None of those are
    /// supplied here - the three size fields are left zero, which tells ProjFS to
    /// use the defaults - but <b>the trailing member still has to be declared</b>.
    /// </para>
    /// <para>
    /// <b>Leaving it out is a bug, and an obscure one.</b> ProjFS checks the size it
    /// is given against its own <c>sizeof(PRJ_PLACEHOLDER_INFO)</c>. Without the
    /// trailing byte and its alignment padding this struct measures 336 bytes where
    /// the native one is 344, so every <c>PrjWritePlaceholderInfo</c> is refused
    /// with <c>ERROR_INSUFFICIENT_BUFFER</c> - which Explorer reports as "the data
    /// area passed to a system call is too small". Directory listings still work,
    /// because <c>PrjFillDirEntryBuffer</c> takes no size, so the projection looks
    /// correct until something is opened. Both figures were measured, not reasoned
    /// about, after the first version of this file got it wrong.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PrjPlaceholderInfo
    {
        internal PrjFileBasicInfo FileBasicInfo;
        internal uint EaBufferSize;
        internal uint OffsetToFirstEa;
        internal uint SecurityBufferSize;
        internal uint OffsetToSecurityDescriptor;
        internal uint StreamsInfoBufferSize;
        internal uint OffsetToFirstStreamInfo;
        internal PrjPlaceholderVersionInfo VersionInfo;
        internal fixed byte VariableData[1];
    }

    /// <summary><c>PRJ_CALLBACK_DATA</c>: what every callback is told about the request.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrjCallbackData
    {
        internal uint Size;
        internal uint Flags;
        internal nint NamespaceVirtualizationContext;
        internal int CommandId;
        internal Guid FileId;
        internal Guid DataStreamId;
        internal nint FilePathName;
        internal nint VersionInfo;
        internal uint TriggeringProcessId;
        internal nint TriggeringProcessImageFileName;
        internal nint InstanceContext;
    }

    /// <summary>
    /// <c>PRJ_CALLBACKS</c>: the function pointers ProjFS calls. The first five are
    /// required; the last three are optional and left null.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrjCallbacks
    {
        internal nint StartDirectoryEnumerationCallback;
        internal nint EndDirectoryEnumerationCallback;
        internal nint GetDirectoryEnumerationCallback;
        internal nint GetPlaceholderInfoCallback;
        internal nint GetFileDataCallback;
        internal nint QueryFileNameCallback;
        internal nint NotificationCallback;
        internal nint CancelCommandCallback;
    }

    /// <summary>
    /// Marks a directory as the root of a projection. Called once, before
    /// <see cref="PrjStartVirtualizing"/>, and it persists on the directory.
    /// </summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjMarkDirectoryAsPlaceholder", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PrjMarkDirectoryAsPlaceholder(
        string rootPathName,
        string? targetPathName,
        nint versionInfo,
        in Guid virtualizationInstanceId);

    /// <summary>
    /// Starts serving a projection. The context comes back as a raw handle and is
    /// wrapped immediately, the same way <c>OpenVirtualDisk</c>'s is.
    /// </summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjStartVirtualizing", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PrjStartVirtualizing(
        string virtualizationRootPath,
        in PrjCallbacks callbacks,
        nint instanceContext,
        nint options,
        out nint namespaceVirtualizationContext);

    /// <summary>Stops serving a projection. Returns nothing and cannot fail.</summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjStopVirtualizing")]
    internal static partial void PrjStopVirtualizing(nint namespaceVirtualizationContext);

    /// <summary>Adds one entry to a directory enumeration ProjFS is collecting.</summary>
    /// <remarks>
    /// Returns <c>HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)</c> when the buffer
    /// is full, which is not a failure: it means "stop, I will ask again", and the
    /// enumeration must resume from that entry next time.
    /// </remarks>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjFillDirEntryBuffer", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PrjFillDirEntryBuffer(
        string fileName,
        in PrjFileBasicInfo fileBasicInfo,
        nint dirEntryBufferHandle);

    /// <summary>Tells ProjFS about one item's metadata, in answer to a placeholder request.</summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjWritePlaceholderInfo", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PrjWritePlaceholderInfo(
        nint namespaceVirtualizationContext,
        string destinationFileName,
        in PrjPlaceholderInfo placeholderInfo,
        uint placeholderInfoSize);

    /// <summary>Hands ProjFS a range of a file's contents.</summary>
    /// <remarks>
    /// The buffer must come from <see cref="PrjAllocateAlignedBuffer"/>: ProjFS
    /// writes it to the volume directly and the alignment is the filesystem's, not
    /// something a managed array can be relied on to satisfy.
    /// </remarks>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjWriteFileData")]
    internal static partial int PrjWriteFileData(
        nint namespaceVirtualizationContext,
        in Guid dataStreamId,
        nint buffer,
        ulong byteOffset,
        uint length);

    /// <summary>Allocates a buffer aligned for <see cref="PrjWriteFileData"/>.</summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjAllocateAlignedBuffer")]
    internal static partial nint PrjAllocateAlignedBuffer(
        nint namespaceVirtualizationContext,
        nuint size);

    /// <summary>Frees a buffer from <see cref="PrjAllocateAlignedBuffer"/>.</summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjFreeAlignedBuffer")]
    internal static partial void PrjFreeAlignedBuffer(nint buffer);

    /// <summary>
    /// Whether a name matches an enumeration's search expression. Used so that
    /// filtering behaves exactly as Windows expects rather than approximately.
    /// </summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjFileNameMatch", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool PrjFileNameMatch(string fileNameToCheck, string pattern);

    /// <summary>
    /// Whether a name contains wildcards. Also the cheapest exported function in
    /// the library, which is what the availability probe calls to find out whether
    /// ProjFS is on this machine at all.
    /// </summary>
    [LibraryImport("ProjectedFSLib.dll", EntryPoint = "PrjDoesNameContainWildCards", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool PrjDoesNameContainWildCards(string fileName);
}

/// <summary>
/// A <c>PRJ_NAMESPACE_VIRTUALIZATION_CONTEXT</c>, released by
/// <c>PrjStopVirtualizing</c>.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a bare <c>nint</c> for the same reason as
/// <c>SafeVirtualDiskHandle</c>: an early return or a throw must not be able to
/// leave a projection running with nothing owning it. A leaked context here leaves
/// a folder on the user's disk that Explorer will keep trying to populate from a
/// process that is gone.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SafeProjectionContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeProjectionContextHandle(nint existingHandle)
        : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle()
    {
        ProjFsNative.PrjStopVirtualizing(handle);

        return true;
    }
}
