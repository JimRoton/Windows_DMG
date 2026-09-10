using Dmg.Core;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.Mounts;

/// <summary>
/// A mount record is the only thing that remembers which drive letter came from
/// which image. These tests hold it to two promises that pull in opposite
/// directions: what this code creates is always well formed, and what it reads back
/// off disk is never trusted.
/// </summary>
public sealed class MountRecordTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateProducesARecordWithEverythingTheRegistryNeeds()
    {
        MountRecord record = Created();

        Assert.Equal(@"C:\images\ubuntu.dmg", record.SourcePath);
        Assert.Equal(@"C:\scratch\ubuntu.vhd", record.VhdPath);
        Assert.Equal("E", record.DriveLetter);
        Assert.Equal(MountMode.ReadOnly, record.Mode);
        Assert.Equal(Noon, record.MountedAtUtc);
        Assert.True(record.Validate().Ok);
    }

    [Fact]
    public void AGeneratedIdIsEightHexCharacters()
    {
        string id = Created().Id;

        Assert.Equal(MountRecord.IdLength, id.Length);
        Assert.All(id, character => Assert.True(
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f',
            $"'{character}' is not a lower-case hex digit."));
    }

    [Fact]
    public void TwoRecordsMadeInARowDoNotShareAnId()
    {
        Assert.NotEqual(Created().Id, Created().Id);
    }

    [Theory]
    [InlineData("E")]
    [InlineData("e")]
    [InlineData("E:")]
    [InlineData("e:")]
    [InlineData(@"E:\")]
    [InlineData("E:/")]
    [InlineData("  e:  ")]
    public void EveryWayOfWritingADriveLetterStoresTheSameOneCharacter(string written)
    {
        // Windows itself is inconsistent about this - GetVolumePathNamesForVolumeName
        // returns "E:\", a user types "E:", an API wants "E". Normalising on the way
        // in is what stops `dmg unmount E:` failing to match a record that says "E".
        Assert.Equal("E", MountRecord.NormaliseDriveLetter(written));
    }

    [Theory]
    [InlineData("EE")]
    [InlineData("1")]
    [InlineData(":")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"E:\folder")]
    [InlineData(@"\\server\share")]
    public void AnythingThatIsNotADriveLetterIsRefusedRatherThanGuessedAt(string written)
    {
        Assert.Null(MountRecord.NormaliseDriveLetter(written));

        Result<MountRecord> result = MountRecord.Create(
            @"C:\images\ubuntu.dmg",
            @"C:\scratch\ubuntu.vhd",
            written,
            VirtualDiskAccessMode.ReadOnly);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void ALetterlessAttachIsRecordedAsHavingNoLetterRatherThanRefused()
    {
        // A disk attached with ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER is a real
        // state, not an error: S8.5 attaches that way on purpose before assigning
        // the letter itself, and the record has to survive the window in between.
        MountRecord record = Created(driveLetter: null);

        Assert.Null(record.DriveLetter);
        Assert.False(record.HasDriveLetter);
        Assert.Equal("-", record.DescribeDriveLetter());
        Assert.True(record.Validate().Ok);
    }

    [Fact]
    public void ADriveLetterIsPrintedWithItsColon()
    {
        Assert.Equal("E:", Created().DescribeDriveLetter());
    }

    [Theory]
    [InlineData(VirtualDiskAccessMode.ReadOnly, "ro")]
    [InlineData(VirtualDiskAccessMode.ReadWrite, "rw")]
    public void TheModeIsStoredAsATwoLetterStringAndReadsBackAsTheSameMode(
        VirtualDiskAccessMode mode,
        string expected)
    {
        MountRecord record = Created(mode: mode);

        Assert.Equal(expected, record.Mode);
        Assert.Equal(mode, record.AccessMode);
    }

    [Fact]
    public void ATimestampGivenInAnotherZoneIsStoredInUtc()
    {
        // The file is read on machines in other time zones and after the clock has
        // changed. Storing anything but UTC makes "which of these did I mount
        // first" answerable only by accident.
        DateTimeOffset local = new(2026, 9, 10, 7, 0, 0, TimeSpan.FromHours(-5));

        MountRecord record = Created(mountedAt: local);

        Assert.Equal(TimeSpan.Zero, record.MountedAtUtc.Offset);
        Assert.Equal(Noon, record.MountedAtUtc);
    }

    [Fact]
    public void CreateStampsTheCurrentTimeWhenItIsNotToldOne()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        MountRecord record = MountRecord.Create(
            @"C:\images\ubuntu.dmg",
            @"C:\scratch\ubuntu.vhd",
            "E",
            VirtualDiskAccessMode.ReadOnly).Value!;

        Assert.InRange(record.MountedAtUtc, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ARecordMadeFromNonsenseDoesNotThrowOnConstruction()
    {
        // The whole reason this type does not validate in its constructor. It is
        // materialised by the JSON deserializer from a file any text editor can
        // have got at, and a throw there would take down `dmg list` rather than
        // costing one line.
        MountRecord record = new(string.Empty, string.Empty, string.Empty, "9", "sideways", default);

        Assert.False(record.Validate().Ok);
    }

    /// <summary>Every field a hand-edited or half-written file can get wrong.</summary>
    /// <param name="broken">
    /// Which field to spoil. Named rather than passed as a record, because a
    /// <c>TheoryData</c> of records is not serializable and xUnit says so at build
    /// time.
    /// </param>
    /// <param name="expectedFragment">What the warning has to say, so the user can find the bad line.</param>
    [Theory]
    [InlineData("blank id", "no id")]
    [InlineData("no source", "no source image")]
    [InlineData("no vhd", "no VHD")]
    [InlineData("spelled-out mode", "neither 'ro' nor 'rw'")]
    [InlineData("empty mode", "neither 'ro' nor 'rw'")]
    [InlineData("letter with a colon", "not a drive letter")]
    [InlineData("lower-case letter", "not a drive letter")]
    [InlineData("no timestamp", "no timestamp")]
    public void ValidateNamesWhatIsWrongWithARecordReadOffDisk(string broken, string expectedFragment)
    {
        MountRecord record = Spoiled(broken);

        Result validated = record.Validate();

        Assert.False(validated.Ok);
        Assert.Equal(DmgExitCode.InternalError, validated.Error.Code);
        Assert.Contains(expectedFragment, validated.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MountRecord Spoiled(string broken) => broken switch
    {
        "blank id" => Valid() with { Id = "  " },
        "no source" => Valid() with { SourcePath = string.Empty },
        "no vhd" => Valid() with { VhdPath = string.Empty },
        "spelled-out mode" => Valid() with { Mode = "readonly" },
        "empty mode" => Valid() with { Mode = string.Empty },
        "letter with a colon" => Valid() with { DriveLetter = "E:" },
        "lower-case letter" => Valid() with { DriveLetter = "e" },
        "no timestamp" => Valid() with { MountedAtUtc = default },
        _ => throw new ArgumentOutOfRangeException(nameof(broken), broken, "No such case."),
    };

    [Fact]
    public void ARecordThatIsValidStaysValidWhenNothingIsChanged()
    {
        Assert.True(Valid().Validate().Ok);
    }

    private static MountRecord Valid() => new(
        "0123abcd",
        @"C:\images\ubuntu.dmg",
        @"C:\scratch\ubuntu.vhd",
        "E",
        MountMode.ReadOnly,
        Noon);

    private static MountRecord Created(
        string? driveLetter = "E",
        VirtualDiskAccessMode mode = VirtualDiskAccessMode.ReadOnly,
        DateTimeOffset? mountedAt = null)
    {
        Result<MountRecord> result = MountRecord.Create(
            @"C:\images\ubuntu.dmg",
            @"C:\scratch\ubuntu.vhd",
            driveLetter,
            mode,
            mountedAt ?? Noon);

        Assert.True(result.Ok, result.Ok ? string.Empty : result.Error.ToString());

        return result.Value!;
    }
}
