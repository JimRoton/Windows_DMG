namespace Dmg.Core;

/// <summary>
/// The process exit codes <c>dmg.exe</c> returns, per the CLI design.
/// </summary>
/// <remarks>
/// <para>
/// These values are a public contract. Scripts branch on them, so a member's
/// numeric value must never change and a retired member must never be reused for
/// a different meaning. New failure kinds get new numbers appended.
/// </para>
/// <para>
/// The taxonomy is deliberately coarse. Its job is to let a caller decide what to
/// do next - retry elevated, free some disk, give up on the format - not to
/// enumerate every way a parse can go wrong. The specifics belong in
/// <see cref="DmgError.Message"/> and <see cref="DmgError.Detail"/>.
/// </para>
/// </remarks>
public enum DmgExitCode
{
    /// <summary>The operation completed.</summary>
    Success = 0,

    /// <summary>A bug in this tool: an unexpected exception or a broken invariant.</summary>
    InternalError = 1,

    /// <summary>The command line was wrong - unknown verb, missing or conflicting argument.</summary>
    UsageError = 2,

    /// <summary>
    /// The image is well-formed but this build cannot handle it: an unsupported
    /// container, a chunk codec outside the v1 scope, or a payload filesystem
    /// Windows has no driver for.
    /// </summary>
    UnsupportedFormat = 3,

    /// <summary>The passphrase was wrong, or the key material failed to unwrap.</summary>
    DecryptionFailed = 4,

    /// <summary>
    /// The payload was decoded, but Windows will not mount the filesystem inside it
    /// (HFS+ and APFS being the expected cases).
    /// </summary>
    FilesystemNotMountable = 5,

    /// <summary>Attaching the VHD or assigning a drive letter failed.</summary>
    MountFailed = 6,

    /// <summary>The operation needs an elevated shell and did not get one.</summary>
    ElevationRequired = 7,

    /// <summary>Not enough scratch space to materialise the decoded image.</summary>
    InsufficientSpace = 8,

    /// <summary>The image is malformed: bad magic, impossible offsets, or a failed checksum.</summary>
    CorruptImage = 9,
}

/// <summary>
/// A failure: what went wrong, in one line a user can act on, plus the exit code
/// the process should return.
/// </summary>
/// <param name="Code">The exit code this failure maps to. Never <see cref="DmgExitCode.Success"/>.</param>
/// <param name="Message">
/// A short, user-facing sentence. This is what gets printed. It should say what
/// failed and, where possible, what to do about it - not a stack trace.
/// </param>
/// <param name="Detail">
/// Optional extra context - a byte offset, a native error code, an inner exception
/// message. Shown only at <c>Verbosity.Verbose</c>, so it may be technical.
/// </param>
/// <remarks>
/// A <see cref="DmgError"/> whose <see cref="Code"/> is <see cref="DmgExitCode.Success"/>
/// is a contradiction and is rejected at construction: success is represented by a
/// successful <see cref="Result{T}"/>, never by an error object.
/// </remarks>
public sealed record DmgError(DmgExitCode Code, string Message, string? Detail = null)
{
    /// <summary>The failure's exit code. Guaranteed not to be <see cref="DmgExitCode.Success"/>.</summary>
    public DmgExitCode Code { get; } = Code != DmgExitCode.Success
        ? Code
        : throw new ArgumentOutOfRangeException(
            nameof(Code),
            Code,
            "DmgExitCode.Success cannot describe a failure. Use a successful Result instead.");

    /// <summary>The user-facing message. Never null or blank.</summary>
    public string Message { get; } = !string.IsNullOrWhiteSpace(Message)
        ? Message
        : throw new ArgumentException("A DmgError must carry a message.", nameof(Message));

    /// <summary>An internal error, for broken invariants and unexpected exceptions.</summary>
    public static DmgError Internal(string message, string? detail = null) =>
        new(DmgExitCode.InternalError, message, detail);

    /// <summary>A command-line usage error.</summary>
    public static DmgError Usage(string message, string? detail = null) =>
        new(DmgExitCode.UsageError, message, detail);

    /// <summary>A format this build knowingly does not support.</summary>
    public static DmgError Unsupported(string message, string? detail = null) =>
        new(DmgExitCode.UnsupportedFormat, message, detail);

    /// <summary>A malformed or self-inconsistent image.</summary>
    public static DmgError Corrupt(string message, string? detail = null) =>
        new(DmgExitCode.CorruptImage, message, detail);

    /// <summary>
    /// Renders the error the way the CLI prints it: <c>dmg: message</c>, with the
    /// detail appended in parentheses when there is one.
    /// </summary>
    public override string ToString() =>
        Detail is null ? Message : $"{Message} ({Detail})";
}
