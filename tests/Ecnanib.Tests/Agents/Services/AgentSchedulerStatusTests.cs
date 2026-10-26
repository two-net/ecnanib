using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>The scheduler's self-report for the status page (NFR-OBS-01), without a database.</summary>
public class AgentSchedulerStatusTests
{
    private static readonly MarketKey Key = new(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");

    [Fact]
    public async Task ThePhaseTheCountsAndTheLastErrorFollowTheSchedulersLife()
    {
        var time = new FakeTimeProvider();
        var factory = new ThrowingDbContextFactory();
        var streams = new FakeMarketStreams();
        await using var provider = AgentsTestHost.BuildServices(time: time, streams: streams, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var status = provider.GetRequiredService<ISchedulerStatus>();
        Assert.Same(scheduler, status);

        var before = status.GetStatus();
        Assert.Equal(SchedulerPhase.NotStarted, before.Phase);
        Assert.Null(before.StartedAt);
        Assert.Null(before.LastTickAt);
        Assert.Null(before.LastError);
        Assert.Equal((0, 0, 0), (before.TrackedAgents, before.InFlightEvaluations, before.QueuedEvaluations));
        Assert.True(before.MaxConcurrentEvaluations > 0);
        Assert.Equal(0, before.DeadlineMisses);
        Assert.Null(before.LastDeadlineMissAt);

        // The database is down: the scheduler stays in Starting, retrying, and says why.
        await scheduler.StartAsync(AgentsTestHost.Ct);
        await Wait.UntilAsync(() => factory.Calls == 1, "first attempt");
        await Wait.UntilAsync(() => status.GetStatus().LastError is not null, "error recorded");
        var starting = status.GetStatus();
        Assert.Equal(SchedulerPhase.Starting, starting.Phase);
        // The status page shows the failure's kind, never its message (NFR-SEC-03).
        Assert.Matches("^[A-Za-z]+Exception$", starting.LastError);
        Assert.DoesNotContain("database down", starting.LastError, StringComparison.Ordinal);
        Assert.Equal(time.GetUtcNow(), starting.LastErrorAt);
        Assert.Null(starting.StartedAt);

        Assert.True(scheduler.Track(new AgentTrackingInfo(Guid.NewGuid(), Guid.NewGuid(), Key, CandleInterval.OneHour, "trend-pullback", 1)));
        Assert.Equal(1, status.GetStatus().TrackedAgents);

        await scheduler.StopAsync(AgentsTestHost.Ct);
        Assert.Equal(SchedulerPhase.Stopped, status.GetStatus().Phase);
    }
}
