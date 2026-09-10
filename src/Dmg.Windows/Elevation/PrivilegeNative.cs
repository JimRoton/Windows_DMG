using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dmg.Windows.Elevation;

/// <summary>
/// The token API surface this tool uses, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Declared with <see cref="LibraryImportAttribute"/> for the same reason the
/// virtual disk bindings are: the marshalling code is generated at compile time and
/// survives NativeAOT publishing, where the runtime marshaller a <c>DllImport</c>
/// would need has been removed.
/// </para>
/// <para>
/// Every function here reports failure the Win32 way - <c>false</c> plus
/// <c>GetLastError</c> - and nothing in this file interprets the result. That is
/// <see cref="WindowsPrivilegeService"/>'s job, and the split is what keeps the
/// interpretation testable on a machine with no tokens at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class PrivilegeNative
{
    /// <summary><c>TOKEN_QUERY</c>. Read-only: this tool never adjusts a token.</summary>
    internal const uint TokenQuery = 0x0008;

    /// <summary><c>TokenPrivileges</c> in <c>TOKEN_INFORMATION_CLASS</c>.</summary>
    internal const int TokenPrivileges = 3;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>, which the sizing call always returns.</summary>
    internal const int InsufficientBuffer = 122;

    /// <summary>
    /// <c>LUID</c>. A locally unique identifier - the number a privilege name maps
    /// to on this machine, this boot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid : IEquatable<Luid>
    {
        internal uint LowPart;
        internal int HighPart;

        public readonly bool Equals(Luid other) =>
            LowPart == other.LowPart && HighPart == other.HighPart;

        public override readonly bool Equals(object? obj) => obj is Luid other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(LowPart, HighPart);

        public static bool operator ==(Luid left, Luid right) => left.Equals(right);

        public static bool operator !=(Luid left, Luid right) => !left.Equals(right);
    }

    /// <summary>The pseudo-handle for the current process. Never needs closing.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    internal static partial nint GetCurrentProcess();

    /// <summary>
    /// Opens this process's access token. The handle comes back raw and is wrapped
    /// by the caller immediately, for the reason given on
    /// <see cref="SafeTokenHandle"/>.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    /// <summary>
    /// Maps a privilege name to its LUID on this machine. Privilege LUIDs are not
    /// constants and must not be hardcoded - they are assigned at boot.
    /// </summary>
    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "LookupPrivilegeValueW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    /// <summary>
    /// Reads one class of information out of a token.
    /// </summary>
    /// <remarks>
    /// Called twice: once with a zero-length buffer to learn the size, which fails
    /// with <c>ERROR_INSUFFICIENT_BUFFER</c> and is not an error, and once for real.
    /// The buffer is a byte span because <c>TOKEN_PRIVILEGES</c> ends in a
    /// variable-length array, which no fixed struct can describe.
    /// </remarks>
    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        SafeTokenHandle token,
        int informationClass,
        Span<byte> information,
        int length,
        out int returnLength);
}

/// <summary>
/// A handle from <c>OpenProcessToken</c>, closed by <c>CloseHandle</c>.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a bare <c>IntPtr</c> so that an early
/// return cannot leak it. A leaked token handle is less dramatic than a leaked
/// virtual disk handle - nothing stays mounted - but it is a handle leak in a
/// process that may go on to run for a while, and the safe type costs nothing.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeTokenHandle(nint existingHandle)
        : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
