using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ModelSync.Services;

/// <summary>The runner's guards; infrastructure failures are exceptions, provider failures are outcomes (live tests).</summary>
public class ModelSyncRunnerTests
{
    [Fact]
    public async Task KeyValidationIsNotARunnerTrigger()
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = Build(factory, ai);
        var runner = provider.GetRequiredService<ModelSyncRunner>();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.SyncAsync(AiProvider.Claude, SyncTrigger.KeyValidation, null, KeysTestHost.Ct));

        Assert.Equal(0, factory.Calls);
        Assert.Empty(ai.Listings);
    }

    [Fact]
    public async Task AnUnknownProviderIsRejectedBeforeAnyDatabaseCall()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = Build(factory, new FakeAiClientFactory());
        var runner = provider.GetRequiredService<ModelSyncRunner>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.SyncAsync((AiProvider)99, SyncTrigger.Scheduled, null, KeysTestHost.Ct));

        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ADatabaseFailurePropagatesFromAManualSync()
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = Build(factory, ai);
        var sync = provider.GetRequiredService<IModelSyncService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncNowAsync(AiProvider.Gpt, Guid.NewGuid(), KeysTestHost.Ct));

        Assert.Equal("database down", ex.Message);
        Assert.Equal(1, factory.Calls);
        Assert.Empty(ai.Listings);
    }

    private static ServiceProvider Build(ThrowingDbContextFactory factory, FakeAiClientFactory ai) =>
        KeysTestHost.BuildServices(ai: ai, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
}
