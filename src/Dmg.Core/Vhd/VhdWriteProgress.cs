namespace Dmg.Core.Vhd;

/// <summary>
/// How far through writing a VHD the writer has got: bytes laid down, and how
/// many there will be in total.
/// </summary>
/// <param name="BytesWritten">
/// Bytes written to the destination so far, footer included once it is on disk.
/// Never negative, never greater than <paramref name="TotalBytes"/>.
/// </param>
/// <param name="TotalBytes">
/// The size the finished file will be: the payload rounded up to a whole number
/// of 512-byte sectors, plus the 512-byte footer. Known before the first byte is
/// written, which is what makes a percentage meaningful.
/// </param>
/// <remarks>
/// <para>
/// A struct, and deliberately tiny: the writer reports once per buffer, so on a
/// multi-gigabyte image this is constructed thousands of times and must not put
/// pressure on the heap.
/// </para>
/// <para>
/// The total counts the footer, so the final report is exactly
/// <c>BytesWritten == TotalBytes</c> and a progress bar reaches its end rather
/// than stopping 512 bytes short.
/// </para>
/// </remarks>
public readonly record struct VhdWriteProgress(long BytesWritten, long TotalBytes)
{
    /// <summary>How much of the file is written, from 0 to 1.</summary>
    /// <remarks>Zero when the total is not positive, rather than a division by zero.</remarks>
    public double Fraction => TotalBytes > 0
        ? (double)BytesWritten / TotalBytes
        : 0d;

    /// <summary>The same thing as a percentage, from 0 to 100.</summary>
    public double Percentage => Fraction * 100d;

    /// <summary>True once every byte, footer included, has been written.</summary>
    public bool IsComplete => TotalBytes > 0 && BytesWritten >= TotalBytes;

    /// <summary>Renders the progress the way a verbose log line prints it.</summary>
    public override string ToString() =>
        $"{BytesWritten}/{TotalBytes} bytes ({Percentage:F1}%)";
}
