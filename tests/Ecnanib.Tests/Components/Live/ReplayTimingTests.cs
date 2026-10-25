using Ecnanib.Components.Live;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Components.Live;

public class ReplayTimingTests
{
    [Theory]
    [InlineData(RoleOutputStatus.Succeeded, 0, 400)]
    [InlineData(RoleOutputStatus.Succeeded, 2000, 600)]
    [InlineData(RoleOutputStatus.Succeeded, 20_000, 1200)]
    [InlineData(RoleOutputStatus.Failed, 8000, 1200)]
    [InlineData(RoleOutputStatus.TimedOut, 60_000, 1200)]
    [InlineData(RoleOutputStatus.Succeeded, -5, 400)]
    [InlineData(RoleOutputStatus.Disabled, 0, 250)]
    [InlineData(RoleOutputStatus.Skipped, 5000, 250)]
    public void ATurnLastsAMomentProportionalToItsLatencyWithinBounds(RoleOutputStatus status, int latencyMs, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ReplayTiming.Duration(status, latencyMs));

    [Fact]
    public void TheTransitionsStayWithinTheFourHundredMillisecondRule()
    {
        Assert.True(ReplayTiming.Handoff <= TimeSpan.FromMilliseconds(400));
        Assert.True(ReplayTiming.Reveal <= TimeSpan.FromMilliseconds(400));
        Assert.True(ReplayTiming.Minimum <= ReplayTiming.Maximum);
    }
}
