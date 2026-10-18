using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange.Live;

/// <summary>Keyless production checks (EX-06). Read-only; costs a few request-weight units.</summary>
[Trait("Category", "Live")]
public class PublicMarketDataLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MarketType.Spot)]
    [InlineData(MarketType.UsdFutures)]
    public async Task ServerTimeIsCloseToLocalTime(MarketType market)
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var data = services.GetRequiredService<IExchangeClientFactory>().GetPublicData(ExchangeEnvironment.Production, market);

        var serverTime = await data.GetServerTimeAsync(Ct);

        Assert.InRange((serverTime - DateTimeOffset.UtcNow).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(MarketType.Spot)]
    [InlineData(MarketType.UsdFutures)]
    public async Task ExchangeInfoListsBtcUsdtWithFiltersAndRateLimits(MarketType market)
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var data = services.GetRequiredService<IExchangeClientFactory>().GetPublicData(ExchangeEnvironment.Production, market);

        var info = await data.GetExchangeInfoAsync(Ct);

        var btc = Assert.Single(info.Symbols, s => s.Symbol == "BTCUSDT");
        Assert.True(btc.IsTrading);
        Assert.True(btc.Filters.TickSize > 0 && btc.Filters.StepSize > 0 && btc.Filters.MinNotional > 0);
        Assert.True(btc.Filters.IsValidPrice(btc.Filters.RoundPriceDownToTick(50000.123m)));
        Assert.Contains(info.RateLimits, r => r.Kind == RateLimitKind.RequestWeight && r.Limit > 0);
        Assert.Contains(info.RateLimits, r => r.Kind == RateLimitKind.Orders && r.Limit > 0);
        if (market == MarketType.Spot)
            Assert.True(btc.OcoAllowed);
    }

    [Theory]
    [InlineData(MarketType.Spot)]
    [InlineData(MarketType.UsdFutures)]
    public async Task RecentCandlesArePagedContiguousAndClosed(MarketType market)
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var data = services.GetRequiredService<IExchangeClientFactory>().GetPublicData(ExchangeEnvironment.Production, market);

        var candles = await data.GetRecentCandlesAsync("BTCUSDT", CandleInterval.OneHour, 1200, closedOnly: true, Ct);

        Assert.Equal(1200, candles.Count);
        Assert.All(candles, c => Assert.True(c.IsClosed));
        for (var i = 1; i < candles.Count; i++)
            Assert.Equal(candles[i - 1].OpenTime + TimeSpan.FromHours(1), candles[i].OpenTime);
        Assert.True(candles[^1].CloseTime < DateTimeOffset.UtcNow);

        var withForming = await data.GetRecentCandlesAsync("BTCUSDT", CandleInterval.FifteenMinutes, 3, closedOnly: false, Ct);
        Assert.Equal(3, withForming.Count);
        Assert.False(withForming[^1].IsClosed);
    }

    [Fact]
    public async Task PriceAndMarkPrice()
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var factory = services.GetRequiredService<IExchangeClientFactory>();

        var spot = await factory.GetPublicData(ExchangeEnvironment.Production, MarketType.Spot).GetPriceAsync("btcusdt", Ct);
        var mark = await factory.GetPublicData(ExchangeEnvironment.Production, MarketType.UsdFutures).GetMarkPriceAsync("BTCUSDT", Ct);

        Assert.Equal("BTCUSDT", spot.Symbol);
        Assert.True(spot.Price > 0);
        Assert.True(mark.Mark > 0);
        Assert.True(mark.NextFundingTime > DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.GetPublicData(ExchangeEnvironment.Production, MarketType.Spot).GetMarkPriceAsync("BTCUSDT", Ct));
    }

    [Fact]
    public async Task UnknownSymbolIsARequestError()
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var data = services.GetRequiredService<IExchangeClientFactory>().GetPublicData(ExchangeEnvironment.Production, MarketType.Spot);

        var ex = await Assert.ThrowsAsync<ExchangeRequestException>(() => data.GetPriceAsync("NOPE123XYZ", Ct));

        Assert.Equal(ExchangeErrorKind.UnknownSymbol, ex.Kind);
    }

    [Fact]
    public async Task PriceAndMarkStreamsDeliverWithinThirtySeconds()
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var streams = services.GetRequiredService<IMarketStreams>();
        var price = new TaskCompletionSource<PriceTick>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mark = new TaskCompletionSource<MarkPrice>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var p = streams.SubscribePrice(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT"), (t, _) => { price.TrySetResult(t); return ValueTask.CompletedTask; });
        using var m = streams.SubscribeMarkPrice(new MarketKey(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT"), (t, _) => { mark.TrySetResult(t); return ValueTask.CompletedTask; });

        var tick = await price.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var markTick = await mark.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(tick.Price > 0);
        Assert.True(markTick.Mark > 0);
        Assert.All(streams.GetStatuses(), s => Assert.Equal(StreamHealth.Connected, s.Health));
        Assert.All(services.GetRequiredService<IExchangeHealth>().MarketStreams, s => Assert.NotNull(s.LastMessageAt));
    }
}
