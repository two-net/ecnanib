namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// What the execution module's reconciliation with the exchange reports for the status page (FR-EXE-08, NFR-REL-01,
/// NFR-OBS-01). <see cref="Available"/> is false until the execution module is registered. <see cref="CheckedAgents"/>
/// are the live agents verified against the exchange since this process started and <see cref="AwaitingCheck"/> the
/// ones with open rows still waiting for that first check; <see cref="Adoptions"/> counts the times the exchange's
/// state was adopted over the local one since start.
/// </summary>
public sealed record ReconciliationStatus(
    bool Available,
    /// <summary>When this process's execution module came up: rows written before it belong to a finished or dead evaluation.</summary>
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastRoundAt,
    int LastRoundAgents,
    /// <summary>Keys that could not be read and agents that could not be compared in the last round; the errors themselves are in the log.</summary>
    int LastRoundFailures,
    int CheckedAgents,
    int AwaitingCheck,
    int Adoptions,
    DateTimeOffset? LastAdoptionAt,
    /// <summary>The last read or compare failure, kept until the next one.</summary>
    string? LastError,
    DateTimeOffset? LastErrorAt)
{
    public static readonly ReconciliationStatus Unavailable = new(false, null, null, 0, 0, 0, 0, 0, null, null, null);
}

public interface IReconciliationStatus
{
    ReconciliationStatus GetStatus();
}
