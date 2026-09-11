using Dmg.Windows.Volumes;

namespace Dmg.Windows.Tests.Volumes;

/// <summary>How long discovery keeps looking, and the guarantees that make the retry loop safe.</summary>
public sealed class VolumeWaitPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AtLeastOneAttemptIsRequired(int attempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeWaitPolicy(attempts, TimeSpan.Zero));
    }

    [Fact]
    public void TheDelayCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeWaitPolicy(1, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void MaximumWaitCountsTheGapsBetweenAttemptsNotTheAttemptsThemselves()
    {
        VolumeWaitPolicy policy = new(4, TimeSpan.FromMilliseconds(250));

        Assert.Equal(TimeSpan.FromMilliseconds(750), policy.MaximumWait);
    }

    [Fact]
    public void OneAttemptWaitsForNothing()
    {
        VolumeWaitPolicy policy = new(1, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, policy.MaximumWait);
    }

    [Fact]
    public void DefaultIsTwentyLooksAQuarterSecondApart()
    {
        Assert.Equal(20, VolumeWaitPolicy.Default.Attempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), VolumeWaitPolicy.Default.Delay);
    }

    [Fact]
    public void OnceIsASingleLookWithNoWaiting()
    {
        Assert.Equal(1, VolumeWaitPolicy.Once.Attempts);
        Assert.Equal(TimeSpan.Zero, VolumeWaitPolicy.Once.Delay);
    }
}
