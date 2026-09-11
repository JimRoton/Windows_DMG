using Dmg.Core;

namespace Dmg.Windows.Volumes;

/// <summary>
/// The one place that decides what a drive letter is and what it looks like.
/// </summary>
/// <remarks>
/// <para>
/// Windows hands the same letter back in a different shape depending on which API
/// was asked. <c>GetVolumePathNamesForVolumeName</c> returns <c>E:\</c>,
/// <c>SetVolumeMountPoint</c> insists on <c>E:\</c>, <c>QueryDosDevice</c> wants
/// <c>E:</c>, a user types <c>e</c> or <c>E:</c>, and the mount registry stores
/// <c>E</c>. Left to itself that difference turns into <c>dmg unmount E:</c>
/// failing to find a mount recorded as <c>E</c>, which is a bug nobody can see by
/// reading either side on its own.
/// </para>
/// <para>
/// So there is one canonical form - a single upper-case letter - and every other
/// shape is produced from it by a named method here. Anything that is not a drive
/// letter comes back as null rather than as a guess; a wrong letter unmounts
/// somebody else's disk.
/// </para>
/// </remarks>
public static class DriveLetter
{
    /// <summary>The first letter Windows will assign. A: and B: are historically floppies.</summary>
    public const char First = 'A';

    /// <summary>The last letter there is.</summary>
    public const char Last = 'Z';

    /// <summary>
    /// Reduces any of the ways a drive letter gets written to the canonical single
    /// upper-case letter, or returns null when it is not a drive letter at all.
    /// </summary>
    /// <param name="value">
    /// <c>E</c>, <c>e</c>, <c>E:</c>, <c>E:\</c>, <c>E:/</c>, with or without
    /// surrounding whitespace - or null, or something else entirely.
    /// </param>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim().TrimEnd('\\', '/');

        if (trimmed.EndsWith(':'))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Length != 1)
        {
            return null;
        }

        char letter = char.ToUpperInvariant(trimmed[0]);

        return letter is >= First and <= Last ? letter.ToString() : null;
    }

    /// <summary>True when <paramref name="value"/> names a drive letter in any shape.</summary>
    public static bool IsDriveLetter(string? value) => Normalise(value) is not null;

    /// <summary>
    /// The command-line reading of <c>--letter</c>: the canonical letter, or a
    /// usage error saying what was wrong with what the user typed.
    /// </summary>
    /// <remarks>
    /// <see cref="DmgExitCode.UsageError"/> rather than a mount failure, because
    /// nothing has been attempted yet - the command line itself is wrong, and this
    /// check runs before any work so that a two-gigabyte decode is not thrown away
    /// over a typo.
    /// </remarks>
    public static Result<string> Parse(string? value)
    {
        string? normalised = Normalise(value);

        if (normalised is not null)
        {
            return Result<string>.Success(normalised);
        }

        return Result<string>.Failure(DmgError.Usage(
            $"'{value}' is not a drive letter. Pass a single letter A-Z, with or without a colon, "
            + "for example --letter X: or --letter x.",
            value is null ? "no value given" : $"--letter {value}"));
    }

    /// <summary>The letter as an API that wants a device name spells it: <c>E:</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="letter"/> is not canonical.</exception>
    public static string WithColon(string letter) => $"{Require(letter)}:";

    /// <summary>
    /// The letter as an API that wants a directory spells it: <c>E:\</c>. This is
    /// the form <c>SetVolumeMountPoint</c> requires and silently misbehaves without.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="letter"/> is not canonical.</exception>
    public static string Root(string letter) => $@"{Require(letter)}:\";

    /// <summary>
    /// The letter's place in the 26-bit mask <c>GetLogicalDrives</c> returns:
    /// 0 for A, 25 for Z.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="letter"/> is not canonical.</exception>
    public static int BitPosition(string letter) => Require(letter)[0] - First;

    private static string Require(string letter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(letter);

        return Normalise(letter) == letter
            ? letter
            : throw new ArgumentException(
                $"'{letter}' is not a canonical drive letter. Pass the result of {nameof(Normalise)}.",
                nameof(letter));
    }
}
