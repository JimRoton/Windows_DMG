using System.Text.Json.Serialization;
using Dmg.Core;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Mounts;

/// <summary>
/// One line of the mount registry: what was mounted, where it went, and when.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type does not validate itself.</b> Every other record in this codebase
/// throws on nonsense in its constructor; this one must not, because it is
/// materialised from a file that any text editor, any crash, and any older version
/// of this tool can have got at. A constructor that threw would turn one bad entry
/// into an exception in the middle of <c>dmg list</c>, which is the opposite of
/// degrading gracefully. Instead the file is deserialized into whatever it holds
/// and <see cref="Validate"/> decides afterwards, one record at a time, so a single
/// bad line costs the user that line and nothing else.
/// </para>
/// <para>
/// <see cref="Create"/> is the way to make a record in code: it validates, it
/// normalises the drive letter, and it generates the id.
/// </para>
/// </remarks>
/// <param name="Id">
/// The short handle a user types at <c>dmg unmount</c>. Eight hex characters -
/// long enough not to collide among the handful of mounts anyone has at once, short
/// enough to retype from a screen.
/// </param>
/// <param name="SourcePath">The <c>.dmg</c> that was mounted.</param>
/// <param name="VhdPath">The scratch <c>.vhd</c> the decoded image was written to.</param>
/// <param name="DriveLetter">
/// The single letter Windows gave it, without a colon, or null when the disk was
/// attached with no letter.
/// </param>
/// <param name="Mode"><see cref="MountMode.ReadOnly"/> or <see cref="MountMode.ReadWrite"/>.</param>
/// <param name="MountedAtUtc">When the mount happened, in UTC.</param>
public sealed record MountRecord(
    string Id,
    string SourcePath,
    string VhdPath,
    string? DriveLetter,
    string Mode,
    DateTimeOffset MountedAtUtc)
{
    /// <summary>How many characters a generated <see cref="Id"/> has.</summary>
    public const int IdLength = 8;

    /// <summary>The access mode this record describes.</summary>
    [JsonIgnore]
    public VirtualDiskAccessMode AccessMode => MountMode.ToAccessMode(Mode);

    /// <summary>True when the disk was attached without a drive letter.</summary>
    [JsonIgnore]
    public bool HasDriveLetter => DriveLetter is not null;

    /// <summary>
    /// Builds a record for a mount that has just happened, validating as it goes.
    /// </summary>
    /// <param name="sourcePath">The image that was mounted.</param>
    /// <param name="vhdPath">The scratch VHD it was decoded to.</param>
    /// <param name="driveLetter">
    /// The letter Windows assigned, in any of the forms it comes in - <c>E</c>,
    /// <c>e</c>, <c>E:</c>, <c>E:\</c> - or null for a letterless attach.
    /// </param>
    /// <param name="mode">Read-only unless the user asked for <c>--rw</c>.</param>
    /// <param name="mountedAtUtc">When it happened. Defaults to now.</param>
    /// <param name="id">
    /// The id to use. Defaults to a fresh one; supplied only by a caller that has
    /// already checked it does not collide.
    /// </param>
    public static Result<MountRecord> Create(
        string sourcePath,
        string vhdPath,
        string? driveLetter,
        VirtualDiskAccessMode mode,
        DateTimeOffset? mountedAtUtc = null,
        string? id = null)
    {
        if (driveLetter is not null && NormaliseDriveLetter(driveLetter) is null)
        {
            return Result<MountRecord>.Failure(DmgError.Internal(
                $"'{driveLetter}' is not a drive letter.",
                "A mount record's drive letter must be a single letter A-Z, or absent."));
        }

        MountRecord record = new(
            id ?? NewId(),
            sourcePath,
            vhdPath,
            NormaliseDriveLetter(driveLetter),
            MountMode.From(mode),
            (mountedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());

        Result validated = record.Validate();

        return validated.Ok ? Result<MountRecord>.Success(record) : validated.CastFailure<MountRecord>();
    }

    /// <summary>
    /// Checks a record read from the file.
    /// </summary>
    /// <returns>
    /// Success, or an <see cref="DmgExitCode.InternalError"/> naming the first field
    /// that is wrong - which the registry turns into a warning and a dropped line
    /// rather than a failure of whatever the user was doing.
    /// </returns>
    public Result Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return Invalid("it has no id");
        }

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return Invalid("it names no source image");
        }

        if (string.IsNullOrWhiteSpace(VhdPath))
        {
            return Invalid("it names no VHD");
        }

        if (!MountMode.IsValid(Mode))
        {
            return Invalid(
                $"its mode is '{Mode}', which is neither '{MountMode.ReadOnly}' nor "
                + $"'{MountMode.ReadWrite}'");
        }

        if (DriveLetter is not null && NormaliseDriveLetter(DriveLetter) != DriveLetter)
        {
            return Invalid($"'{DriveLetter}' is not a drive letter");
        }

        if (MountedAtUtc == default)
        {
            return Invalid("it has no timestamp");
        }

        return Result.Success();
    }

    /// <summary>
    /// Reduces any of the ways a drive letter gets written to the one this file
    /// stores: a single upper-case letter. Returns null for anything else,
    /// including null itself.
    /// </summary>
    public static string? NormaliseDriveLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            return null;
        }

        string trimmed = driveLetter.Trim().TrimEnd('\\', '/');

        if (trimmed.EndsWith(':'))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Length != 1)
        {
            return null;
        }

        char letter = char.ToUpperInvariant(trimmed[0]);

        return letter is >= 'A' and <= 'Z' ? letter.ToString() : null;
    }

    /// <summary>A fresh id: eight lower-case hex characters.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..IdLength];

    /// <summary>The drive letter with a colon, for printing, or a dash when there is none.</summary>
    public string DescribeDriveLetter() => DriveLetter is null ? "-" : $"{DriveLetter}:";

    private Result Invalid(string why) =>
        Result.Failure(DmgError.Internal(
            $"A mount registry entry was ignored because {why}.",
            $"id='{Id}', source='{SourcePath}', vhd='{VhdPath}'."));
}
