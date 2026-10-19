using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.ModelSync.DependencyInjection;

public class AddModelSyncTests
{
    [Fact]
    public async Task ResolvesTheCatalogSyncWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = Build(factory);

        Assert.IsType<ProviderCatalogSync>(provider.GetRequiredService<IProviderCatalogSync>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.ModelSync", StringComparison.Ordinal));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ModelsOfAnotherProviderAreRejectedBeforeAnyDatabaseCall()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = Build(factory);
        var sync = provider.GetRequiredService<IProviderCatalogSync>();

        await Assert.ThrowsAsync<ArgumentException>(() => sync.SyncAsync(AiProvider.Claude, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-x")], SyncTrigger.Manual, DatabaseTestHost.Ct));

        Assert.Equal(0, factory.Calls);
    }

    private static ServiceProvider Build(ThrowingDbContextFactory factory) =>
        DatabaseTestHost.BuildServices(configure: s =>
        {
            s.AddModelSync(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
        });
}
