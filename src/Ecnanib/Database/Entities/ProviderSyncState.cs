using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>Last catalog sync per provider, shown in the UI and the health status (FR-SYNC-06, NFR-OBS-01).</summary>
public sealed class ProviderSyncState
{
    public AiProvider Provider { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    public SyncStatus LastStatus { get; set; } = SyncStatus.Never;

    public string? LastError { get; set; }

    public int ModelCount { get; set; }

    public SyncTrigger? Trigger { get; set; }
}
