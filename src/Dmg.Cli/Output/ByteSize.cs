using System.Globalization;

namespace Dmg.Cli.Output;

/// <summary>
/// Byte counts written the way a person reads them: <c>2.41 GiB</c>.
/// </summary>
/// <remarks>
/// <para>
/// Binary units, and spelled as binary units. A disk image's sizes come from sector
/// counts, which are powers of two all the way down, so calling 2 147 483 648 bytes
/// "2.15 GB" would be arithmetically defensible and would not match a single other
/// number on the screen. GiB it is, and the exact byte count goes alongside wherever
/// someone might want to check it.
/// </para>
/// <para>
/// Invariant culture throughout - the build sets <c>InvariantGlobalization</c>, so
/// there is no other culture to use, and output a script may parse should not
/// change shape with a machine's locale anyway.
/// </para>
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["bytes", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    /// <summary>
    /// <paramref name="bytes"/> as a short human-readable size.
    /// </summary>
    /// <param name="bytes">A byte count.</param>
    /// <returns>
    /// <c>512 bytes</c>, <c>1.5 KiB</c>, <c>2.41 GiB</c>. Whole bytes below a
    /// kibibyte, because "0.50 KiB" is a worse way of saying 512.
    /// </returns>
    public static string Format(ulong bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} {Units[0]}");
        }

        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Two decimals below 10, one above: the second digit of "941.3 MiB" carries
        // information and the second digit of "941.32 MiB" does not.
        return value < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0.00} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }

    /// <summary>The same, for a signed count. Negative is a bug, so it is clamped.</summary>
    /// <param name="bytes">A byte count.</param>
    public static string Format(long bytes) => Format(bytes < 0 ? 0UL : (ulong)bytes);

    /// <summary>A count with thousands separators: <c>5,033,164</c>.</summary>
    /// <param name="count">The number.</param>
    public static string Count(ulong count) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0}");

    /// <summary>A count with thousands separators.</summary>
    /// <param name="count">The number.</param>
    public static string Count(int count) => Count((ulong)Math.Max(0, count));
}
