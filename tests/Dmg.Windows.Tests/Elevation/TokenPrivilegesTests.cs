using System.Buffers.Binary;
using Dmg.Windows.Elevation;

namespace Dmg.Windows.Tests.Elevation;

/// <summary>
/// The bytes <c>GetTokenInformation</c> hands back, built by hand.
/// </summary>
/// <remarks>
/// This is the only real logic in the elevation path, and on a Mac it is also the
/// only part that can be run at all - which is exactly why it was separated from
/// the P/Invoke. The cases below include ones a real token would never produce; a
/// bound that is only tested with well-formed input is not a bound.
/// </remarks>
public sealed class TokenPrivilegesTests
{
    private const uint ManageVolumeLow = 0x0000_0021;
    private const int ManageVolumeHigh = 0;

    [Fact]
    public void AnEnabledPrivilegeIsFound()
    {
        byte[] block = Block((ManageVolumeLow, ManageVolumeHigh, TokenPrivileges.Enabled));

        Assert.Equal(
            PrivilegeState.Enabled,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void APresentButSwitchedOffPrivilegeIsDisabledRatherThanAbsent()
    {
        // The normal state of a privilege in an elevated token, and the case the
        // whole three-state enum exists for.
        byte[] block = Block((ManageVolumeLow, ManageVolumeHigh, 0));

        Assert.Equal(
            PrivilegeState.Disabled,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void EnabledByDefaultCountsAsEnabledOnlyWhenTheEnabledBitIsAlsoSet()
    {
        byte[] onlyByDefault = Block((ManageVolumeLow, ManageVolumeHigh, TokenPrivileges.EnabledByDefault));
        byte[] both = Block((
            ManageVolumeLow,
            ManageVolumeHigh,
            TokenPrivileges.EnabledByDefault | TokenPrivileges.Enabled));

        Assert.Equal(
            PrivilegeState.Disabled,
            TokenPrivileges.Find(onlyByDefault, ManageVolumeLow, ManageVolumeHigh));
        Assert.Equal(
            PrivilegeState.Enabled,
            TokenPrivileges.Find(both, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void ARemovedPrivilegeIsAbsentHoweverItIsFlagged()
    {
        // SE_PRIVILEGE_REMOVED is permanent for the life of the token. Listing it as
        // held would be true to the bytes and useless to the caller.
        byte[] block = Block((
            ManageVolumeLow,
            ManageVolumeHigh,
            TokenPrivileges.Removed | TokenPrivileges.Enabled));

        Assert.Equal(
            PrivilegeState.Absent,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void APrivilegeThatIsNotThereIsAbsent()
    {
        byte[] block = Block((0x11, 0, TokenPrivileges.Enabled), (0x12, 0, TokenPrivileges.Enabled));

        Assert.Equal(
            PrivilegeState.Absent,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void TheHighHalfOfTheLuidIsPartOfTheComparison()
    {
        // LUIDs are 64 bits. Comparing only the low half would match the wrong
        // privilege on any machine that ever handed out a high part.
        byte[] block = Block((ManageVolumeLow, 1, TokenPrivileges.Enabled));

        Assert.Equal(PrivilegeState.Absent, TokenPrivileges.Find(block, ManageVolumeLow, 0));
        Assert.Equal(PrivilegeState.Enabled, TokenPrivileges.Find(block, ManageVolumeLow, 1));
    }

    [Fact]
    public void TheWantedPrivilegeIsFoundWhereverItSits()
    {
        byte[] block = Block(
            (0x01, 0, TokenPrivileges.Enabled),
            (0x02, 0, 0),
            (ManageVolumeLow, ManageVolumeHigh, TokenPrivileges.Enabled),
            (0x03, 0, 0));

        Assert.Equal(
            PrivilegeState.Enabled,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void ADeclaredCountLargerThanTheBufferIsClampedRatherThanTrusted()
    {
        // The declared count and the buffer come from the same API and should agree.
        // "Should agree" is not a bound, and reading past the buffer on the strength
        // of a number in it is how this sort of code goes wrong.
        byte[] block = Block((ManageVolumeLow, ManageVolumeHigh, TokenPrivileges.Enabled));
        BinaryPrimitives.WriteUInt32LittleEndian(block, uint.MaxValue);

        Assert.Equal(1, TokenPrivileges.CountIn(block));
        Assert.Equal(
            PrivilegeState.Enabled,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void ATrailingPartialEntryIsIgnored()
    {
        byte[] whole = Block(
            (0x01, 0, TokenPrivileges.Enabled),
            (ManageVolumeLow, ManageVolumeHigh, TokenPrivileges.Enabled));
        byte[] truncated = whole[..(whole.Length - 5)];

        Assert.Equal(1, TokenPrivileges.CountIn(truncated));
        Assert.Equal(
            PrivilegeState.Absent,
            TokenPrivileges.Find(truncated, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void ATokenWithNoPrivilegesIsAbsentNotAnError()
    {
        byte[] block = Block();

        Assert.Equal(0, TokenPrivileges.CountIn(block));
        Assert.Equal(
            PrivilegeState.Absent,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ABlockTooShortToHoldTheCountIsEmpty(int length)
    {
        byte[] block = new byte[length];

        Assert.Equal(0, TokenPrivileges.CountIn(block));
        Assert.Equal(
            PrivilegeState.Absent,
            TokenPrivileges.Find(block, ManageVolumeLow, ManageVolumeHigh));
    }

    [Fact]
    public void TheLayoutConstantsMatchTheWindowsStructures()
    {
        // If these ever drift, every offset above is wrong by a multiple of four and
        // the failures would be baffling.
        Assert.Equal(4, TokenPrivileges.CountSize);
        Assert.Equal(12, TokenPrivileges.EntrySize);
        Assert.Equal(0x1u, TokenPrivileges.EnabledByDefault);
        Assert.Equal(0x2u, TokenPrivileges.Enabled);
        Assert.Equal(0x4u, TokenPrivileges.Removed);
    }

    /// <summary>
    /// Builds a <c>TOKEN_PRIVILEGES</c> block: a little-endian count followed by
    /// twelve bytes per entry, exactly as Windows lays it out.
    /// </summary>
    private static byte[] Block(params (uint Low, int High, uint Attributes)[] entries)
    {
        byte[] block = new byte[TokenPrivileges.CountSize + (entries.Length * TokenPrivileges.EntrySize)];

        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)entries.Length);

        for (int index = 0; index < entries.Length; index++)
        {
            int offset = TokenPrivileges.CountSize + (index * TokenPrivileges.EntrySize);

            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset), entries[index].Low);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset + 4), entries[index].High);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 8), entries[index].Attributes);
        }

        return block;
    }
}
