using Ecnanib.Agents.Abstractions;
using Ecnanib.Components.Health;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;

namespace Ecnanib.Tests.Health.Pages;

public class HealthMessagesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "never")]
    [InlineData(3, "just now")]
    [InlineData(42, "42 s ago")]
    [InlineData(125, "2 min ago")]
    [InlineData(3 * 3600 + 5, "3 h ago")]
    [InlineData(2 * 86400 + 60, "2 d ago")]
    public void AgesReadNaturally(int? secondsAgo, string expected) =>
        Assert.Equal(expected, HealthMessages.Age(secondsAgo is { } s ? Now.AddSeconds(-s) : null, Now));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(12.0, "12 ms")]
    [InlineData(1200.0, "1.2 s")]
    [InlineData(0.4, "0 ms")]
    public void LatenciesAreMillisecondsBelowASecond(double? ms, string expected) =>
        Assert.Equal(expected, HealthMessages.Latency(ms is { } m ? TimeSpan.FromMilliseconds(m) : null));

    [Fact]
    public void StreamSummariesCountEachHealth()
    {
        IReadOnlyList<StreamStatus> streams =
        [
            new("a", StreamHealth.Connected, Now, Now, null), new("b", StreamHealth.Connected, Now, Now, null),
            new("c", StreamHealth.Polling, Now, Now, "lost"), new("d", StreamHealth.Down, null, Now, "poll failed"),
        ];

        Assert.Equal("2 connected, 1 polling, 1 down, 0 stopped", HealthMessages.Streams(streams));
        Assert.Equal("none", HealthMessages.Streams([]));
    }

    [Fact]
    public void ClassesAndNamesFollowTheEnums()
    {
        Assert.Equal(("Healthy", "status-healthy"), (HealthMessages.Describe(HealthState.Healthy), HealthMessages.StateClass(HealthState.Healthy)));
        Assert.Equal(("Degraded", "status-degraded"), (HealthMessages.Describe(HealthState.Degraded), HealthMessages.StateClass(HealthState.Degraded)));
        Assert.Equal(("Unhealthy", "status-unhealthy"), (HealthMessages.Describe(HealthState.Unhealthy), HealthMessages.StateClass(HealthState.Unhealthy)));
        Assert.Equal(("polling", "status-polling"), (HealthMessages.Stream(StreamHealth.Polling), HealthMessages.StreamClass(StreamHealth.Polling)));
        Assert.Equal(("Not started", "status-notstarted"), (HealthMessages.Phase(SchedulerPhase.NotStarted), HealthMessages.PhaseClass(SchedulerPhase.NotStarted)));
        Assert.Equal(("Running", "status-running"), (HealthMessages.Phase(SchedulerPhase.Running), HealthMessages.PhaseClass(SchedulerPhase.Running)));
        Assert.Equal("2026-01-05 12:00:00 UTC", HealthMessages.Stamp(Now));
        Assert.Equal("never", HealthMessages.Stamp(null));
    }
}
