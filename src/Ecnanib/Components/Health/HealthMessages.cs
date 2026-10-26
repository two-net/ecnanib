using System.Globalization;
using System.Net;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Configuration;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Components.Health;

/// <summary>User-facing texts of the system status page (NFR-OBS-01, FR-UI-01).</summary>
internal static class HealthMessages
{
    public const string Intro = "Health only: whether the database answers and this is its one instance, how the exchange streams are doing, what the agent scheduler and the exchange reconciliation report and when each provider's model catalog was last synced. No user data.";

    public const string ReconciliationUnavailable = "Live execution is not registered.";

    public const string ReloadHint = "This page does not reload by itself.";

    public const string ReloadingHint = "This page reloads every minute.";

    public const string StartReloading = "Reload every minute";

    public const string StopReloading = "Stop reloading";

    public const string UserDataNote = "Account streams carry fills for live agents; they are counted here, not named, because their names carry key ids.";

    public const string MarketStreamsNote = "Grouped by kind, environment and market: which symbols are followed is the agents' owners' business, not this page's (NFR-SEC-03).";

    public static string Describe(HealthState state) => state switch
    {
        HealthState.Healthy => "Healthy",
        HealthState.Degraded => "Degraded",
        HealthState.Unhealthy => "Unhealthy",
        _ => state.ToString(),
    };

    public static string StateClass(HealthState state) => "status-" + state.ToString().ToLowerInvariant();

    public static string StreamClass(StreamHealth health) => "status-" + health.ToString().ToLowerInvariant();

    public static string Stream(StreamHealth health) => health switch
    {
        StreamHealth.Connected => "connected",
        StreamHealth.Polling => "polling",
        StreamHealth.Down => "down",
        StreamHealth.Stopped => "stopped",
        _ => health.ToString().ToLowerInvariant(),
    };

    public static string Phase(SchedulerPhase phase) => phase switch
    {
        SchedulerPhase.NotStarted => "Not started",
        SchedulerPhase.Starting => "Starting",
        SchedulerPhase.Running => "Running",
        SchedulerPhase.Stopped => "Stopped",
        _ => phase.ToString(),
    };

    public static string PhaseClass(SchedulerPhase phase) => phase switch
    {
        SchedulerPhase.Running => "status-running",
        SchedulerPhase.Starting => "status-starting",
        SchedulerPhase.Stopped => "status-stopped",
        _ => "status-notstarted",
    };

