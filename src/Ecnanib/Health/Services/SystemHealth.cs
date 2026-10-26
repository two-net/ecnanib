using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Health.Services;

/// <summary>
/// One report per call: a bounded database probe, a bounded read of the model sync states, the exchange's own stream
/// and rate-limit view, the scheduler's, the reconciliation's, the lock's and the sync job's statuses, rolled up into
/// one state per part and one for the whole. A component that fails is reported as failed, as a kind, with the detail
/// logged when the outcome changes. Callers that arrive while a report is being gathered share it (one probe at a time,
/// whatever an anonymous client does), and only each caller's own cancellation ends its wait.
/// </summary>
internal sealed class SystemHealth : ISystemHealth
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Three minute ticks without one completing means the scheduler's loops are stuck.</summary>
    internal static readonly TimeSpan TickStaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>Live agents with open rows still unverified against the exchange this long after the scheduler started have not been evaluated since the restart (NFR-REL-01).</summary>
    internal static readonly TimeSpan RestartCheckGrace = TimeSpan.FromMinutes(3);

    /// <summary>A scheduled model sync this long past its due time, with no pass in progress, means the job's loop is stuck (FR-SYNC-02).</summary>
    internal static readonly TimeSpan ModelSyncOverdueGrace = TimeSpan.FromMinutes(3);

    /// <summary>A pass in progress this long has a provider call that is not ending: the call timeout, the three attempts and the three keys per provider end even four dead providers within the hour.</summary>
    internal static readonly TimeSpan ModelSyncPassStuckAfter = TimeSpan.FromHours(1);

    internal static readonly string NoAnswer = $"No answer within {ProbeTimeout.TotalSeconds:0} s.";

    internal const string DidNotAnswer = "The database did not answer.";

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IExchangeHealth _exchange;
    private readonly ISchedulerStatus _scheduler;
    private readonly IModelCatalog _catalog;
    private readonly IModelSyncStatus _modelSyncJob;
    private readonly IReconciliationStatus _reconciliation;
    private readonly IInstanceLockStatus _instanceLock;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemHealth> _logger;

    private readonly Lock _flightLock = new();
    private Task<SystemHealthReport>? _flight;

    // The outcome last logged, per bounded read; touched by the one gather in flight only. Null while it answers.
    private string? _probeFailure;
    private string? _syncReadFailure;

    public SystemHealth(IDbContextFactory<EcnanibDbContext> factory, IExchangeHealth exchange, ISchedulerStatus scheduler, IModelCatalog catalog, IModelSyncStatus modelSyncJob,
        IReconciliationStatus reconciliation, IInstanceLockStatus instanceLock, TimeProvider time, ILogger<SystemHealth> logger)
    {
        _factory = factory;
        _exchange = exchange;
        _scheduler = scheduler;
        _catalog = catalog;
        _modelSyncJob = modelSyncJob;
        _reconciliation = reconciliation;
        _instanceLock = instanceLock;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Single flight, not a cache: a call that finds a report being gathered waits for that one; a call after the last
    /// report completed gathers a fresh one. The gather starts on the pool, so the lock holds nothing but the swap; the
    /// field is replaced, never cleared, so a gather that completes at once leaves no stale task behind.
    /// </summary>
    public Task<SystemHealthReport> GetAsync(CancellationToken cancellationToken)
    {
        Task<SystemHealthReport> flight;
        lock (_flightLock)
        {
            if (_flight is null || _flight.IsCompleted)
                _flight = Task.Run(GatherAsync);
            flight = _flight;
        }

        return flight.WaitAsync(cancellationToken);
    }

    private async Task<SystemHealthReport> GatherAsync()
    {
        var now = _time.GetUtcNow();
        // Side by side, each under its own bound, so a database that hangs costs one timeout, not two.
        var probe = ProbeDatabaseAsync();
        var read = ReadSyncStatesAsync();
        var database = await probe.ConfigureAwait(false);
        var (sync, syncError) = await read.ConfigureAwait(false);
        var scheduler = _scheduler.GetStatus();
        var market = _exchange.MarketStreams;
        var userData = _exchange.UserDataStreams;
        var limits = _exchange.RateLimits;
        var reconciliation = _reconciliation.GetStatus();
        var instanceLock = _instanceLock.GetStatus();
        var job = _modelSyncJob.GetStatus();
        var states = RollUp(database, scheduler, market, userData, sync, syncError, job, limits, reconciliation, instanceLock, now);
        return new SystemHealthReport(now, states.Overall, database, market, userData, scheduler, sync, syncError, limits, reconciliation, instanceLock, job, states);
    }

    /// <summary>
    /// One state per part, the worst winning overall. Unhealthy (nothing is, or soon will be, evaluated): the database
    /// unreachable, the scheduler NotStarted or Stopped, the instance lock taken by another process. Degraded: the
    /// scheduler still Starting or without a completed tick for <see cref="TickStaleAfter"/>; a stream Down (polling
    /// failed too); a provider whose last sync Failed, sync states that could not be read, the sync job's last pass hit
    /// an exception, or the job is <see cref="ModelSyncOverdueGrace"/> past its due time with no pass in progress (or
    /// one in progress for <see cref="ModelSyncPassStuckAfter"/>); an exchange rate limit in force;
    /// the lock being re-acquired; a reconciliation whose last round could not read a key or compare an agent (or whose
    /// last failure is newer than its last round), or live agents still awaiting their restart check
    /// <see cref="RestartCheckGrace"/> after the scheduler started. Polling is the designed fallback, a provider never
    /// synced is not a problem, a job never started or a lock not taken is how every test host runs.
    /// </summary>
    internal static SystemHealthStates RollUp(DatabaseHealth database, SchedulerStatus scheduler, IReadOnlyList<StreamStatus> market, IReadOnlyList<StreamStatus> userData,
        IReadOnlyList<ProviderSyncView> sync, string? syncError, ModelSyncJobStatus job, RateLimitSnapshot limits, ReconciliationStatus reconciliation, InstanceLockStatus instanceLock, DateTimeOffset now)
    {
        var tickStale = scheduler.StartedAt is { } started && now - started > TickStaleAfter && (scheduler.LastTickAt is not { } tick || now - tick > TickStaleAfter);
        var checksLate = reconciliation.Available && reconciliation.AwaitingCheck > 0 && scheduler.StartedAt is { } since && now - since > RestartCheckGrace;
        var reconciliationFailing = reconciliation.Available
            && (reconciliation.LastRoundFailures > 0 || reconciliation.LastErrorAt is { } failed && (reconciliation.LastRoundAt is not { } round || failed > round));
        var syncOverdue = job.NextRunAt is { } due && now - due > ModelSyncOverdueGrace && (job.RunningSince is not { } running || now - running > ModelSyncPassStuckAfter);

        return new SystemHealthStates(
            Database: database.State,
            InstanceLock: instanceLock.State switch
            {
                InstanceLockState.Lost => HealthState.Unhealthy,
                InstanceLockState.Reacquiring => HealthState.Degraded,
                _ => HealthState.Healthy,
            },
            Scheduler: scheduler.Phase switch
            {
                SchedulerPhase.NotStarted or SchedulerPhase.Stopped => HealthState.Unhealthy,
                SchedulerPhase.Starting => HealthState.Degraded,
                _ => tickStale ? HealthState.Degraded : HealthState.Healthy,
            },
            MarketStreams: Streams(market),
            UserDataStreams: Streams(userData),
            ModelSync: syncError is not null || sync.Any(s => s.LastStatus == SyncStatus.Failed) || syncOverdue || job.LastRunFailures > 0 ? HealthState.Degraded : HealthState.Healthy,
            RateLimits: limits.RetryAfterUntil > now ? HealthState.Degraded : HealthState.Healthy,
            Reconciliation: checksLate || reconciliationFailing ? HealthState.Degraded : HealthState.Healthy);
    }

    private static HealthState Streams(IReadOnlyList<StreamStatus> streams) =>
        streams.Any(s => s.Health == StreamHealth.Down) ? HealthState.Degraded : HealthState.Healthy;

    /// <summary>
    /// One connection, one question, under <see cref="ProbeTimeout"/>. The failure's kind is reported, never its message
    /// (an Npgsql message can name the host, the database or the user, and every signed-in user sees the page); the
    /// log carries the exception when the outcome changes. A connection attempt the provider refused silently answers
    /// "did not answer" (<c>CanConnectAsync</c> swallows the cause); one that hung answers the timeout.
    /// </summary>
    private async Task<DatabaseHealth> ProbeDatabaseAsync()
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, _time);
        var started = _time.GetTimestamp();
        try
        {
            await using var db = await _factory.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            var answered = await db.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false);
            var latency = _time.GetElapsedTime(started);
            if (answered)
            {
                Outcome(ref _probeFailure, null, null, "The database probe");
                return new DatabaseHealth(HealthState.Healthy, latency, null);
            }

            Outcome(ref _probeFailure, DidNotAnswer, null, "The database probe");
            return new DatabaseHealth(HealthState.Unhealthy, latency, DidNotAnswer);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Outcome(ref _probeFailure, NoAnswer, null, "The database probe");
            return new DatabaseHealth(HealthState.Unhealthy, ProbeTimeout, NoAnswer);
        }
        catch (Exception ex)
        {
            var kind = ErrorSummary.Describe(ex);
            Outcome(ref _probeFailure, kind, ex, "The database probe");
            return new DatabaseHealth(HealthState.Unhealthy, _time.GetElapsedTime(started), kind);
        }
    }

    /// <summary>The same bound and the same kind-not-message rule as the probe: a read that hangs would otherwise pin the shared report.</summary>
    private async Task<(IReadOnlyList<ProviderSyncView> States, string? Error)> ReadSyncStatesAsync()
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, _time);
        try
        {
            var states = await _catalog.GetSyncStatesAsync(timeout.Token).ConfigureAwait(false);
            Outcome(ref _syncReadFailure, null, null, "The model sync states read");
            return (states, null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Outcome(ref _syncReadFailure, NoAnswer, null, "The model sync states read");
            return ([], NoAnswer);
        }
        catch (Exception ex)
        {
            var kind = ErrorSummary.Describe(ex);
            Outcome(ref _syncReadFailure, kind, ex, "The model sync states read");
            return ([], kind);
        }
    }

    /// <summary>
    /// Edge-triggered: a read that starts failing is a Warning with the detail, one that keeps failing the same way is
    /// Debug (an anonymous client can ask for a report as often as the previous one completes, and a status page left
    /// open asks every minute), one that answers again is Information.
    /// </summary>
    private void Outcome(ref string? last, string? failure, Exception? exception, string what)
    {
        if (failure is null)
        {
            if (last is not null)
                _logger.LogInformation("{What} answers again", what);
            last = null;
            return;
        }

        if (failure == last)
        {
            _logger.LogDebug(exception, "{What} still fails: {Kind}", what, failure);
            return;
        }

        _logger.LogWarning(exception, "{What} failed: {Kind}", what, failure);
        last = failure;
    }
}
