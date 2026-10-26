using Ecnanib.Agents.Abstractions;
using Ecnanib.Components.Health;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;
using System.Net;
using Ecnanib.Configuration;

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

    /// <summary>WCAG 2.2.1: the status page reloads only when asked, and says which state it is in.</summary>
    [Fact]
    public void TheReloadTextsNameBothStates()
    {
        Assert.Equal("This page does not reload by itself.", HealthMessages.ReloadHint);
        Assert.Equal("This page reloads every minute.", HealthMessages.ReloadingHint);
        Assert.Equal("Reload every minute", HealthMessages.StartReloading);
        Assert.Equal("Stop reloading", HealthMessages.StopReloading);
    }

    [Fact]
    public void DeadlineMissesReadNoneOrACountWithTheLastAge()
    {
        var now = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("none", HealthMessages.DeadlineMisses(0, null, now));
        Assert.Equal("3 since start, last 2 min ago", HealthMessages.DeadlineMisses(3, now.AddMinutes(-2), now));
    }

    [Fact]
    public void InterruptedAtStartReadsNoneOrACount()
    {
        Assert.Equal("none", HealthMessages.InterruptedAtStart(0));
        Assert.Equal("2 recorded as failed at start", HealthMessages.InterruptedAtStart(2));
    }

    [Fact]
    public void TheInstanceLockReadsItsState()
    {
        var held = new InstanceLockStatus(InstanceLockState.Held, Now.AddHours(-1), 4242, Now.AddSeconds(-20), null, null);

        Assert.Equal("held since 2026-01-05 11:00:00 UTC (backend 4242), checked 20 s ago", HealthMessages.InstanceLock(held, Now));
        Assert.Equal("status-healthy", HealthMessages.InstanceLockClass(InstanceLockState.Held));
        Assert.Equal("re-acquiring: connection reset", HealthMessages.InstanceLock(held with { State = InstanceLockState.Reacquiring, LastError = "connection reset" }, Now));
        Assert.Equal("status-degraded", HealthMessages.InstanceLockClass(InstanceLockState.Reacquiring));
        Assert.Equal("lost: another instance holds it; this instance is stopping", HealthMessages.InstanceLock(held with { State = InstanceLockState.Lost }, Now));
        Assert.Equal("status-unhealthy", HealthMessages.InstanceLockClass(InstanceLockState.Lost));
        Assert.Equal("not held", HealthMessages.InstanceLock(InstanceLockStatus.NotHeld, Now));
    }

    [Fact]
    public void ReconciliationReadsRoundsChecksAndAdoptions()
    {
        var status = new ReconciliationStatus(true, Now.AddHours(-1), Now.AddMinutes(-2), 3, 0, 2, 1, 4, Now.AddMinutes(-5), null, null);

        Assert.Equal("2 min ago, 3 agent(s)", HealthMessages.LastRound(status, Now));
        Assert.Equal("2 min ago, 3 agent(s), 1 failed", HealthMessages.LastRound(status with { LastRoundFailures = 1 }, Now));
        Assert.Equal("2 agent(s) verified, 1 awaiting", HealthMessages.RestartChecks(status));
        Assert.Equal("4 since start, last 5 min ago", HealthMessages.Adoptions(status, Now));
        Assert.Equal("never", HealthMessages.LastRound(ReconciliationStatus.Unavailable, Now));
        Assert.Equal("none", HealthMessages.Adoptions(ReconciliationStatus.Unavailable, Now));
    }


    /// <summary>NFR-SEC-03: the status page groups streams by kind, environment and market; the symbols stay with their owners.</summary>
    [Fact]
    public void StreamsAreGroupedByKindEnvironmentAndMarketWithoutSymbols()
    {
        IReadOnlyList<StreamStatus> streams =
        [
            new("candles:production:spot:btcusdt:1h", StreamHealth.Connected, Now.AddSeconds(-30), Now.AddHours(-2), null),
            new("candles:production:spot:ethusdt:15m", StreamHealth.Polling, Now.AddSeconds(-5), Now.AddHours(-1), "HttpRequestException"),
            new("candles:testnet:usdfutures:btcusdt:1h", StreamHealth.Down, null, Now, "TimeoutException"),
        ];

        var groups = HealthMessages.GroupStreams(streams);

        Assert.Equal(["candles:production:spot", "candles:testnet:usdfutures"], groups.Select(g => g.Name));
        Assert.Equal((2, StreamHealth.Polling, Now.AddSeconds(-5), Now.AddHours(-2), "HttpRequestException"), (groups[0].Count, groups[0].Worst, groups[0].LastMessageAt, groups[0].Since, groups[0].LastError));
        Assert.Equal((1, StreamHealth.Down, (DateTimeOffset?)null), (groups[1].Count, groups[1].Worst, groups[1].LastMessageAt));
        Assert.DoesNotContain(groups, g => g.Name.Contains("btcusdt", StringComparison.Ordinal));
        Assert.Empty(HealthMessages.GroupStreams([]));
    }

    /// <summary>NFR-SEC-02: the Transport card states the public origin's consequences, this request's transport and the trusted proxies.</summary>
    [Fact]
    public void TransportTextsFollowThePublicOrigin()
    {
        var https = new ServerOptions { PublicUrl = "https://ecnanib.example.com", TrustedProxies = ["10.0.0.0/8", "203.0.113.9"] };
        var http = new ServerOptions();

        Assert.Equal("https://ecnanib.example.com: https, so cookies are Secure, HSTS is sent and a plain request is redirected", HealthMessages.PublicOrigin(https));
        Assert.Equal("http://localhost:5207: plain http, allowed for this machine only", HealthMessages.PublicOrigin(http));
        Assert.Equal("loopback and 2 configured", HealthMessages.TrustedProxies(https));
        Assert.Equal("loopback only", HealthMessages.TrustedProxies(http));
        Assert.Equal("https from 203.0.113.9", HealthMessages.RequestTransport(true, IPAddress.Parse("203.0.113.9")));
        Assert.Equal("http from an unknown address", HealthMessages.RequestTransport(false, null));
    }
}
