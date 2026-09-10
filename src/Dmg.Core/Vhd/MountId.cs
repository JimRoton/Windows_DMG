using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Dmg.Core.Vhd;

/// <summary>
/// The identifier for one mount: the name of its scratch directory, the key its
/// VHD is filed under, and the handle <c>dmg unmount</c> is given.
/// </summary>
/// <remarks>
/// <para>
/// <b>We generate these. Nothing in the image ever names a file.</b> A DMG carries
/// a volume name, and that volume name is attacker-controlled: it can be
/// <c>..\..\Windows\System32</c>, a reserved device name like <c>CON</c>, a string
/// of trailing dots, or eight hundred characters of Unicode. Deriving a path from
/// it is the classic way an unpacking tool writes outside the directory it meant
/// to. So the only thing a scratch path is ever built from is a value of this
/// type, and the only way to get one is to generate it or to parse a string that
/// already looks exactly like a generated one.
/// </para>
/// <para>
/// The value is 32 lowercase hexadecimal characters over 128 bits of
/// cryptographically strong randomness. Hexadecimal because it is case-insensitive
/// in effect - Windows paths are - and because it contains no separator, no dot,
/// no space and no character any filesystem treats specially. 128 bits because
/// collisions must not happen between concurrent mounts, and a counter or a
/// process id would collide across runs.
/// </para>
/// </remarks>
public readonly record struct MountId
{
    /// <summary>The number of characters in a mount id.</summary>
    public const int Length = 32;

    private const int RandomByteCount = Length / 2;

    private readonly string? _value;

    private MountId(string value) => _value = value;

    /// <summary>
    /// The identifier's text: 32 lowercase hexadecimal characters, safe to use as
    /// one path segment on every filesystem this tool runs on.
    /// </summary>
    /// <remarks>
    /// A <c>default(MountId)</c> - which no factory here produces - reads as the
    /// empty string rather than null, so a forgotten assignment fails a path check
    /// instead of throwing somewhere further along.
    /// </remarks>
    public string Value => _value ?? string.Empty;

    /// <summary>True unless this is a <c>default(MountId)</c>.</summary>
    public bool IsValid => _value is not null;

    /// <summary>Generates a fresh identifier from 128 bits of strong randomness.</summary>
    public static MountId New()
    {
        Span<byte> random = stackalloc byte[RandomByteCount];
        RandomNumberGenerator.Fill(random);

        return new MountId(Convert.ToHexStringLower(random));
    }

    /// <summary>
    /// Accepts a string only if it is exactly what <see cref="New"/> produces:
    /// 32 lowercase hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// This is the gate on the way back in - for <c>dmg unmount &lt;id&gt;</c> and
    /// for reading the names of directories already in the scratch root. Anything
    /// that is not a generated id is refused outright rather than sanitised;
    /// sanitising is how <c>....//</c> becomes <c>../</c>.
    /// </remarks>
    public static bool TryParse(string? text, out MountId id)
    {
        id = default;

        if (text is not { Length: Length })
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        id = new MountId(text);
        return true;
    }

    /// <summary>
    /// Parses a mount id, returning the failure a caller should propagate when the
    /// text is not one.
    /// </summary>
    public static Result<MountId> Parse(string? text) =>
        TryParse(text, out MountId id)
            ? Result<MountId>.Success(id)
            : Result<MountId>.Failure(DmgError.Usage(
                "That is not a mount id.",
                $"expected {Length} hexadecimal characters, got '{Describe(text)}'"));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Renders a rejected string for an error message without letting it run to
    /// hundreds of characters or carry control codes into the console.
    /// </summary>
    private static string Describe([NotNullWhen(true)] string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        Span<char> safe = stackalloc char[Math.Min(text.Length, 64)];

        for (int index = 0; index < safe.Length; index++)
        {
            char character = text[index];
            safe[index] = character is >= ' ' and <= '~' ? character : '?';
        }

        return new string(safe);
    }
}
