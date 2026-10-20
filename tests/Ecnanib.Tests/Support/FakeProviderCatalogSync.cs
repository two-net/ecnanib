using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Records catalog syncs and failures instead of writing them; <see cref="Failure"/> makes every call throw.</summary>
internal sealed class FakeProviderCatalogSync : IProviderCatalogSync
{
    public List<(AiProvider Provider, IReadOnlyList<AiModelInfo> Models, SyncTrigger Trigger)> Calls { get; } = [];

    public List<(AiProvider Provider, SyncTrigger Trigger, string Error)> RecordedFailures { get; } = [];

    public Exception? Failure { get; set; }

    public Task<CatalogSyncResult> SyncAsync(AiProvider provider, IReadOnlyList<AiModelInfo> models, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        lock (Calls)
            Calls.Add((provider, models, trigger));
        if (Failure is not null)
            throw Failure;
        return Task.FromResult(new CatalogSyncResult(provider, models.Count, 0, models.Count, DateTimeOffset.UnixEpoch));
    }

    public Task RecordFailureAsync(AiProvider provider, SyncTrigger trigger, string error, CancellationToken cancellationToken)
    {
        lock (RecordedFailures)
            RecordedFailures.Add((provider, trigger, error));
        if (Failure is not null)
            throw Failure;
        return Task.CompletedTask;
    }
}
