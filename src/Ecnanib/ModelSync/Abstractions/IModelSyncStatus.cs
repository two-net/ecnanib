namespace Ecnanib.ModelSync.Abstractions;

/// <summary>
/// The scheduled sync's heartbeat for the health status (FR-SYNC-02, NFR-OBS-01): when the job started, whether a pass
/// is in progress, when the last pass ended and what it found, when the next one is due, and the last failure as a
/// kind. A provider without a valid key writes nothing to its sync row, so the rows alone cannot tell a stalled job
/// from a provider nobody holds a key for; this can.
/// </summary>
public sealed record ModelSyncJobStatus(
    /// <summary>When the hosted service started; null in a host that runs no jobs.</summary>
    DateTimeOffset? StartedAt,
    /// <summary>When the pass in progress began; null between passes.</summary>
    DateTimeOffset? RunningSince,
    /// <summary>When the last pass ended, whatever it found.</summary>
    DateTimeOffset? LastRunAt,
    /// <summary>When the next pass is due: the initial delay after the start, the interval after each pass.</summary>
    DateTimeOffset? NextRunAt,
    int LastRunSynced,
    int LastRunWithoutKey,
    /// <summary>Providers whose last pass ended as Failed, recorded on their rows.</summary>
    int LastRunFailed,
    /// <summary>Exceptions outside the provider call, such as the database, in the last pass.</summary>
    int LastRunFailures,
    /// <summary>The last failure's kind (<c>ErrorSummary.Describe</c>), kept until the next one.</summary>
    string? LastError,
    DateTimeOffset? LastErrorAt)
{
    public static readonly ModelSyncJobStatus NotStarted = new(null, null, null, null, 0, 0, 0, 0, null, null);
}

public interface IModelSyncStatus
{
    ModelSyncJobStatus GetStatus();
}
