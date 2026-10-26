namespace Ecnanib.Agents.Abstractions;

/// <summary>Where the agent scheduler is in its life (NFR-OBS-01). Starting lasts as long as the database refuses the start-up sweep.</summary>
public enum SchedulerPhase
{
    NotStarted = 1,
    Starting = 2,
    Running = 3,
    Stopped = 4,
}

/// <summary>What the agent scheduler reports about itself for the status page (NFR-OBS-01).</summary>
public sealed record SchedulerStatus(
    SchedulerPhase Phase,
    /// <summary>When the phase became Running.</summary>
    DateTimeOffset? StartedAt,
    /// <summary>The last minute tick that completed (daily-loss resumes, orphan finalisation).</summary>
    DateTimeOffset? LastTickAt,
    int TrackedAgents,
    int InFlightEvaluations,
    int QueuedEvaluations,
    int MaxConcurrentEvaluations,
    /// <summary>The last start-up, tick or loop failure, kept until the next one.</summary>
    string? LastError,
    DateTimeOffset? LastErrorAt,
    /// <summary>Evaluations that missed their deadline since the scheduler started: runs that ended at or past it and candles too late to start (NFR-PERF-01).</summary>
    int DeadlineMisses,
    DateTimeOffset? LastDeadlineMissAt,
    /// <summary>Evaluations in flight at the last stop, recorded as failed by the start-up sweep (NFR-REL-01).</summary>
    int InterruptedAtStart);

public interface ISchedulerStatus
{
    SchedulerStatus GetStatus();
}
