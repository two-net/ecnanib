using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Health.Abstractions;

/// <summary>
/// The health status as <c>GET /health</c> answers it (NFR-OBS-01): states and times only. Never a stream name (it
/// carries a symbol), a key id, an error text, a count of agents or the transport, because the document is anonymous;
/// the signed-in status page has the detail. Serialised with camelCase names and lowerCamel enum values.
/// </summary>
public sealed record HealthDocument(
    HealthState Status,
    DateTimeOffset At,
    DatabaseDocument Database,
    InstanceLockDocument InstanceLock,
    SchedulerDocument Scheduler,
    StreamsDocument MarketStreams,
    StreamsDocument AccountStreams,
    ModelSyncDocument ModelSync,
    RateLimitsDocument RateLimits,
    ReconciliationDocument Reconciliation)
{
    public static HealthDocument From(SystemHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var states = report.States;
        var job = report.ModelSyncJob;
        return new HealthDocument(
            report.Overall,
            report.At,
            new DatabaseDocument(states.Database, report.Database is { State: HealthState.Healthy, Latency: { } latency } ? Math.Max(0, (int)Math.Round(latency.TotalMilliseconds)) : null),
            new InstanceLockDocument(states.InstanceLock, report.InstanceLock.State),
            new SchedulerDocument(states.Scheduler, report.Scheduler.Phase, report.Scheduler.StartedAt, report.Scheduler.LastTickAt),
            StreamsDocument.From(states.MarketStreams, report.MarketStreams),
            StreamsDocument.From(states.UserDataStreams, report.UserDataStreams),
            new ModelSyncDocument(
                states.ModelSync,
                report.ModelSync.Select(s => new ProviderSyncDocument(s.Provider, s.LastStatus, s.LastSyncAt)).ToList(),
                new ModelSyncJobDocument(job.StartedAt, job.RunningSince, job.LastRunAt, job.NextRunAt)),
            new RateLimitsDocument(states.RateLimits, report.RateLimits.RetryAfterUntil),
            new ReconciliationDocument(states.Reconciliation, report.Reconciliation.Available, report.Reconciliation.LastRoundAt));
    }
}

/// <summary><see cref="LatencyMs"/> is the probe's answer time, rounded; null when the database did not answer (the page shows how long the failure took, a monitor's graph must not).</summary>
public sealed record DatabaseDocument(HealthState Status, int? LatencyMs);

public sealed record InstanceLockDocument(HealthState Status, InstanceLockState State);

public sealed record SchedulerDocument(HealthState Status, SchedulerPhase Phase, DateTimeOffset? StartedAt, DateTimeOffset? LastTickAt);

/// <summary>How many streams are in each health, never which (NFR-SEC-03).</summary>
public sealed record StreamsDocument(HealthState Status, int Connected, int Polling, int Down, int Stopped)
{
    public static StreamsDocument From(HealthState status, IReadOnlyList<StreamStatus> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        return new StreamsDocument(status,
            streams.Count(s => s.Health == StreamHealth.Connected),
            streams.Count(s => s.Health == StreamHealth.Polling),
            streams.Count(s => s.Health == StreamHealth.Down),
            streams.Count(s => s.Health == StreamHealth.Stopped));
    }
}

/// <summary>The last sync per provider (FR-SYNC-06) and the scheduled job's heartbeat (FR-SYNC-02).</summary>
public sealed record ModelSyncDocument(HealthState Status, IReadOnlyList<ProviderSyncDocument> Providers, ModelSyncJobDocument Job);

public sealed record ProviderSyncDocument(AiProvider Provider, SyncStatus Status, DateTimeOffset? LastSyncAt);

public sealed record ModelSyncJobDocument(DateTimeOffset? StartedAt, DateTimeOffset? RunningSince, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

/// <summary><see cref="RetryAfterUntil"/> is set while an exchange rate limit holds requests back.</summary>
public sealed record RateLimitsDocument(HealthState Status, DateTimeOffset? RetryAfterUntil);

/// <summary><see cref="Available"/> is false without the execution module (FR-EXE-08).</summary>
public sealed record ReconciliationDocument(HealthState Status, bool Available, DateTimeOffset? LastRoundAt);