    /// <summary>"never", "just now", "42 s ago", "2 min ago", "3 h ago" or "2 d ago".</summary>
    public static string Age(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } when)
            return "never";
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromSeconds(10))
            return "just now";
        return Span(elapsed) + " ago";
    }

    /// <summary>"42 s", "2 min", "3 h" or "2 d".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return $"{(int)span.TotalSeconds} s";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1))
            return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }

    /// <summary>"not scheduled", "in 4 h", "due now" (within ten seconds either way) or "overdue by 5 min".</summary>
    public static string Due(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } due)
            return "not scheduled";
        var left = due - now;
        if (left.Duration() < TimeSpan.FromSeconds(10))
            return "due now";
        return left > TimeSpan.Zero ? $"in {Span(left)}" : $"overdue by {Span(-left)}";
    }

    /// <summary>
    /// The scheduled sync's heartbeat (FR-SYNC-02, NFR-OBS-01): "not started"; "started 2 min ago, first run in 1 min";
    /// "running for 20 s"; or "last run 2 h ago (3 synced, 1 without a key, 0 failed, 0 failure(s)), next run in 4 h";
    /// with "; last error TimeoutException 2 h ago" while one is kept.
    /// </summary>
    public static string ModelSyncJob(ModelSyncJobStatus job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.StartedAt is null)
            return "not started";
        var text = job switch
        {
            { RunningSince: { } running } => $"running for {Span(now - running)}",
            { LastRunAt: { } last } => $"last run {Age(last, now)} ({job.LastRunSynced} synced, {job.LastRunWithoutKey} without a key, {job.LastRunFailed} failed, {job.LastRunFailures} failure(s)), next run {Due(job.NextRunAt, now)}",
            _ => $"started {Age(job.StartedAt, now)}, first run {Due(job.NextRunAt, now)}",
        };
        return job.LastError is { } error ? $"{text}; last error {error} {Age(job.LastErrorAt, now)}" : text;
    }

    /// <summary>"none" or "3 since start, last 2 min ago" (NFR-PERF-01).</summary>
    public static string DeadlineMisses(int misses, DateTimeOffset? lastAt, DateTimeOffset now) =>
        misses == 0 ? "none" : $"{misses} since start, last {Age(lastAt, now)}";

    /// <summary>"none" or "2 recorded as failed at start" (NFR-REL-01).</summary>
    public static string InterruptedAtStart(int count) => count == 0 ? "none" : $"{count} recorded as failed at start";

    /// <summary>"held since 2026-01-05 12:00:00 UTC (backend 4242), checked just now", "re-acquiring: …", "lost: …" or "not held" (NFR-REL-04).</summary>
    public static string InstanceLock(InstanceLockStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            InstanceLockState.Held => $"held since {Stamp(status.HeldSince)}" + (status.BackendPid is { } pid ? $" (backend {pid})" : "") + $", checked {Age(status.LastCheckedAt ?? status.HeldSince, now)}",
            InstanceLockState.Reacquiring => "re-acquiring" + (status.LastError is { } error ? $": {error}" : ""),
            InstanceLockState.Lost => "lost: another instance holds it; this instance is stopping",
            _ => "not held",
        };
    }

    public static string InstanceLockClass(InstanceLockState state) => state switch
    {
        InstanceLockState.Held => "status-healthy",
        InstanceLockState.Lost => "status-unhealthy",
        _ => "status-degraded",
    };

    /// <summary>"2 agent(s) verified, 1 awaiting" (NFR-REL-01).</summary>
    public static string RestartChecks(ReconciliationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return $"{status.CheckedAgents} agent(s) verified, {status.AwaitingCheck} awaiting";
    }

    /// <summary>"none" or "3 since start, last 2 min ago" (FR-EXE-08).</summary>
    public static string Adoptions(ReconciliationStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Adoptions == 0 ? "none" : $"{status.Adoptions} since start, last {Age(status.LastAdoptionAt, now)}";
    }

    /// <summary>"never", "2 min ago, 3 agent(s)" or "2 min ago, 3 agent(s), 1 failed".</summary>
    public static string LastRound(ReconciliationStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.LastRoundAt is null)
            return "never";
        return $"{Age(status.LastRoundAt, now)}, {status.LastRoundAgents} agent(s)" + (status.LastRoundFailures > 0 ? $", {status.LastRoundFailures} failed" : "");
    }

    /// <summary>"—", "12 ms" or "1.2 s".</summary>
    public static string Latency(TimeSpan? latency) => latency switch
    {
        null => "—",
        { TotalSeconds: >= 1 } l => l.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s",
        { } l => Math.Max(0, (int)Math.Round(l.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + " ms",
    };

    /// <summary>"none" or "3 connected, 1 polling, 0 down, 0 stopped".</summary>
    public static string Streams(IReadOnlyList<StreamStatus> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
            return "none";
        return $"{streams.Count(s => s.Health == StreamHealth.Connected)} connected, {streams.Count(s => s.Health == StreamHealth.Polling)} polling, "
               + $"{streams.Count(s => s.Health == StreamHealth.Down)} down, {streams.Count(s => s.Health == StreamHealth.Stopped)} stopped";
    }

    /// <summary>"https://ecnanib.example.com: https, so …" or "http://localhost:5207: plain http, allowed for this machine only" (NFR-SEC-02).</summary>
    public static string PublicOrigin(ServerOptions server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.IsHttps
            ? $"{server.PublicUrl}: https, so cookies are Secure, HSTS is sent and a plain request is redirected"
            : $"{server.PublicUrl}: plain http, allowed for this machine only";
    }

    /// <summary>"https from 203.0.113.9": the scheme and client address this request arrived with, after the trusted proxy's forwarded headers.</summary>
    public static string RequestTransport(bool isHttps, IPAddress? client) =>
        $"{(isHttps ? "https" : "http")} from {client?.ToString() ?? "an unknown address"}";

    /// <summary>"loopback only" or "loopback and 2 configured".</summary>
    public static string TrustedProxies(ServerOptions server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var configured = server.TrustedProxies?.Length ?? 0;
        return configured == 0 ? "loopback only" : $"loopback and {configured} configured";
    }

    /// <summary>
    /// One row per kind, environment and market (the first three segments of a stream name): how many streams, the worst
    /// health among them, the latest message, the earliest start and the first recorded error. Symbols stay out (NFR-SEC-03).
    /// </summary>
    public static IReadOnlyList<StreamGroup> GroupStreams(IReadOnlyList<StreamStatus> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        return streams
            .GroupBy(s => string.Join(':', s.Name.Split(':').Take(3)), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StreamGroup(
                g.Key,
                g.Count(),
                g.Select(s => s.Health).MaxBy(Severity),
                g.Max(s => s.LastMessageAt),
                g.Min(s => s.Since),
                g.Select(s => s.LastError).FirstOrDefault(e => e is not null)))
            .ToList();
    }

    private static int Severity(StreamHealth health) => health switch
    {
        StreamHealth.Down => 3,
        StreamHealth.Polling => 2,
        StreamHealth.Stopped => 1,
        _ => 0,
    };

    public sealed record StreamGroup(string Name, int Count, StreamHealth Worst, DateTimeOffset? LastMessageAt, DateTimeOffset Since, string? LastError);

    public static string Stamp(DateTimeOffset? at) => UtcStamp.Second(at);
}
