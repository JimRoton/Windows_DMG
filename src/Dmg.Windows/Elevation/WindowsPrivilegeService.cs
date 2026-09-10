using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dmg.Core;

namespace Dmg.Windows.Elevation;

/// <summary>
/// The real <see cref="IPrivilegeService"/>: opens this process's token and looks
/// one privilege up in it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately dull, like the virtual disk service it sits beside. Everything that
/// involves a decision - what counts as held, what to say when it is not, when to
/// ask - is in <see cref="ElevationCheck"/> and <see cref="PrivilegeState"/>, both
/// of which run and are tested on any machine. What is left here is three system
/// calls and some pointer arithmetic over a variable-length structure.
/// </para>
/// <para>
/// <b>Read-only.</b> The token is opened with <c>TOKEN_QUERY</c> and nothing else.
/// This class never adjusts a privilege, never impersonates, and never launches
/// anything - see the remarks on <see cref="ElevationCheck"/> for why there is no
/// self-elevation path anywhere in this tool.
/// </para>
/// <para>
/// <b>A missing privilege is not an error.</b> It is the answer, and it comes back
/// as a successful <see cref="Result{T}"/> holding
/// <see cref="PrivilegeState.Absent"/>. Only a token that cannot be read at all is
/// a failure, and that is an internal error: a process can always open its own
/// token, so if it cannot, something is wrong with this code and not with the
/// user's shell.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegeService : IPrivilegeService
{
    /// <summary>
    /// A ceiling on the token privilege block, so a nonsense size from the API
    /// cannot become a nonsense allocation. Windows defines about three dozen
    /// privileges at twelve bytes each; 64 KiB is four orders of magnitude of room.
    /// </summary>
    private const int MaximumPrivilegeBlockBytes = 64 * 1024;

    /// <summary>
    /// <c>ERROR_NO_SUCH_PRIVILEGE</c>. The name did not resolve, which means the
    /// name is wrong - privilege names are compile-time constants here.
    /// </summary>
    private const int NoSuchPrivilege = 1313;

    /// <inheritdoc />
    public Result<PrivilegeState> Query(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        // Privilege LUIDs are assigned per machine, per boot. Hardcoding one would
        // work on the machine it was read from and quietly mean something else
        // everywhere afterwards.
        if (!PrivilegeNative.LookupPrivilegeValue(null, privilegeName, out PrivilegeNative.Luid wanted))
        {
            int error = Marshal.GetLastPInvokeError();

            return Result<PrivilegeState>.Failure(DmgError.Internal(
                error == NoSuchPrivilege
                    ? $"Windows does not know a privilege called '{privilegeName}'. This is a bug in dmg."
                    : $"dmg could not look up the '{privilegeName}' privilege.",
                $"LookupPrivilegeValueW returned error {error}."));
        }

        if (!PrivilegeNative.OpenProcessToken(
                PrivilegeNative.GetCurrentProcess(),
                PrivilegeNative.TokenQuery,
                out nint rawToken))
        {
            return Result<PrivilegeState>.Failure(DmgError.Internal(
                "dmg could not open its own process token to check for the privileges it needs.",
                $"OpenProcessToken returned error {Marshal.GetLastPInvokeError()}."));
        }

        // Wrapped before anything else can happen, so there is no window in which
        // the handle exists and nothing owns it.
        using SafeTokenHandle token = new(rawToken);

        Result<byte[]> block = ReadPrivilegeBlock(token);

        return block.TryGetValue(out byte[]? privileges)
            ? Result<PrivilegeState>.Success(
                TokenPrivileges.Find(privileges, wanted.LowPart, wanted.HighPart))
            : block.CastFailure<PrivilegeState>();
    }

    /// <summary>
    /// Reads the token's whole <c>TOKEN_PRIVILEGES</c> block, sizing the buffer from
    /// the API rather than guessing at it.
    /// </summary>
    private static Result<byte[]> ReadPrivilegeBlock(SafeTokenHandle token)
    {
        // The documented way to size this call is to make it fail: a zero-length
        // buffer returns ERROR_INSUFFICIENT_BUFFER and the length it wanted.
        if (PrivilegeNative.GetTokenInformation(
                token,
                PrivilegeNative.TokenPrivileges,
                Span<byte>.Empty,
                0,
                out int needed))
        {
            // Succeeding with no buffer would mean a token with no privilege block
            // at all, which is not a thing Windows produces.
            return Result<byte[]>.Failure(DmgError.Internal(
                "The process token reported no privilege information.",
                "GetTokenInformation succeeded against a zero-length buffer."));
        }

        int sizingError = Marshal.GetLastPInvokeError();

        if (sizingError != PrivilegeNative.InsufficientBuffer)
        {
            return Result<byte[]>.Failure(DmgError.Internal(
                "dmg could not read the privileges out of its own process token.",
                $"GetTokenInformation returned error {sizingError} while sizing the buffer."));
        }

        if (needed <= sizeof(uint) || needed > MaximumPrivilegeBlockBytes)
        {
            return Result<byte[]>.Failure(DmgError.Internal(
                "The process token reported an implausible privilege block size.",
                $"GetTokenInformation asked for {needed} bytes; the ceiling is "
                + $"{MaximumPrivilegeBlockBytes}."));
        }

        byte[] buffer = new byte[needed];

        if (!PrivilegeNative.GetTokenInformation(
                token,
                PrivilegeNative.TokenPrivileges,
                buffer,
                needed,
                out _))
        {
            return Result<byte[]>.Failure(DmgError.Internal(
                "dmg could not read the privileges out of its own process token.",
                $"GetTokenInformation returned error {Marshal.GetLastPInvokeError()}."));
        }

        return Result<byte[]>.Success(buffer);
    }
}
