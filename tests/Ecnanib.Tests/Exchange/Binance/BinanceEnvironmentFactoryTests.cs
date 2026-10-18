using Binance.Net;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.Binance;

public class BinanceEnvironmentFactoryTests
{
    [Fact]
    public void ProductionMatchesLivePreset()
    {
        var env = new BinanceEnvironmentFactory(Options.Create(new ExchangeOptions())).Get(ExchangeEnvironment.Production);
        var live = BinanceEnvironment.Live;

        Assert.Equal(live.SpotRestAddress, env.SpotRestAddress);
        Assert.Equal(live.SpotSocketStreamAddress, env.SpotSocketStreamAddress);
        Assert.Equal(live.SpotSocketApiAddress, env.SpotSocketApiAddress);
        Assert.Equal(live.UsdFuturesRestAddress, env.UsdFuturesRestAddress);
        Assert.Equal(live.UsdFuturesSocketAddress, env.UsdFuturesSocketAddress);
        Assert.Equal(live.UsdFuturesSocketApiAddress, env.UsdFuturesSocketApiAddress);
    }

    [Fact]
    public void TestnetUsesSpotTestnetAndFuturesDemoPresets()
    {
        var env = new BinanceEnvironmentFactory(Options.Create(new ExchangeOptions())).Get(ExchangeEnvironment.Testnet);
        var testnet = BinanceEnvironment.Testnet;
        var demo = BinanceEnvironment.Demo;

        Assert.Equal(testnet.SpotRestAddress, env.SpotRestAddress);
        Assert.Equal(testnet.SpotSocketStreamAddress, env.SpotSocketStreamAddress);
        Assert.Equal(testnet.SpotSocketApiAddress, env.SpotSocketApiAddress);
        Assert.Equal(demo.UsdFuturesRestAddress, env.UsdFuturesRestAddress);
        Assert.Equal(demo.UsdFuturesSocketAddress, env.UsdFuturesSocketAddress);
        Assert.True(string.IsNullOrEmpty(env.UsdFuturesSocketApiAddress));
    }

    [Fact]
    public void NamesNeverCollideWithPresets()
    {
        var factory = new BinanceEnvironmentFactory(Options.Create(new ExchangeOptions()));

        foreach (var environment in new[] { ExchangeEnvironment.Production, ExchangeEnvironment.Testnet })
            Assert.DoesNotContain(factory.Get(environment).Name, BinanceEnvironment.All);
    }

    [Fact]
    public void OverridesAreHonouredAndInstancesAreMemoised()
    {
        var options = new ExchangeOptions();
        options.Testnet.FuturesRest = "https://testnet.binancefuture.com";
        var factory = new BinanceEnvironmentFactory(Options.Create(options));

        var first = factory.Get(ExchangeEnvironment.Testnet);

        Assert.Equal("https://testnet.binancefuture.com", first.UsdFuturesRestAddress);
        Assert.Same(first, factory.Get(ExchangeEnvironment.Testnet));
        Assert.NotSame(first, factory.Get(ExchangeEnvironment.Production));
    }
}
