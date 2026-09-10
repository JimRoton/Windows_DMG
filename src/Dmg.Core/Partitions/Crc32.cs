namespace Dmg.Core.Partitions;

/// <summary>
/// CRC-32 (the reflected IEEE 802.3 variant, polynomial <c>0xEDB88320</c>), which
/// is what the GPT header and its entry array are checksummed with.
/// </summary>
/// <remarks>
/// This is here rather than taken from a package because <c>src/</c> carries no
/// third-party dependencies, and because <c>System.IO.Hashing</c> - the only place
/// the BCL exposes CRC-32 - is a NuGet package rather than part of the shared
/// framework. Sixteen lines of table lookup is a smaller liability than a
/// dependency.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC-32 of <paramref name="data"/>.</summary>
    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
