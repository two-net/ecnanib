using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange.Live;

/// <summary>USD-M futures demo environment round trips (EX-02, EX-04, FR-EXE-11). Orders are placed far from the market and cancelled.</summary>
[Trait("Category", "Live")]
public class FuturesDemoLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewId(string prefix) => $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";

    [Fact]
    public async Task PingAccountAndPositionSettings()
    {
        var key = LiveTestKeys.RequireFuturesTestnet();
        await using var services = LiveTestKeys.BuildServices();
        var client = services.GetRequiredService<IExchangeClientFactory>().GetFuturesClient(key);

        await client.PingAsync(Ct);
        var summary = await client.GetAccountSummaryAsync(Ct);
        var balances = await client.GetBalancesAsync(Ct);
        Assert.True(summary.TotalWalletBalance >= 0);
        Assert.NotEmpty(balances);

        await client.SetPositionModeAsync(PositionMode.OneWay, Ct);
        Assert.Equal(PositionMode.OneWay, await client.GetPositionModeAsync(Ct));

        Assert.Equal(5, await client.SetLeverageAsync("BTCUSDT", 5, Ct));
        await client.SetMarginTypeAsync("BTCUSDT", MarginType.Isolated, Ct);
        await client.SetMarginTypeAsync("BTCUSDT", MarginType.Isolated, Ct);

        var positions = await client.GetPositionsAsync("BTCUSDT", Ct);
        Assert.All(positions, p => Assert.Equal("BTCUSDT", p.Symbol));
    }

    [Fact]
    public async Task LimitOrderRoundTripAndReduceOnlyRejection()
    {
        var key = LiveTestKeys.RequireFuturesTestnet();
        await using var services = LiveTestKeys.BuildServices();
        var factory = services.GetRequiredService<IExchangeClientFactory>();
        var client = factory.GetFuturesClient(key);
        var data = factory.GetPublicData(ExchangeEnvironment.Testnet, MarketType.UsdFutures);

        var info = await data.GetExchangeInfoAsync(Ct);
        var symbol = Assert.Single(info.Symbols, s => s.Symbol == "BTCUSDT");
        var mark = await data.GetMarkPriceAsync("BTCUSDT", Ct);
        var limitPrice = symbol.Filters.RoundPriceDownToTick(mark.Mark * 0.5m);
        var quantity = symbol.Filters.RoundQuantityDownToStep(Math.Max(symbol.Filters.MinNotional * 1.5m / limitPrice, symbol.Filters.MinQuantity) + symbol.Filters.StepSize);

        var events = new List<UserDataEvent>();
        using var subscription = client.SubscribeUserData((e, _) => { lock (events) events.Add(e); return ValueTask.CompletedTask; });
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        var clientId = NewId("ecn-fut");
        var placed = await client.PlaceOrderAsync(new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, clientId, quantity, limitPrice), Ct);
        Assert.Equal(clientId, placed.ClientOrderId);
        Assert.Equal(limitPrice, placed.Price);

        var fetched = await client.GetOrderAsync("BTCUSDT", OrderRef.ByClientId(clientId), Ct);
        Assert.Equal(placed.OrderId, fetched.OrderId);

        var positions = await client.GetPositionsAsync("BTCUSDT", Ct);
        if (positions.All(p => !p.IsOpen))
        {
            var rejected = await Assert.ThrowsAsync<ExchangeRequestException>(() => client.PlaceOrderAsync(
                new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, NewId("ecn-sl"), quantity, StopPrice: symbol.Filters.RoundPriceDownToTick(mark.Mark * 0.4m), ReduceOnly: true, WorkingType: WorkingType.MarkPrice), Ct));
            Assert.Contains(rejected.Kind, new[] { ExchangeErrorKind.NoPosition, ExchangeErrorKind.RejectedOrder, ExchangeErrorKind.IncorrectState });
        }

        var cancelled = await client.CancelAllOrdersAsync("BTCUSDT", Ct);
        Assert.Contains(cancelled, o => o.OrderId == placed.OrderId);
        Assert.Empty(await client.GetOpenOrdersAsync("BTCUSDT", Ct));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                if (events.OfType<UserDataEvent.OrderUpdate>().Any(e => e.Order.ClientOrderId == clientId && e.Execution == ExecutionType.Canceled))
                    break;
            }

            await Task.Delay(250, Ct);
        }

        lock (events)
        {
            var updates = events.OfType<UserDataEvent.OrderUpdate>().Where(e => e.Order.ClientOrderId == clientId).ToList();
            Assert.Contains(updates, e => e.Execution == ExecutionType.New);
            Assert.Contains(updates, e => e.Execution == ExecutionType.Canceled);
        }

        Assert.Equal(StreamHealth.Connected, client.UserDataStatus.Health);
    }
}
