using System.Buffers.Binary;

namespace Dmg.Windows.Elevation;

/// <summary>
/// Reads a <c>TOKEN_PRIVILEGES</c> block: the one piece of real logic in the
/// elevation check, kept where it can be tested without a token.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the P/Invoke for the same reason
/// <see cref="Dmg.Windows.VirtualDisk.VirtualDiskErrors"/> is separated from
/// <c>virtdisk.dll</c>: getting the bytes needs Windows, understanding them does
/// not. A block of bytes can be built by hand on any machine, including the awkward
/// ones - a count that disagrees with the buffer, a privilege marked removed, an
/// empty token - and those are exactly the cases that would otherwise never be
/// exercised until something went wrong on a user's machine.
/// </para>
/// <para>
/// <b>Layout.</b> <c>TOKEN_PRIVILEGES</c> is a <c>DWORD</c> count followed by that
/// many <c>LUID_AND_ATTRIBUTES</c>, each of which is a <c>DWORD</c> low part, a
/// <c>LONG</c> high part and a <c>DWORD</c> of attributes. Everything in it is
/// four-byte aligned, so the array starts at offset 4 with no padding on any
/// architecture Windows runs on, and the whole thing is little-endian because
/// Windows is.
/// </para>
/// <para>
/// Nothing here trusts the declared count. It arrives from the same call that sized
/// the buffer, so it should always fit, but a length that is only checked when it
/// is convenient is not a check.
/// </para>
/// </remarks>
public static class TokenPrivileges
{
    /// <summary>The <c>DWORD</c> count at the front of the block.</summary>
    public const int CountSize = sizeof(uint);

    /// <summary>The size of one <c>LUID_AND_ATTRIBUTES</c>: 4 + 4 + 4.</summary>
    public const int EntrySize = 12;

    /// <summary><c>SE_PRIVILEGE_ENABLED_BY_DEFAULT</c>.</summary>
    public const uint EnabledByDefault = 0x0000_0001;

    /// <summary><c>SE_PRIVILEGE_ENABLED</c>.</summary>
    public const uint Enabled = 0x0000_0002;

    /// <summary>
    /// <c>SE_PRIVILEGE_REMOVED</c>. A privilege stripped from the token for the rest
    /// of its life - still listed, but never enableable again.
    /// </summary>
    public const uint Removed = 0x0000_0004;

    /// <summary>
    /// How many entries the block declares, clamped to how many it can actually
    /// hold.
    /// </summary>
    /// <param name="block">The bytes <c>GetTokenInformation</c> returned.</param>
    public static int CountIn(ReadOnlySpan<byte> block)
    {
        if (block.Length < CountSize)
        {
            return 0;
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(block);
        int available = (block.Length - CountSize) / EntrySize;

        return (int)Math.Min(declared, (uint)Math.Max(available, 0));
    }

    /// <summary>
    /// Finds one privilege by LUID and says what state it is in.
    /// </summary>
    /// <param name="block">The bytes <c>GetTokenInformation</c> returned.</param>
    /// <param name="luidLow">The low half of the LUID <c>LookupPrivilegeValue</c> gave.</param>
    /// <param name="luidHigh">The high half.</param>
    /// <returns>
    /// <see cref="PrivilegeState.Enabled"/> or <see cref="PrivilegeState.Disabled"/>
    /// when the privilege is in the token, and <see cref="PrivilegeState.Absent"/>
    /// when it is not there - or is there and marked removed, which comes to the
    /// same thing for anyone hoping to use it.
    /// </returns>
    public static PrivilegeState Find(ReadOnlySpan<byte> block, uint luidLow, int luidHigh)
    {
        int count = CountIn(block);

        for (int index = 0; index < count; index++)
        {
            int offset = CountSize + (index * EntrySize);

            if (BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]) != luidLow
                || BinaryPrimitives.ReadInt32LittleEndian(block[(offset + 4)..]) != luidHigh)
            {
                continue;
            }

            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(block[(offset + 8)..]);

            if ((attributes & Removed) != 0)
            {
                return PrivilegeState.Absent;
            }

            return (attributes & Enabled) != 0 ? PrivilegeState.Enabled : PrivilegeState.Disabled;
        }

        return PrivilegeState.Absent;
    }
}
