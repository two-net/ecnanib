using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.ModelSync.Abstractions;

namespace Ecnanib.Health.Abstractions;

public enum HealthState
{
    Healthy = 1,
    /// <summary>Everything runs, but something is not as it should be: a stream polling failed, a provider sync failed, the scheduler is still starting.</summary>
    Degraded = 2,
    /// <summary>The database does not answer or the scheduler is not running: agents are not being evaluated.</summary>
    Unhealthy = 3,
}

/// <summary>The outcome of one connectivity probe; <see cref="Latency"/> is how long the answer took.</summary>
public sealed record DatabaseHealth(HealthState State, TimeSpan? Latency, string? Error);

/// <summary>The health status of NFR-OBS-01: database connectivity and the instance lock, exchange streams, the agent scheduler, the exchange reconciliation and the last model sync per provider.</summary>
public sealed record SystemHealthReport(
    DateTimeOffset At,
    HealthState Overall,
    DatabaseHealth Database,
    IReadOnlyList<StreamStatus> MarketStreams,
    IReadOnlyList<StreamStatus> UserDataStreams,
    SchedulerStatus Scheduler,
    IReadOnlyList<ProviderSyncView> ModelSync,
    /// <summary>Why the sync states could not be read, when the database refused that query.</summary>
    string? ModelSyncError,
    RateLimitSnapshot RateLimits,
    /// <summary>The execution module's reconciliation and restart checks (FR-EXE-08, NFR-REL-01); unavailable without it.</summary>
    ReconciliationStatus Reconciliation,
    /// <summary>The database's instance lock (NFR-REL-04).</summary>
    InstanceLockStatus InstanceLock);

/// <summary>Gathers the health report; never throws for a failing component, which is reported as such.</summary>
public interface ISystemHealth
{
    Task<SystemHealthReport> GetAsync(CancellationToken cancellationToken);
}
