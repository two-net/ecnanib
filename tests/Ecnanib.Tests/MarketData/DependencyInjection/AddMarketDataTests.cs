using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData;
using Ecnanib.MarketData.Abstractions;
using Ecnanib.MarketData.Configuration;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.MarketData.DependencyInjection;

public class AddMarketDataTests
{
    [Fact]
    public async Task RegisteredBeforeAddAgentsTheModuleReplacesThePlaceholdersWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        var streams = new FakeMarketStreams();
        await using var provider = MarketDataTestHost.BuildServices(exchange: exchange, streams: streams, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<SymbolCatalog>(provider.GetRequiredService<ISymbolCatalog>());
        Assert.IsType<CandleFeed>(provider.GetRequiredService<IMarketDataLoader>());
        Assert.Same(provider.GetRequiredService<CandleFeed>(), provider.GetRequiredService<IMarketDataLoader>());
        Assert.IsType<FuturesMetrics>(provider.GetRequiredService<IFuturesMetrics>());
        Assert.Same(provider.GetRequiredService<SymbolCatalog>(), provider.GetRequiredService<ISymbolCatalog>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.MarketData", StringComparison.Ordinal));
        Assert.Same(provider.GetRequiredService<SymbolCatalogRefresher>(), hosted);

        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.ExchangeInfoReads);
        Assert.Empty(exchange.RecentCandleReads);
        Assert.Equal(0, streams.CandleSubscriptionsEver);
        Assert.Equal(0, streams.MarkSubscriptionsEver);
    }

    [Fact]
    public async Task BindsAndValidatesTheMarketDataSectionAndReadsTheRetentionPeriod()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString,
            ["MarketData:BackfillCandles:4H"] = "321",
            ["Retention:CandleDays"] = "42",
        }).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddMarketData(configuration);
        services.AddSingleton<IExchangeClientFactory>(new FakeExchangeClientFactory());
        services.AddSingleton<IMarketStreams>(new FakeMarketStreams());
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(321, provider.GetRequiredService<IOptions<MarketDataOptions>>().Value.BackfillFor(CandleInterval.FourHours));
        Assert.Equal(TimeSpan.FromDays(42), provider.GetRequiredService<IOptions<RetentionOptions>>().Value.CandleRetention);
        Assert.Contains(provider.GetServices<IValidateOptions<MarketDataOptions>>(), v => v is MarketDataOptionsValidator);
    }
}
