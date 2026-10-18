using Ecnanib.Exchange;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.Configuration;

public class ExchangeOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValidAndMatchBinanceProduction()
    {
        var options = Resolve([]);

        Assert.Equal("https://api.binance.com", options.Production.SpotRest);
        Assert.Equal("https://demo-fapi.binance.com", options.Testnet.FuturesRest);
        Assert.Equal(TimeSpan.FromSeconds(15), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ReconciliationInterval);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new()
        {
            ["Exchange:RequestTimeoutSeconds"] = "30",
            ["Exchange:Testnet:FuturesRest"] = "https://testnet.binancefuture.com",
        });

        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal("https://testnet.binancefuture.com", options.Testnet.FuturesRest);
        Assert.Equal("https://testnet.binance.vision", options.Testnet.SpotRest);
    }

    [Theory]
    [InlineData("Exchange:RequestTimeoutSeconds", "0", "RequestTimeoutSeconds")]
    [InlineData("Exchange:Production:SpotRest", "http://api.binance.com", "Exchange:Production:SpotRest must use the https scheme")]
    [InlineData("Exchange:Production:SpotStreams", "https://stream.binance.com", "Exchange:Production:SpotStreams must use the wss scheme")]
    [InlineData("Exchange:Testnet:FuturesRest", "not a url", "Exchange:Testnet:FuturesRest must be an absolute URL")]
    [InlineData("Exchange:Testnet:FuturesWsApi", "https://x.example", "Exchange:Testnet:FuturesWsApi must use the wss scheme")]
    [InlineData("Exchange:Streams:MarkPriceUpdateIntervalMs", "2000", "MarkPriceUpdateIntervalMs must be 1000 or 3000")]
    [InlineData("Exchange:Streams:ReconnectMaxDelaySeconds", "1", "ReconnectMaxDelaySeconds must be >= ReconnectInitialDelaySeconds")]
    [InlineData("Exchange:Retry:MaxAttempts", "0", "Exchange:Retry:MaxAttempts must be > 0")]
    [InlineData("Exchange:Testnet:SpotRest", "https://api.binance.com", "Exchange:Testnet:SpotRest must differ")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void EmptyFuturesWsApiIsAllowed()
    {
        var options = Resolve(new() { ["Exchange:Production:FuturesWsApi"] = "" });
        Assert.True(string.IsNullOrEmpty(options.Production.FuturesWsApi));
    }

    private static ExchangeOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddExchange(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ExchangeOptions>>().Value;
    }
}
