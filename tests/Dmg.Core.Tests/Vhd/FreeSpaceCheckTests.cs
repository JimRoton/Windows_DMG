using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers the precheck that refuses a conversion before a single byte is written,
/// and the exit code 8 it produces.
/// </summary>
/// <remarks>
/// A real volume cannot be made to have exactly 41 bytes free on demand, so the
/// arithmetic is checked through a stub probe. What the real probe does is
/// checked separately, against the volume this suite happens to be running on.
/// </remarks>
public sealed class FreeSpaceCheckTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static readonly string SamplePath = Path.Combine(Path.GetTempPath(), "dmg-precheck", "sample.vhd");

    [Fact]
    public void SucceedsWhenTheVolumeHasRoomForTheFileAndTheMargin()
    {
        Result required = FreeSpaceCheck.Require(
            SamplePath,
            requiredBytes: 4 * Gigabyte,
            new StubProbe(4 * Gigabyte + FreeSpaceCheck.DefaultMarginBytes));

        Assert.True(required.Ok);
    }

    [Fact]
    public void FailsWithExitCodeEightWhenItIsOneByteShort()
    {
        Result required = FreeSpaceCheck.Require(
            SamplePath,
            requiredBytes: 4 * Gigabyte,
            new StubProbe(4 * Gigabyte + FreeSpaceCheck.DefaultMarginBytes - 1));

        Assert.False(required.Ok);
        Assert.Equal(DmgExitCode.InsufficientSpace, required.Error.Code);
        Assert.Equal(8, (int)required.Error.Code);
    }

    [Fact]
    public void ReportsBothTheRequiredAndTheAvailableFigures()
    {
        Result required = FreeSpaceCheck.Require(
            SamplePath,
            requiredBytes: 40 * Gigabyte,
            new StubProbe(12 * Gigabyte),
            marginBytes: 0);

        Assert.False(required.Ok);

        // The message a user sees carries both numbers in units they can compare
        // with what the file manager told them.
        Assert.Contains("40 GiB", required.Error.Message, StringComparison.Ordinal);
        Assert.Contains("12 GiB", required.Error.Message, StringComparison.Ordinal);

        // The verbose detail carries the exact byte counts and the shortfall.
        string detail = Assert.IsType<string>(required.Error.Detail);

        Assert.Contains((40 * Gigabyte).ToString(), detail, StringComparison.Ordinal);
        Assert.Contains((12 * Gigabyte).ToString(), detail, StringComparison.Ordinal);
        Assert.Contains((28 * Gigabyte).ToString(), detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsTheFooterAndTheSectorPaddingAsPartOfWhatIsRequired()
    {
        // A payload one byte over a sector needs the next whole sector plus a
        // footer, and the precheck must ask for all of it.
        long payload = 512 + 1;
        long fileSize = VhdWriter.FixedFileSizeFor(payload);

        Assert.Equal(1024 + 512, fileSize);

        Result exact = FreeSpaceCheck.Require(SamplePath, fileSize, new StubProbe(fileSize), marginBytes: 0);
        Result short1 = FreeSpaceCheck.Require(SamplePath, fileSize, new StubProbe(fileSize - 1), marginBytes: 0);

        Assert.True(exact.Ok);
        Assert.False(short1.Ok);
    }

    [Fact]
    public void InsistsOnAMarginSoAConversionDoesNotFillTheVolumeToZero()
    {
        long fileSize = 8 * Gigabyte;

        Result exact = FreeSpaceCheck.Require(SamplePath, fileSize, new StubProbe(fileSize));

        Assert.False(exact.Ok);
        Assert.Equal(DmgExitCode.InsufficientSpace, exact.Error.Code);
        Assert.Equal(64L * 1024 * 1024, FreeSpaceCheck.DefaultMarginBytes);
    }

    [Fact]
    public void DoesNotBlockTheConversionWhenTheVolumeCannotBeMeasured()
    {
        Result required = FreeSpaceCheck.Require(
            SamplePath,
            requiredBytes: 40 * Gigabyte,
            new FailingProbe());

        Assert.True(required.Ok, "An unmeasurable volume is not the same as a full one.");
    }

    [Fact]
    public void MeasureStillReportsTheFiguresWhenThereIsNotEnoughRoom()
    {
        Result<FreeSpaceReport> measured = FreeSpaceCheck.Measure(
            SamplePath,
            requiredBytes: 10 * Gigabyte,
            new StubProbe(Gigabyte),
            marginBytes: 0);

        Assert.True(measured.TryGetValue(out FreeSpaceReport? report), "A short volume is measurable.");
        Assert.False(report.HasRoom);
        Assert.Equal(10 * Gigabyte, report.TotalRequiredBytes);
        Assert.Equal(Gigabyte, report.AvailableBytes);
        Assert.Equal(9 * Gigabyte, report.ShortfallBytes);
    }

    [Fact]
    public void MeasurePropagatesAProbeFailure()
    {
        Result<FreeSpaceReport> measured = FreeSpaceCheck.Measure(SamplePath, Gigabyte, new FailingProbe());

        Assert.False(measured.Ok);
        Assert.Equal(DmgExitCode.InternalError, measured.Error.Code);
    }

    [Fact]
    public void AReportWithRoomToSpareRefusesToDescribeAFailure()
    {
        FreeSpaceReport report = new(SamplePath, 512, 0, 4096);

        Assert.True(report.HasRoom);
        Assert.Equal(0, report.ShortfallBytes);
        Assert.Throws<InvalidOperationException>(report.ToError);
    }

    [Fact]
    public void TheRealProbeAnswersForTheVolumeThisTestIsRunningOn()
    {
        // Portable on purpose: the same DriveInfo lookup answers on the Mac this is
        // developed on and on the Windows box it ships to.
        Result<long> available = DriveFreeSpaceProbe.Instance.AvailableBytes(Path.GetTempPath());

        Assert.True(available.TryGetValue(out long bytes), "The temporary directory's volume should be measurable.");
        Assert.True(bytes > 0, "A volume running a test suite has some room on it.");
    }

    [Fact]
    public void TheRealProbeAnswersForAPathThatDoesNotExistYet()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "not-created-yet.vhd");

        Result<long> available = DriveFreeSpaceProbe.Instance.AvailableBytes(missing);

        Assert.True(available.Ok, "The precheck runs before the scratch directory exists.");
    }

    /// <summary>Reports whatever free-space figure a test needs it to.</summary>
    private sealed class StubProbe(long availableBytes) : IFreeSpaceProbe
    {
        public Result<long> AvailableBytes(string path) => Result<long>.Success(availableBytes);
    }

    /// <summary>A volume that cannot be interrogated at all.</summary>
    private sealed class FailingProbe : IFreeSpaceProbe
    {
        public Result<long> AvailableBytes(string path) =>
            Result<long>.Failure(DmgError.Internal("No mounted volume holds the scratch path.", path));
    }
}
