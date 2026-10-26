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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Health.Services;

/// <summary>
/// The health report (NFR-OBS-01) over fakes: a failing part is reported as a kind with the detail logged, never thrown;
/// each part carries its own state and the worst is the whole; the roll-up applies its rules; callers that overlap
/// share one probe and a later caller gets a fresh one.
/// </summary>
public class SystemHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADownDatabaseAndAFailingCatalogAreReportedAsKindsWithoutAnException()
    {
        var factory = new ThrowingDbContextFactory();
        var logs = new TestLoggerProvider();
        var exchange = new FakeExchangeHealth { MarketStreams = [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Connected)] };
        var health = Build(factory, exchange, Running(), new FakeModelCatalog { Error = new TimeoutException("catalog down at db.internal:5432") }, Started(), Reconciled(), Held(), new FakeTimeProvider(Now), logs);

        var report = await health.GetAsync(Ct);

        Assert.Equal(Now, report.At);
        Assert.Equal(HealthState.Unhealthy, report.Overall);
        Assert.Equal(HealthState.Unhealthy, report.Database.State);
        Assert.Equal("InvalidOperationException", report.Database.Error);
        Assert.Equal(1, factory.Calls);
        Assert.Equal("TimeoutException", report.ModelSyncError);
        Assert.Empty(report.ModelSync);
        Assert.Equal(SchedulerPhase.Running, report.Scheduler.Phase);
        Assert.Single(report.MarketStreams);
        Assert.Same(RateLimitSnapshot.Empty, report.RateLimits);
        Assert.Equal(InstanceLockState.Held, report.InstanceLock.State);
        Assert.Equal(2, report.Reconciliation.CheckedAgents);
        Assert.Equal(Started(), report.ModelSyncJob);
        Assert.Equal(HealthState.Unhealthy, report.States.Database);
        Assert.Equal(HealthState.Degraded, report.States.ModelSync);
        Assert.Equal(HealthState.Healthy, report.States.Scheduler);
        Assert.Equal(report.States.Overall, report.Overall);
        // The detail is in the log, at Warning, beside each kind (NFR-SEC-03).
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "The database probe failed: InvalidOperationException" && e.Exception?.Message == "database down");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "The model sync states read failed: TimeoutException" && e.Exception?.Message == "catalog down at db.internal:5432");
    }

    [Fact]
    public async Task AProbeThatNeverAnswersTimesOutUnderTheFakeClock()
    {
        var time = new FakeTimeProvider(Now);
        var logs = new TestLoggerProvider();
        var health = Build(new HangingDbContextFactory(), new FakeExchangeHealth(), Running(), new FakeModelCatalog(), Started(), ReconciliationStatus.Unavailable, Held(), time, logs);

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
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "The database probe failed: No answer within 5 s.");
    }

    [Fact]
    public async Task ASyncStatesReadThatNeverAnswersTimesOutLikeTheProbe()
    {
        var time = new FakeTimeProvider(Now);
        var health = Build(new ThrowingDbContextFactory(), new FakeExchangeHealth(), Running(), new FakeModelCatalog { Hang = true }, Started(), Reconciled(), Held(), time);

        var probe = health.GetAsync(Ct);
        await Wait.SettleAsync();
        Assert.False(probe.IsCompleted);
        time.Advance(SystemHealth.ProbeTimeout);
        var report = await probe;

        Assert.Equal("No answer within 5 s.", report.ModelSyncError);
        Assert.Empty(report.ModelSync);
        Assert.Equal(HealthState.Degraded, report.States.ModelSync);
    }

    /// <summary>An outage is logged when it starts, when it changes and when it ends, not on every report an anonymous client asks for.</summary>
    [Fact]
    public async Task AnOutageIsLoggedWhenItStartsChangesAndEndsNotPerReport()
    {
        var logs = new TestLoggerProvider();
        var catalog = new FakeModelCatalog { Error = new TimeoutException("catalog down") };
        var health = Build(new ThrowingDbContextFactory(), new FakeExchangeHealth(), Running(), catalog, Started(), Reconciled(), Held(), new FakeTimeProvider(Now), logs);

        await health.GetAsync(Ct);
        await health.GetAsync(Ct);
        catalog.Error = new InvalidOperationException("down differently");
        await health.GetAsync(Ct);
        catalog.Error = null;
        var recovered = await health.GetAsync(Ct);

        Assert.Null(recovered.ModelSyncError);
        (LogLevel, string)[] expected =
        [
            (LogLevel.Warning, "The model sync states read failed: TimeoutException"),
            (LogLevel.Debug, "The model sync states read still fails: TimeoutException"),
            (LogLevel.Warning, "The model sync states read failed: InvalidOperationException"),
            (LogLevel.Information, "The model sync states read answers again"),
        ];
        Assert.Equal(expected, logs.Entries.Where(e => e.Message.StartsWith("The model sync states read", StringComparison.Ordinal)).Select(e => (e.Level, e.Message)));
        // The probe failed the same way all four times: one Warning, then Debug.
        Assert.Equal(1, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message == "The database probe failed: InvalidOperationException"));
        Assert.Equal(3, logs.Entries.Count(e => e.Level == LogLevel.Debug && e.Message == "The database probe still fails: InvalidOperationException"));
    }

    /// <summary>Single flight, not a cache: overlapping callers share the probe in progress; a caller after it completed gets a fresh one.</summary>
    [Fact]
    public async Task OverlappingCallersShareOneProbeAndALaterCallerProbesAgain()
    {
        var time = new FakeTimeProvider(Now);
        var factory = new HangingDbContextFactory();
        var health = Build(factory, new FakeExchangeHealth(), Running(), new FakeModelCatalog(), Started(), Reconciled(), Held(), time);

        var first = health.GetAsync(Ct);
        var second = health.GetAsync(Ct);
        await Wait.SettleAsync();
        Assert.False(first.IsCompleted);
        Assert.Equal(1, factory.Calls);
        time.Advance(SystemHealth.ProbeTimeout);
        var shared = await first;
        Assert.Same(shared, await second);

        var third = health.GetAsync(Ct);
        await Wait.SettleAsync();
        Assert.Equal(2, factory.Calls);
        time.Advance(SystemHealth.ProbeTimeout);
        Assert.NotSame(shared, await third);
    }

    [Fact]
    public async Task ACancelledCallerLeavesTheSharedProbeToTheOthers()
    {
        var time = new FakeTimeProvider(Now);
        var factory = new HangingDbContextFactory();
        var health = Build(factory, new FakeExchangeHealth(), Running(), new FakeModelCatalog(), Started(), Reconciled(), Held(), time);
        using var impatient = new CancellationTokenSource();

        var cancelled = health.GetAsync(impatient.Token);
        var patient = health.GetAsync(Ct);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Wait.SettleAsync();
        Assert.False(patient.IsCompleted);

        time.Advance(SystemHealth.ProbeTimeout);
        var report = await patient;

        Assert.Equal("No answer within 5 s.", report.Database.Error);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public void EachPartCarriesItsOwnStateAndTheWorstIsTheWhole()
    {
        var states = SystemHealth.RollUp(
            new DatabaseHealth(HealthState.Unhealthy, null, "NpgsqlException"),
            Running() with { StartedAt = Now.AddMinutes(-10), LastTickAt = Now.AddMinutes(-4) },
            [Stream("candles:production:spot:btcusdt:1h", StreamHealth.Down)],
            [Stream("userdata:spot:key", StreamHealth.Polling)],
            [Sync(AiProvider.Claude, SyncStatus.Succeeded)],
            null,
            Started(),
            RateLimitSnapshot.Empty,
            Reconciled() with { AwaitingCheck = 1 },
            Held() with { State = InstanceLockState.Reacquiring },
            Now);

        Assert.Equal(HealthState.Unhealthy, states.Database);
        Assert.Equal(HealthState.Degraded, states.InstanceLock);
        Assert.Equal(HealthState.Degraded, states.Scheduler);
        Assert.Equal(HealthState.Degraded, states.MarketStreams);
        Assert.Equal(HealthState.Healthy, states.UserDataStreams);
        Assert.Equal(HealthState.Healthy, states.ModelSync);
        Assert.Equal(HealthState.Healthy, states.RateLimits);
        Assert.Equal(HealthState.Degraded, states.Reconciliation);
        Assert.Equal(HealthState.Unhealthy, states.Overall);
        Assert.Equal(HealthState.Healthy, SystemHealthStates.AllHealthy.Overall);
        Assert.Equal(HealthState.Degraded, (SystemHealthStates.AllHealthy with { RateLimits = HealthState.Degraded }).Overall);
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
        { "sync-job-overdue", HealthState.Degraded },
        { "sync-job-due-within-grace", HealthState.Healthy },
        { "sync-job-running-past-due", HealthState.Healthy },
        { "sync-job-running-stuck", HealthState.Degraded },
        { "sync-job-last-pass-failed", HealthState.Degraded },
        { "sync-job-not-started", HealthState.Healthy },
        { "rate-limited", HealthState.Degraded },
        { "rate-limit-over", HealthState.Healthy },
        { "database-unhealthy-beats-degraded", HealthState.Unhealthy },
        { "reconciliation-unavailable", HealthState.Healthy },
        { "reconciliation-last-round-failed", HealthState.Degraded },
        { "reconciliation-whole-round-failed", HealthState.Degraded },
        { "reconciliation-recovered", HealthState.Healthy },
        { "restart-checks-pending-after-grace", HealthState.Degraded },
        { "restart-checks-pending-fresh-start", HealthState.Healthy },
        { "instance-lock-not-held", HealthState.Healthy },
        { "instance-lock-reacquiring", HealthState.Degraded },
        { "instance-lock-lost", HealthState.Unhealthy },
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
        var job = Started();
        var limits = RateLimitSnapshot.Empty;
        var reconciliation = Reconciled();
        var instanceLock = Held();

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
                syncError = "TimeoutException";
                break;
            case "sync-job-overdue":
                job = job with { NextRunAt = Now.AddMinutes(-4) };
                break;
            case "sync-job-due-within-grace":
                job = job with { NextRunAt = Now.AddMinutes(-2) };
                break;
            case "sync-job-running-past-due":
                // A pass that takes long (four providers, each with its call timeout and retries) is not a stuck loop.
                job = job with { NextRunAt = Now.AddMinutes(-10), RunningSince = Now.AddMinutes(-9) };
                break;
            case "sync-job-running-stuck":
                // A pass in progress for an hour has a provider call that is not ending.
                job = job with { NextRunAt = Now.AddMinutes(-61), RunningSince = Now.AddMinutes(-61) };
                break;
            case "sync-job-last-pass-failed":
                job = job with { LastRunFailures = 1, LastError = "InvalidOperationException", LastErrorAt = Now.AddMinutes(-30) };
                break;
            case "sync-job-not-started":
                job = ModelSyncJobStatus.NotStarted;
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
            case "reconciliation-unavailable":
                reconciliation = ReconciliationStatus.Unavailable;
                break;
            case "reconciliation-last-round-failed":
                // The round records its failures and then its own completion, so the failure count is what tells, not the timestamps.
                reconciliation = reconciliation with { LastRoundFailures = 1, LastError = "key could not be read", LastErrorAt = Now.AddMinutes(-1).AddMilliseconds(-5) };
                break;
            case "reconciliation-whole-round-failed":
                reconciliation = reconciliation with { LastError = "NpgsqlException", LastErrorAt = Now.AddSeconds(-20) };
                break;
            case "reconciliation-recovered":
                reconciliation = reconciliation with { LastError = "key could not be read", LastErrorAt = Now.AddMinutes(-5) };
                break;
            case "restart-checks-pending-after-grace":
                reconciliation = reconciliation with { AwaitingCheck = 1 };
                break;
            case "restart-checks-pending-fresh-start":
                scheduler = scheduler with { StartedAt = Now.AddMinutes(-1) };
                reconciliation = reconciliation with { AwaitingCheck = 1 };
                break;
            case "instance-lock-not-held":
                instanceLock = InstanceLockStatus.NotHeld;
                break;
            case "instance-lock-reacquiring":
                instanceLock = instanceLock with { State = InstanceLockState.Reacquiring, LastError = "NpgsqlException" };
                break;
            case "instance-lock-lost":
                instanceLock = instanceLock with { State = InstanceLockState.Lost };
                break;
        }

        Assert.Equal(expected, SystemHealth.RollUp(database, scheduler, market, userData, sync, syncError, job, limits, reconciliation, instanceLock, Now).Overall);
    }

    private static SystemHealth Build(IDbContextFactory<EcnanibDbContext> factory, IExchangeHealth exchange, SchedulerStatus scheduler, IModelCatalog catalog, ModelSyncJobStatus job,
        ReconciliationStatus reconciliation, InstanceLockStatus instanceLock, TimeProvider time, TestLoggerProvider? logs = null) =>
        new(factory, exchange, new FakeSchedulerStatus(scheduler), catalog, new FakeModelSyncStatus(job), new FakeReconciliationStatus(reconciliation), new FakeInstanceLockStatus(instanceLock), time,
            logs is null ? NullLogger<SystemHealth>.Instance : TestLogging.CreateFactory(logs).CreateLogger<SystemHealth>());

    private static SchedulerStatus Running() => new(SchedulerPhase.Running, Now.AddHours(-1), Now.AddSeconds(-30), 3, 1, 0, 10, null, null, 0, null, 0);

    /// <summary>A reconciliation that ran a minute ago over two verified agents with nothing awaiting (NFR-REL-01).</summary>
    private static ReconciliationStatus Reconciled() => new(true, Now.AddHours(-1), Now.AddMinutes(-1), 2, 0, 2, 0, 1, Now.AddMinutes(-30), null, null);

    private static InstanceLockStatus Held() => new(InstanceLockState.Held, Now.AddHours(-1), 4242, Now.AddSeconds(-10), null, null);

    /// <summary>A sync job started an hour ago whose last pass, half an hour ago, synced two providers and found no key for two; the next is due in half an hour.</summary>
    private static ModelSyncJobStatus Started() => new(Now.AddHours(-1), null, Now.AddMinutes(-30), Now.AddMinutes(30), 2, 2, 0, 0, null, null);

    private static StreamStatus Stream(string name, StreamHealth health) => new(name, health, Now.AddSeconds(-2), Now.AddHours(-1), health == StreamHealth.Down ? "poll failed" : null);

    private static ProviderSyncView Sync(AiProvider provider, SyncStatus status) =>
        new(provider, status == SyncStatus.Never ? null : Now.AddMinutes(-30), status, status == SyncStatus.Failed ? "401" : null, status == SyncStatus.Never ? 0 : 5, status == SyncStatus.Never ? null : SyncTrigger.Scheduled, 5, 0);

    private sealed class FakeSchedulerStatus(SchedulerStatus status) : ISchedulerStatus
    {
        public SchedulerStatus GetStatus() => status;
    }

    private sealed class FakeModelSyncStatus(ModelSyncJobStatus status) : IModelSyncStatus
    {
        public ModelSyncJobStatus GetStatus() => status;
    }

    private sealed class FakeReconciliationStatus(ReconciliationStatus status) : IReconciliationStatus
    {
        public ReconciliationStatus GetStatus() => status;
    }

    private sealed class FakeInstanceLockStatus(InstanceLockStatus status) : IInstanceLockStatus
    {
        public InstanceLockStatus GetStatus() => status;
    }

    private sealed class FakeModelCatalog : IModelCatalog
    {
        public Exception? Error { get; set; }

        /// <summary>A read that never answers, for the bound on the sync states.</summary>
        public bool Hang { get; init; }

        public Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public async Task<IReadOnlyList<ProviderSyncView>> GetSyncStatesAsync(CancellationToken cancellationToken)
        {
            if (Error is { } error)
                throw error;
            if (Hang)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Enum.GetValues<AiProvider>().Select(p => Sync(p, SyncStatus.Never)).ToList();
        }
    }

    /// <summary>A database that accepts the connection attempt and never answers it; every attempt counts.</summary>
    private sealed class HangingDbContextFactory : IDbContextFactory<EcnanibDbContext>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EcnanibDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<EcnanibDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
