using Ecnanib.Exchange;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.DependencyInjection;

public class AddExchangeTests
{
    private static readonly ExchangeKeyHandle Handle = new("key-1", ExchangeEnvironment.Testnet, new ExchangeCredentials("abcdefgh12345", "s3cr3tvalue"));

    private static ServiceProvider Build(TestLoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddExchange(configuration);
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ResolvesTheModuleWithoutContactingTheExchange()
    {
        await using var provider = Build();

        var factory = provider.GetRequiredService<IExchangeClientFactory>();
        Assert.NotNull(provider.GetRequiredService<IMarketStreams>());
        Assert.NotNull(provider.GetRequiredService<IExchangeHealth>());
        Assert.Equal(3, provider.GetServices<IHostedService>().Count());

        var production = factory.GetPublicData(ExchangeEnvironment.Production, MarketType.Spot);
        var testnet = factory.GetPublicData(ExchangeEnvironment.Testnet, MarketType.Spot);
        Assert.NotSame(production, testnet);
        Assert.Same(production, factory.GetPublicData(ExchangeEnvironment.Production, MarketType.Spot));
        Assert.Equal(ExchangeEnvironment.Testnet, testnet.Environment);

        var health = provider.GetRequiredService<IExchangeHealth>();
        Assert.Empty(health.MarketStreams);
        Assert.Empty(health.UserDataStreams);
        Assert.Empty(health.RateLimits.Usages);
    }

    [Fact]
    public async Task AccountClientsAreCachedPerKeyAndReleased()
    {
        await using var provider = Build();
        var factory = provider.GetRequiredService<IExchangeClientFactory>();

        var spot = factory.GetSpotClient(Handle);
        var futures = factory.GetFuturesClient(Handle);
        Assert.Same(spot, factory.GetSpotClient(Handle));
        Assert.Equal("key-1", spot.KeyId);
        Assert.Equal(MarketType.UsdFutures, futures.Market);
        Assert.Equal(StreamHealth.Stopped, spot.UserDataStatus.Health);

        var rotated = new ExchangeKeyHandle("key-1", ExchangeEnvironment.Testnet, new ExchangeCredentials("zzzzzzzz99999", "other"));
        Assert.Throws<InvalidOperationException>(() => factory.GetSpotClient(rotated));
        var otherEnvironment = new ExchangeKeyHandle("key-1", ExchangeEnvironment.Production, Handle.Credentials);
        Assert.Throws<InvalidOperationException>(() => factory.GetFuturesClient(otherEnvironment));

        await factory.ReleaseAsync("key-1", TestContext.Current.CancellationToken);
        await factory.ReleaseAsync("unknown", TestContext.Current.CancellationToken);
        Assert.NotSame(spot, factory.GetSpotClient(rotated));
    }

    [Fact]
    public async Task TestnetPermissionsAreUnverifiedWithoutACall()
    {
        await using var provider = Build();
        var client = provider.GetRequiredService<IExchangeClientFactory>().GetSpotClient(Handle);

        var permissions = await client.GetApiKeyPermissionsAsync(TestContext.Current.CancellationToken);

        Assert.False(permissions.Verified);
    }

    [Fact]
    public async Task RegistryNeverLogsSecrets()
    {
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs);
        var registry = provider.GetRequiredService<IBinanceClientRegistry>();

        var rest = registry.GetKeyedRest(Handle);
        Assert.Same(rest, registry.GetKeyedRest(Handle));
        Assert.True(registry.HasKey("key-1"));
        registry.GetPublicRest(ExchangeEnvironment.Production);
        await registry.ReleaseKeyAsync("key-1", TestContext.Current.CancellationToken);
        Assert.False(registry.HasKey("key-1"));

        Assert.Contains(logs.Entries, e => e.Message.Contains("…2345"));
        Assert.DoesNotContain("s3cr3tvalue", logs.AllText);
        Assert.DoesNotContain("abcdefgh12345", logs.AllText);
    }

    [Fact]
    public async Task TheApiKeyHeaderIsRedactedFromTheHttpClientLogs()
    {
        await using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(BinanceClientRegistry.HttpClientName);

        Assert.True(options.ShouldRedactHeaderValue("X-MBX-APIKEY"));
        Assert.True(options.ShouldRedactHeaderValue("x-mbx-apikey"));
        Assert.False(options.ShouldRedactHeaderValue("Content-Type"));
    }

    [Fact]
    public async Task RegistryRejectsMismatchedHandles()
    {
        await using var provider = Build();
        var registry = provider.GetRequiredService<IBinanceClientRegistry>();
        registry.GetKeyedSocket(Handle);

        Assert.Throws<InvalidOperationException>(() => registry.GetKeyedRest(new ExchangeKeyHandle("key-1", ExchangeEnvironment.Production, Handle.Credentials)));
        Assert.Throws<InvalidOperationException>(() => registry.GetKeyedRest(new ExchangeKeyHandle("key-1", ExchangeEnvironment.Testnet, new ExchangeCredentials("otherkey0000", "x"))));
    }

    [Fact]
    public async Task FuturesClientRefusesConditionalTypesOnThePlainOrderEndpointBeforeSendingAnything()
    {
        await using var provider = Build();
        var client = provider.GetRequiredService<IExchangeClientFactory>().GetFuturesClient(Handle);
        var request = new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "c2", 1m, StopPrice: 1m, ReduceOnly: true);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.PlaceOrderAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("PlaceConditionalOrderAsync", ex.Message);
        Assert.Contains("-4120", ex.Message);
    }

    [Fact]
    public async Task SpotClientRejectsFuturesOnlyFlags()
    {
        await using var provider = Build();
        var client = provider.GetRequiredService<IExchangeClientFactory>().GetSpotClient(Handle);
        var request = new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "c1", 1m, StopPrice: 1m, ReduceOnly: true);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PlaceOrderAsync(request, TestContext.Current.CancellationToken));
    }
}
