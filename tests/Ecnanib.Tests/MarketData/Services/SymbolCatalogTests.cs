using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.MarketData.Services;

/// <summary>The listing per environment and market: loaded once, shared, refreshed on the schedule and kept through a failed refresh (FR-DATA-01).</summary>
public class SymbolCatalogTests
{
    [Fact]
    public async Task TheFirstUseLoadsTheListingOnceAndLaterReadsAreServedFromIt()
    {
        var exchange = new FakeExchangeClientFactory { OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "ETHUSDT") };
        await using var provider = MarketDataTestHost.BuildServices(exchange: exchange);
        var catalog = provider.GetRequiredService<ISymbolCatalog>();

        var reads = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "btcusdt ", MarketDataTestHost.Ct)));
        var eth = await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT", MarketDataTestHost.Ct);
        var listed = await catalog.ListAsync(ExchangeEnvironment.Production, MarketType.Spot, MarketDataTestHost.Ct);

        Assert.All(reads, r => Assert.Equal("BTCUSDT", r!.Symbol));
        Assert.Equal(0.01m, reads[0]!.Filters.TickSize);
        Assert.Equal("ETHUSDT", eth!.Symbol);
        Assert.Equal(2, listed.Count);
        Assert.Null(await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "DOGEUSDT", MarketDataTestHost.Ct));
        Assert.Single(exchange.ExchangeInfoReads);

        // Another market is another listing.
        await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT", MarketDataTestHost.Ct);
        Assert.Equal(2, exchange.ExchangeInfoReads.Count);
        Assert.Equal([(ExchangeEnvironment.Production, MarketType.Spot), (ExchangeEnvironment.Production, MarketType.UsdFutures)], exchange.ExchangeInfoReads);
    }

    [Fact]
    public async Task AnOutageWithNothingLoadedThrowsAndTheNextCallTriesAgain()
    {
        var exchange = new FakeExchangeClientFactory { OnExchangeInfo = (_, _) => throw new TimeoutException("binance down") };
        await using var provider = MarketDataTestHost.BuildServices(exchange: exchange);
        var catalog = provider.GetRequiredService<ISymbolCatalog>();

        await Assert.ThrowsAsync<TimeoutException>(() => catalog.FindAsync(ExchangeEnvironment.Testnet, MarketType.Spot, "BTCUSDT", MarketDataTestHost.Ct));
        exchange.OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT");

        var found = await catalog.FindAsync(ExchangeEnvironment.Testnet, MarketType.Spot, "BTCUSDT", MarketDataTestHost.Ct);

        Assert.NotNull(found);
        Assert.Equal(2, exchange.ExchangeInfoReads.Count);
    }

    [Fact]
    public async Task ARefreshReplacesTheListingAndAFailedRefreshKeepsIt()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var exchange = new FakeExchangeClientFactory { OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT") };
        await using var provider = MarketDataTestHost.BuildServices(logs: logs, time: time, exchange: exchange);
        var catalog = provider.GetRequiredService<SymbolCatalog>();
        var refresher = provider.GetRequiredService<SymbolCatalogRefresher>();

        Assert.NotNull(await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT", MarketDataTestHost.Ct));
        var loaded = Assert.Single(catalog.States);
        Assert.Equal((ExchangeEnvironment.Production, MarketType.Spot, 1, time.GetUtcNow()), (loaded.Environment, loaded.Market, loaded.Symbols, loaded.LoadedAt));

        time.Advance(TimeSpan.FromMinutes(10));
        exchange.OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "SOLUSDT");
        Assert.Equal(new SymbolRefreshResult(1, 0), await refresher.RefreshOnceAsync(MarketDataTestHost.Ct));
        Assert.NotNull(await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "SOLUSDT", MarketDataTestHost.Ct));
        Assert.Equal(time.GetUtcNow(), Assert.Single(catalog.States).LoadedAt);

        exchange.OnExchangeInfo = (_, _) => throw new TimeoutException("binance down");
        Assert.Equal(new SymbolRefreshResult(0, 1), await refresher.RefreshOnceAsync(MarketDataTestHost.Ct));
        Assert.NotNull(await catalog.FindAsync(ExchangeEnvironment.Production, MarketType.Spot, "SOLUSDT", MarketDataTestHost.Ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Symbol listing of Production Spot could not be refreshed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(3, exchange.ExchangeInfoReads.Count);
    }

    [Fact]
    public async Task TheRefresherRunsAfterTheInitialDelayThenEveryIntervalOverTheLoadedListingsOnly()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var exchange = new FakeExchangeClientFactory { OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT") };
        await using var provider = MarketDataTestHost.BuildServices(logs: logs, time: time, exchange: exchange, config: new() { ["MarketData:SymbolRefreshIntervalMinutes"] = "30" });
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<SymbolCatalogRefresher>());
        var catalog = provider.GetRequiredService<ISymbolCatalog>();

        await hosted.StartAsync(MarketDataTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Empty(exchange.ExchangeInfoReads);

        // Nothing is loaded yet: the first run has nothing to refresh and reads nothing.
        time.Advance(SymbolCatalogRefresher.InitialDelay);
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Message == "Symbol listing refresh checked 0 listing(s): 0 refreshed, 0 failed"), "first run");
        Assert.Empty(exchange.ExchangeInfoReads);

        await catalog.ListAsync(ExchangeEnvironment.Production, MarketType.UsdFutures, MarketDataTestHost.Ct);
        Assert.Single(exchange.ExchangeInfoReads);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Single(exchange.ExchangeInfoReads);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => exchange.ExchangeInfoReads.Count == 2, "second run refreshed the loaded listing");
        Assert.Equal((ExchangeEnvironment.Production, MarketType.UsdFutures), exchange.ExchangeInfoReads[1]);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);

        await hosted.StopAsync(MarketDataTestHost.Ct);
    }
}
