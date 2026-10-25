using System.Globalization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health.Abstractions;

namespace Ecnanib.Components.Health;

/// <summary>User-facing texts of the system status page (NFR-OBS-01, FR-UI-01).</summary>
internal static class HealthMessages
{
    public const string Intro = "Health only: whether the database answers, how the exchange streams are doing, what the agent scheduler reports and when each provider's model catalog was last synced. No user data.";

    public const string RefreshHint = "This page reloads every minute.";

    public const string UserDataNote = "Account streams carry fills for live agents; they are counted here, not named, because their names carry key ids.";

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
        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds} s ago";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours} h ago";
        return $"{(int)elapsed.TotalDays} d ago";
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

    public static string Stamp(DateTimeOffset? at) => at is { } when ? when.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "never";
}
