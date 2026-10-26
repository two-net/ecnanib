using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.ModelSync.DependencyInjection;

public class AddModelSyncTests
{
    [Fact]
    public async Task ResolvesTheModuleWithOneHostedServiceAndWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = Build(factory, ai);

        Assert.IsType<ProviderCatalogSync>(provider.GetRequiredService<IProviderCatalogSync>());
        Assert.IsType<ModelCatalog>(provider.GetRequiredService<IModelCatalog>());
        var runner = Assert.IsType<ModelSyncRunner>(provider.GetRequiredService<IModelSyncService>());
        Assert.Same(runner, provider.GetRequiredService<ModelSyncRunner>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>().Where(h => h.GetType().Namespace!.StartsWith("Ecnanib.ModelSync", StringComparison.Ordinal)));
        Assert.Same(provider.GetRequiredService<ModelSyncScheduler>(), hosted);
        Assert.Same(hosted, provider.GetRequiredService<IModelSyncStatus>());
        Assert.Equal(ModelSyncJobStatus.NotStarted, provider.GetRequiredService<IModelSyncStatus>().GetStatus());
        Assert.Equal(0, factory.Calls);
        Assert.Empty(ai.Listings);
    }

    [Fact]
    public async Task BindsTheProvidersSectionOnItsOwn()
    {
        await using var provider = Build(new ThrowingDbContextFactory(), new FakeAiClientFactory(),
            new() { ["Providers:ModelSyncIntervalMinutes"] = "15", ["Providers:DeprecationMissThreshold"] = "2" });

        var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(15), options.ModelSyncInterval);
        Assert.Equal(2, options.DeprecationMissThreshold);
    }

    [Fact]
    public async Task ModelsOfAnotherProviderAreRejectedBeforeAnyDatabaseCall()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = Build(factory, new FakeAiClientFactory());
        var sync = provider.GetRequiredService<IProviderCatalogSync>();

        await Assert.ThrowsAsync<ArgumentException>(() => sync.SyncAsync(AiProvider.Claude, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-x")], SyncTrigger.Manual, DatabaseTestHost.Ct));

        Assert.Equal(0, factory.Calls);
    }

    private static ServiceProvider Build(ThrowingDbContextFactory factory, FakeAiClientFactory ai, Dictionary<string, string?>? config = null) =>
        DatabaseTestHost.BuildServices(configure: s =>
        {
            s.AddModelSync(new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
            s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
            s.AddSingleton<IAiClientFactory>(ai);
        });
}
