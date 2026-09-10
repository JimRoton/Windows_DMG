using System.Buffers.Binary;

namespace Dmg.Core.Vhd;

/// <summary>
/// The cylinder/head/sector geometry stored in the four bytes at offset 56 of a
/// VHD footer.
/// </summary>
/// <param name="Cylinders">Cylinder count, big-endian <c>ushort</c> at offset 56.</param>
/// <param name="Heads">Head count, one byte at offset 58.</param>
/// <param name="SectorsPerTrack">Sectors per track, one byte at offset 59.</param>
/// <remarks>
/// <para>
/// CHS is an anachronism - no physical disk has looked like this in thirty years -
/// but the VHD provider in Windows reads these three numbers, and a nonsense
/// combination is one of the ways a hand-rolled VHD gets rejected at attach time.
/// So the derivation in <see cref="ForSectorCount"/> follows the pseudo-code in
/// the "Virtual Hard Disk Image Format Specification" (Microsoft, October 2006,
/// Appendix: CHS Calculation) exactly, including its oddities.
/// </para>
/// <para>
/// CHS can address at most <c>65535 * 16 * 255</c> sectors - roughly 127 GB. Above
/// that the specification pins the geometry at the maximum and lets the size
/// fields carry the real capacity. That is a clamp, not an error, so
/// <see cref="ForSectorCount"/> cannot fail for an oversized disk.
/// </para>
/// <para>
/// At the other end the algorithm is equally literal: a disk of only a few
/// thousand sectors comes out with a very small - possibly zero - cylinder count,
/// because the integer division rounds down. That is what the specification says
/// to do. Enforcing a sensible minimum image size belongs to the layer that
/// decides what is worth writing, not here.
/// </para>
/// </remarks>
public readonly record struct VhdGeometry(ushort Cylinders, byte Heads, byte SectorsPerTrack)
{
    /// <summary>The number of bytes the packed geometry field occupies.</summary>
    public const int Length = 4;

    /// <summary>The largest sector count CHS can address: <c>65535 * 16 * 255</c>.</summary>
    public const long MaxAddressableSectors = 65535L * 16L * 255L;

    /// <summary>The number of sectors this geometry addresses: C * H * S.</summary>
    /// <remarks>
    /// Usually a little under the disk's real sector count, because the truncating
    /// integer division in the specification's algorithm throws away the remainder.
    /// That shortfall is expected and is exactly what Windows itself writes.
    /// </remarks>
    public long TotalSectors => (long)Cylinders * Heads * SectorsPerTrack;

    /// <summary>The capacity this geometry addresses, in bytes.</summary>
    public long AddressableBytes => TotalSectors * VhdFooter.SectorSize;

    /// <summary>
    /// Derives the geometry for a disk of <paramref name="totalSectors"/> 512-byte
    /// sectors, following the VHD specification's CHS algorithm.
    /// </summary>
    /// <param name="totalSectors">
    /// The disk's sector count. Must be positive. Values above
    /// <see cref="MaxAddressableSectors"/> are clamped, as the specification requires.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalSectors"/> is not positive.</exception>
    public static VhdGeometry ForSectorCount(long totalSectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSectors);

        if (totalSectors > MaxAddressableSectors)
        {
            totalSectors = MaxAddressableSectors;
        }

        long sectorsPerTrack;
        long heads;
        long cylinderTimesHeads;

        if (totalSectors >= 65535L * 16L * 63L)
        {
            sectorsPerTrack = 255;
            heads = 16;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;
        }
        else
        {
            sectorsPerTrack = 17;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;

            heads = (cylinderTimesHeads + 1023) / 1024;

            if (heads < 4)
            {
                heads = 4;
            }

            if (cylinderTimesHeads >= heads * 1024 || heads > 16)
            {
                sectorsPerTrack = 31;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }

            if (cylinderTimesHeads >= heads * 1024)
            {
                sectorsPerTrack = 63;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }
        }

        return new VhdGeometry(
            (ushort)(cylinderTimesHeads / heads),
            (byte)heads,
            (byte)sectorsPerTrack);
    }

    /// <summary>
    /// Derives the geometry for a disk of <paramref name="diskSizeInBytes"/> bytes,
    /// which must be a whole number of 512-byte sectors.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="diskSizeInBytes"/> is not a positive multiple of 512.
    /// </exception>
    public static VhdGeometry ForDiskSize(long diskSizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diskSizeInBytes);

        if (diskSizeInBytes % VhdFooter.SectorSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diskSizeInBytes),
                diskSizeInBytes,
                "A VHD disk size must be a whole number of 512-byte sectors.");
        }

        return ForSectorCount(diskSizeInBytes / VhdFooter.SectorSize);
    }

    /// <summary>Writes the four packed geometry bytes, big-endian, into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than four bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("The geometry field is four bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, Cylinders);
        destination[2] = Heads;
        destination[3] = SectorsPerTrack;
    }

    /// <summary>Reads the four packed geometry bytes, big-endian, from <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than four bytes.</exception>
    public static VhdGeometry ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Length)
        {
            throw new ArgumentException("The geometry field is four bytes.", nameof(source));
        }

        return new VhdGeometry(
            BinaryPrimitives.ReadUInt16BigEndian(source),
            source[2],
            source[3]);
    }

    /// <summary>Renders the geometry the way disk tools print it: <c>C/H/S</c>.</summary>
    public override string ToString() => $"{Cylinders}/{Heads}/{SectorsPerTrack}";
}
