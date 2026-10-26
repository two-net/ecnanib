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

/// <summary>
/// One state per part of the report, by the rules the health README lists, so the page and the document can name the
/// part that degrades the whole; <see cref="Overall"/> is the worst of them.
/// </summary>
public sealed record SystemHealthStates(
    HealthState Database,
    HealthState InstanceLock,
    HealthState Scheduler,
    HealthState MarketStreams,
    HealthState UserDataStreams,
    /// <summary>The sync per provider and the scheduled job's heartbeat together.</summary>
    HealthState ModelSync,
    HealthState RateLimits,
    HealthState Reconciliation)
{
    public static readonly SystemHealthStates AllHealthy = new(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy);

    /// <summary>The worst of the parts; the enum's order (Healthy, Degraded, Unhealthy) is the severity.</summary>
    public HealthState Overall => new[] { Database, InstanceLock, Scheduler, MarketStreams, UserDataStreams, ModelSync, RateLimits, Reconciliation }.Max();

    public static HealthState Worst(HealthState first, HealthState second) => first > second ? first : second;
}

/// <summary>The health status of NFR-OBS-01: database connectivity and the instance lock, exchange streams, the agent scheduler, the exchange reconciliation and the last model sync per provider with the sync job's heartbeat.</summary>
public sealed record SystemHealthReport(
    DateTimeOffset At,
    /// <summary>The worst of <see cref="States"/>.</summary>
    HealthState Overall,
    DatabaseHealth Database,
    IReadOnlyList<StreamStatus> MarketStreams,
    IReadOnlyList<StreamStatus> UserDataStreams,
    SchedulerStatus Scheduler,
    IReadOnlyList<ProviderSyncView> ModelSync,
    /// <summary>Why the sync states could not be read, as a kind, when the database refused or did not answer that query.</summary>
    string? ModelSyncError,
    RateLimitSnapshot RateLimits,
    /// <summary>The execution module's reconciliation and restart checks (FR-EXE-08, NFR-REL-01); unavailable without it.</summary>
    ReconciliationStatus Reconciliation,
    /// <summary>The database's instance lock (NFR-REL-04).</summary>
    InstanceLockStatus InstanceLock,
    /// <summary>The scheduled model sync's heartbeat (FR-SYNC-02).</summary>
    ModelSyncJobStatus ModelSyncJob,
    /// <summary>One state per part; <see cref="Overall"/> is their worst.</summary>
    SystemHealthStates States);

/// <summary>Gathers the health report; never throws for a failing component, which is reported as such.</summary>
public interface ISystemHealth
{
    Task<SystemHealthReport> GetAsync(CancellationToken cancellationToken);
}
