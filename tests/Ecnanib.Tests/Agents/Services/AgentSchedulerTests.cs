using Ecnanib.Agents.Services;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>The scheduler's rules and plumbing that need no database (FR-AGT-09, NFR-PERF-01, NFR-REL-01).</summary>
public class AgentSchedulerTests
{
    private static readonly DateTimeOffset Boundary = new(2026, 1, 5, 10, 15, 0, TimeSpan.Zero);
    private static readonly MarketKey Key = new(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");

    [Fact]
    public void ClassifyAppliesTheStalenessRules()
    {
        var open = Boundary - TimeSpan.FromMinutes(15);
        var deadline = TimeSpan.FromSeconds(90);

        // Closed 4 s before tracking: within the minute of tolerance, evaluated (the hub's safety poll case).
        Assert.Equal(AgentScheduler.ArrivalDisposition.Evaluate, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, Boundary.AddSeconds(4), null, Boundary.AddSeconds(10), deadline));
        // Closed 15 minutes before tracking: ignored.
        Assert.Equal(AgentScheduler.ArrivalDisposition.ClosedBeforeTracking, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, Boundary.AddMinutes(15), null, Boundary.AddMinutes(15), deadline));
        // Exactly at the tolerance edge still counts.
        Assert.Equal(AgentScheduler.ArrivalDisposition.Evaluate, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, Boundary + AgentScheduler.TrackingTolerance, null, Boundary.AddSeconds(70), deadline));
        // A newer candle was already seen on the stream: superseded.
        Assert.Equal(AgentScheduler.ArrivalDisposition.Superseded, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, open, open.AddMinutes(15), Boundary.AddSeconds(5), deadline));
        // The same candle seen again is not superseded.
        Assert.Equal(AgentScheduler.ArrivalDisposition.Evaluate, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, open, open, Boundary.AddSeconds(5), deadline));
        // The deadline measured from the close has passed: too late to start.
        Assert.Equal(AgentScheduler.ArrivalDisposition.TooLate, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, open, null, Boundary.AddSeconds(90), deadline));
        Assert.Equal(AgentScheduler.ArrivalDisposition.Evaluate, AgentScheduler.Classify(open, CandleInterval.FifteenMinutes, open, null, Boundary.AddSeconds(89), deadline));
    }

    [Fact]
    public async Task TrackSubscribesOncePerAgentAndUntrackDisposes()
    {
        var streams = new FakeMarketStreams();
        var factory = new ThrowingDbContextFactory();
        await using var provider = AgentsTestHost.BuildServices(streams: streams, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var agentId = Guid.NewGuid();
        var info = new AgentTrackingInfo(agentId, Guid.NewGuid(), Key, CandleInterval.OneHour, "trend-pullback", 1);

        Assert.True(scheduler.Track(info));
        Assert.True(scheduler.Track(info));
        Assert.True(scheduler.IsTracked(agentId));
        Assert.Equal(1, streams.CandleSubscriptions);
        Assert.True(streams.HasCandleSubscription(Key, CandleInterval.OneHour));

        // The handler only enqueues: nothing is evaluated (no worker runs) and the database is never touched.
        await streams.PushCandleAsync(Key, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, Boundary));
        Assert.Equal(0, factory.Calls);

        scheduler.Untrack(agentId);
        scheduler.Untrack(agentId);
        Assert.False(scheduler.IsTracked(agentId));
        Assert.Equal(0, streams.CandleSubscriptions);
        Assert.Equal(1, streams.CandleSubscriptionsEver);
    }

    [Fact]
    public async Task StartupRetriesUntilTheDatabaseAnswersAndTrackIsRefusedAfterShutdownBegan()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var factory = new ThrowingDbContextFactory();
        var streams = new FakeMarketStreams();
        await using var provider = AgentsTestHost.BuildServices(logs: logs, time: time, streams: streams, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var scheduler = provider.GetRequiredService<AgentScheduler>();

        await scheduler.StartAsync(AgentsTestHost.Ct);
        await Wait.UntilAsync(() => factory.Calls == 1, "first attempt");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.StartsWith("The agent scheduler could not start", StringComparison.Ordinal)), "warning logged");
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);

        time.Advance(AgentScheduler.StartupRetry - TimeSpan.FromSeconds(1));
        await Wait.SettleAsync();
        Assert.Equal(1, factory.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => factory.Calls == 2, "second attempt");

        await scheduler.StopAsync(AgentsTestHost.Ct);
        Assert.False(scheduler.Track(new AgentTrackingInfo(Guid.NewGuid(), Guid.NewGuid(), Key, CandleInterval.OneHour, "trend-pullback", 1)));
        Assert.Equal(0, streams.CandleSubscriptionsEver);
    }

    [Fact]
    public async Task TheGateSerialisesLifecycleActionsAndCancelIsHarmlessWithoutARun()
    {
        await using var provider = AgentsTestHost.BuildServices();
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var agentId = Guid.NewGuid();

        var first = await scheduler.LockAsync(agentId, AgentsTestHost.Ct);
        var second = scheduler.LockAsync(agentId, AgentsTestHost.Ct);
        await Wait.SettleAsync();
        Assert.False(second.IsCompleted);
        scheduler.CancelEvaluation(agentId);

        first.Dispose();
        first.Dispose();
        using var held = await second;
        Assert.True(second.IsCompletedSuccessfully);

        using var otherAgent = await scheduler.LockAsync(Guid.NewGuid(), AgentsTestHost.Ct);
        scheduler.Forget(agentId);
    }
}
