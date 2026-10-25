using System.Diagnostics;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;
using Ecnanib.Health.Services;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Health.Services;

/// <summary>The health report (NFR-OBS-01) over fakes: a failing part is reported, never thrown, and the roll-up applies its rules.</summary>
public class SystemHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADownDatabaseAndAFailingCatalogAreReportedWithoutAnException()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeHealth { MarketStreams = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Connected)] };
        var health = new SystemHealth(factory, exchange, new FakeSchedulerStatus(Running()), new FakeModelCatalog { Error = new TimeoutException("catalog down") }, new FakeTimeProvider(Now), NullLogger<SystemHealth>.Instance);

        var report = await health.GetAsync(Ct);

        Assert.Equal(Now, report.At);
        Assert.Equal(HealthState.Unhealthy, report.Overall);
        Assert.Equal(HealthState.Unhealthy, report.Database.State);
        Assert.Equal("database down", report.Database.Error);
        Assert.Equal(1, factory.Calls);
        Assert.Equal("catalog down", report.ModelSyncError);
        Assert.Empty(report.ModelSync);
        Assert.Equal(SchedulerPhase.Running, report.Scheduler.Phase);
        Assert.Single(report.MarketStreams);
        Assert.Same(RateLimitSnapshot.Empty, report.RateLimits);
    }

    [Fact]
    public async Task AProbeThatNeverAnswersTimesOutUnderTheFakeClock()
    {
        var time = new FakeTimeProvider(Now);
        var health = new SystemHealth(new HangingDbContextFactory(), new FakeExchangeHealth(), new FakeSchedulerStatus(Running()), new FakeModelCatalog(), time, NullLogger<SystemHealth>.Instance);

        var probe = health.GetAsync(Ct);
        await Wait.SettleAsync();
        Assert.False(probe.IsCompleted);
        time.Advance(SystemHealth.ProbeTimeout);
        var report = await probe;

        Assert.Equal(HealthState.Unhealthy, report.Database.State);
        Assert.Equal("No answer within 5 s.", report.Database.Error);
        Assert.Equal(SystemHealth.ProbeTimeout, report.Database.Latency);
        Assert.Equal(4, report.ModelSync.Count);
        Assert.Null(report.ModelSyncError);
    }

    public static TheoryData<string, HealthState> RollUpCases => new()
    {
        { "healthy", HealthState.Healthy },
        { "database-unhealthy", HealthState.Unhealthy },
        { "scheduler-not-started", HealthState.Unhealthy },
        { "scheduler-stopped", HealthState.Unhealthy },
        { "scheduler-starting", HealthState.Degraded },
        { "tick-stale", HealthState.Degraded },
        { "tick-fresh-start", HealthState.Healthy },
        { "stream-down", HealthState.Degraded },
        { "stream-polling", HealthState.Healthy },
        { "user-stream-down", HealthState.Degraded },
        { "sync-failed", HealthState.Degraded },
        { "sync-never", HealthState.Healthy },
        { "sync-error", HealthState.Degraded },
        { "rate-limited", HealthState.Degraded },
        { "rate-limit-over", HealthState.Healthy },
        { "database-unhealthy-beats-degraded", HealthState.Unhealthy },
    };

    [Theory]
    [MemberData(nameof(RollUpCases))]
    public void TheRollUpAppliesTheRulesAndTheWorstWins(string scenario, HealthState expected)
    {
        var database = new DatabaseHealth(HealthState.Healthy, TimeSpan.FromMilliseconds(3), null);
        var scheduler = Running();
        IReadOnlyList<StreamStatus> market = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Connected)];
        IReadOnlyList<StreamStatus> userData = [Stream("userdata:spot:key", StreamHealth.Connected)];
        IReadOnlyList<ProviderSyncView> sync = [Sync(AiProvider.Claude, SyncStatus.Succeeded)];
        string? syncError = null;
        var limits = RateLimitSnapshot.Empty;

        switch (scenario)
        {
            case "database-unhealthy":
                database = new DatabaseHealth(HealthState.Unhealthy, null, "down");
                break;
            case "scheduler-not-started":
                scheduler = scheduler with { Phase = SchedulerPhase.NotStarted, StartedAt = null };
                break;
            case "scheduler-stopped":
                scheduler = scheduler with { Phase = SchedulerPhase.Stopped };
                break;
            case "scheduler-starting":
                scheduler = scheduler with { Phase = SchedulerPhase.Starting, StartedAt = null };
                break;
            case "tick-stale":
                scheduler = scheduler with { StartedAt = Now.AddMinutes(-10), LastTickAt = Now.AddMinutes(-4) };
                break;
            case "tick-fresh-start":
                scheduler = scheduler with { StartedAt = Now.AddMinutes(-2), LastTickAt = null };
                break;
            case "stream-down":
                market = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Down)];
                break;
            case "stream-polling":
                market = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Polling)];
                break;
            case "user-stream-down":
                userData = [Stream("userdata:spot:key", StreamHealth.Down)];
                break;
            case "sync-failed":
                sync = [Sync(AiProvider.Claude, SyncStatus.Succeeded), Sync(AiProvider.Gpt, SyncStatus.Failed)];
                break;
            case "sync-never":
                sync = [Sync(AiProvider.Claude, SyncStatus.Never)];
                break;
            case "sync-error":
                syncError = "catalog down";
                break;
            case "rate-limited":
                limits = new RateLimitSnapshot([], Now.AddSeconds(-5), Now.AddSeconds(25));
                break;
            case "rate-limit-over":
                limits = new RateLimitSnapshot([], Now.AddMinutes(-5), Now.AddMinutes(-4));
                break;
            case "database-unhealthy-beats-degraded":
                database = new DatabaseHealth(HealthState.Unhealthy, null, "down");
                market = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Down)];
                break;
        }

        Assert.Equal(expected, SystemHealth.RollUp(database, scheduler, market, userData, sync, syncError, limits, Now));
    }

    private static SchedulerStatus Running() => new(SchedulerPhase.Running, Now.AddHours(-1), Now.AddSeconds(-30), 3, 1, 0, 10, null, null, 0, null);

    private static StreamStatus Stream(string name, StreamHealth health) => new(name, health, Now.AddSeconds(-2), Now.AddHours(-1), health == StreamHealth.Down ? "poll failed" : null);

    private static ProviderSyncView Sync(AiProvider provider, SyncStatus status) =>
        new(provider, status == SyncStatus.Never ? null : Now.AddMinutes(-30), status, status == SyncStatus.Failed ? "401" : null, status == SyncStatus.Never ? 0 : 5, status == SyncStatus.Never ? null : SyncTrigger.Scheduled, 5, 0);

    private sealed class FakeSchedulerStatus(SchedulerStatus status) : ISchedulerStatus
    {
        public SchedulerStatus GetStatus() => status;
    }

    private sealed class FakeModelCatalog : IModelCatalog
    {
        public Exception? Error { get; init; }

        public Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public Task<IReadOnlyList<ProviderSyncView>> GetSyncStatesAsync(CancellationToken cancellationToken)
        {
            if (Error is { } error)
                throw error;
            return Task.FromResult<IReadOnlyList<ProviderSyncView>>(Enum.GetValues<AiProvider>().Select(p => Sync(p, SyncStatus.Never)).ToList());
        }
    }

    /// <summary>A database that accepts the connection attempt and never answers it.</summary>
    private sealed class HangingDbContextFactory : IDbContextFactory<EcnanibDbContext>
    {
        public EcnanibDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<EcnanibDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
