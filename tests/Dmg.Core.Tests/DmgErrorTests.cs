namespace Dmg.Core.Tests;

public sealed class DmgErrorTests
{
    [Fact]
    public void CarriesCodeMessageAndDetail()
    {
        DmgError error = new(DmgExitCode.CorruptImage, "koly trailer is not where it should be.", "offset 0x1F40");

        Assert.Equal(DmgExitCode.CorruptImage, error.Code);
        Assert.Equal("koly trailer is not where it should be.", error.Message);
        Assert.Equal("offset 0x1F40", error.Detail);
    }

    [Fact]
    public void DetailIsOptional()
    {
        DmgError error = new(DmgExitCode.UsageError, "Unknown verb 'moutn'.");

        Assert.Null(error.Detail);
    }

    [Fact]
    public void SuccessIsNotAValidErrorCode()
    {
        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DmgError(DmgExitCode.Success, "this is not a failure"));

        Assert.Equal("Code", thrown.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MessageMustNotBeBlank(string message)
    {
        Assert.Throws<ArgumentException>(() => new DmgError(DmgExitCode.InternalError, message));
    }

    [Fact]
    public void RecordEqualityComparesAllThreeComponents()
    {
        DmgError first = new(DmgExitCode.MountFailed, "AttachVirtualDisk failed.", "0x80070005");
        DmgError same = new(DmgExitCode.MountFailed, "AttachVirtualDisk failed.", "0x80070005");
        DmgError differentDetail = new(DmgExitCode.MountFailed, "AttachVirtualDisk failed.", "0x80070020");

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, differentDetail);
    }

    [Fact]
    public void ToStringAppendsTheDetailWhenThereIsOne()
    {
        Assert.Equal(
            "Decryption failed. (wrong passphrase)",
            new DmgError(DmgExitCode.DecryptionFailed, "Decryption failed.", "wrong passphrase").ToString());

        Assert.Equal(
            "Decryption failed.",
            new DmgError(DmgExitCode.DecryptionFailed, "Decryption failed.").ToString());
    }

    [Theory]
    [InlineData(DmgExitCode.Success, 0)]
    [InlineData(DmgExitCode.InternalError, 1)]
    [InlineData(DmgExitCode.UsageError, 2)]
    [InlineData(DmgExitCode.UnsupportedFormat, 3)]
    [InlineData(DmgExitCode.DecryptionFailed, 4)]
    [InlineData(DmgExitCode.FilesystemNotMountable, 5)]
    [InlineData(DmgExitCode.MountFailed, 6)]
    [InlineData(DmgExitCode.ElevationRequired, 7)]
    [InlineData(DmgExitCode.InsufficientSpace, 8)]
    [InlineData(DmgExitCode.CorruptImage, 9)]
    public void ExitCodesHaveTheirContractedNumericValues(DmgExitCode code, int expected)
    {
        // These numbers are a published contract - scripts branch on them.
        Assert.Equal(expected, (int)code);
    }

    [Fact]
    public void TheTaxonomyHasExactlyTenMembersAndNoGaps()
    {
        int[] values = Enum.GetValues<DmgExitCode>().Select(code => (int)code).Order().ToArray();

        Assert.Equal(Enumerable.Range(0, 10).ToArray(), values);
    }

    [Fact]
    public void ConvenienceFactoriesPickTheRightCode()
    {
        Assert.Equal(DmgExitCode.InternalError, DmgError.Internal("x").Code);
        Assert.Equal(DmgExitCode.UsageError, DmgError.Usage("x").Code);
        Assert.Equal(DmgExitCode.UnsupportedFormat, DmgError.Unsupported("x").Code);
        Assert.Equal(DmgExitCode.CorruptImage, DmgError.Corrupt("x").Code);
    }
}
