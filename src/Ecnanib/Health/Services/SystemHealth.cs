using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Health.Services;

/// <summary>
/// One report per call: a bounded database probe, the exchange's own stream and rate-limit view, the scheduler's
/// status and the model sync states, rolled up into one state. A component that fails is reported as failed; only
/// the caller's cancellation propagates.
/// </summary>
internal sealed class SystemHealth : ISystemHealth
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Three minute ticks without one completing means the scheduler's loops are stuck.</summary>
    internal static readonly TimeSpan TickStaleAfter = TimeSpan.FromMinutes(3);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IExchangeHealth _exchange;
    private readonly ISchedulerStatus _scheduler;
    private readonly IModelCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemHealth> _logger;

    public SystemHealth(IDbContextFactory<EcnanibDbContext> factory, IExchangeHealth exchange, ISchedulerStatus scheduler, IModelCatalog catalog, TimeProvider time, ILogger<SystemHealth> logger)
    {
        _factory = factory;
        _exchange = exchange;
        _scheduler = scheduler;
        _catalog = catalog;
        _time = time;
        _logger = logger;
    }

    public async Task<SystemHealthReport> GetAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var database = await ProbeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ProviderSyncView> sync = [];
        string? syncError = null;
        try
        {
            sync = await _catalog.GetSyncStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            syncError = ex.Message;
            _logger.LogDebug(ex, "The model sync states could not be read for the health report");
        }

        var scheduler = _scheduler.GetStatus();
        var market = _exchange.MarketStreams;
        var userData = _exchange.UserDataStreams;
        var limits = _exchange.RateLimits;
        var overall = RollUp(database, scheduler, market, userData, sync, syncError, limits, now);
        return new SystemHealthReport(now, overall, database, market, userData, scheduler, sync, syncError, limits);
    }

    /// <summary>
    /// The worst wins: an unreachable database or a scheduler that is not running is Unhealthy (nothing is being
    /// evaluated); a scheduler still starting or without a tick for <see cref="TickStaleAfter"/>, a stream that is Down
    /// (polling failed too), a provider whose last sync failed, unreadable sync states or an exchange rate limit in
    /// force is Degraded. Polling is the designed fallback and a provider never synced is not a problem.
    /// </summary>
    internal static HealthState RollUp(DatabaseHealth database, SchedulerStatus scheduler, IReadOnlyList<StreamStatus> market, IReadOnlyList<StreamStatus> userData,
        IReadOnlyList<ProviderSyncView> sync, string? syncError, RateLimitSnapshot limits, DateTimeOffset now)
    {
        if (database.State == HealthState.Unhealthy)
            return HealthState.Unhealthy;
        if (scheduler.Phase is SchedulerPhase.NotStarted or SchedulerPhase.Stopped)
            return HealthState.Unhealthy;

        var tickStale = scheduler.StartedAt is { } started && now - started > TickStaleAfter && (scheduler.LastTickAt is not { } tick || now - tick > TickStaleAfter);
        var degraded = scheduler.Phase == SchedulerPhase.Starting
            || tickStale
            || market.Concat(userData).Any(s => s.Health == StreamHealth.Down)
            || sync.Any(s => s.LastStatus == SyncStatus.Failed)
            || syncError is not null
            || limits.RetryAfterUntil > now;
        return degraded ? HealthState.Degraded : HealthState.Healthy;
    }

    private async Task<DatabaseHealth> ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = _time.GetTimestamp();
        try
        {
            await using var db = await _factory.CreateDbContextAsync(linked.Token).ConfigureAwait(false);
            var answered = await db.Database.CanConnectAsync(linked.Token).ConfigureAwait(false);
            var latency = _time.GetElapsedTime(started);
            return answered
                ? new DatabaseHealth(HealthState.Healthy, latency, null)
                : new DatabaseHealth(HealthState.Unhealthy, latency, "The database did not answer.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new DatabaseHealth(HealthState.Unhealthy, ProbeTimeout, $"No answer within {ProbeTimeout.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DatabaseHealth(HealthState.Unhealthy, _time.GetElapsedTime(started), ex.Message);
        }
    }
}
