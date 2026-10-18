using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange.Live;

/// <summary>Spot testnet round trips (EX-01, EX-04). Orders are placed far from the market and cancelled again.</summary>
[Trait("Category", "Live")]
public class SpotTestnetLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewId(string prefix) => $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";

    [Fact]
    public async Task PingBalancesAndPermissions()
    {
        var key = LiveTestKeys.RequireSpotTestnet();
        await using var services = LiveTestKeys.BuildServices();
        var client = services.GetRequiredService<IExchangeClientFactory>().GetSpotClient(key);

        await client.PingAsync(Ct);
        var balances = await client.GetBalancesAsync(Ct);
        var permissions = await client.GetApiKeyPermissionsAsync(Ct);

        Assert.NotEmpty(balances);
        Assert.False(permissions.Verified);
    }

    [Fact]
    public async Task WrongSecretIsAnAuthFailure()
    {
        var key = LiveTestKeys.RequireSpotTestnet();
        var wrong = new ExchangeKeyHandle("spot-testnet-wrong", ExchangeEnvironment.Testnet, new ExchangeCredentials(key.Credentials.ApiKey, "0000000000000000000000000000000000000000000000000000000000000000"));
        await using var services = LiveTestKeys.BuildServices();
        var client = services.GetRequiredService<IExchangeClientFactory>().GetSpotClient(wrong);

        await Assert.ThrowsAsync<ExchangeAuthException>(() => client.PingAsync(Ct));
    }

    [Fact]
    public async Task LimitOrderRoundTripWithClientOrderIdAndUserDataEvents()
    {
        var key = LiveTestKeys.RequireSpotTestnet();
        await using var services = LiveTestKeys.BuildServices();
        var factory = services.GetRequiredService<IExchangeClientFactory>();
        var client = factory.GetSpotClient(key);
        var data = factory.GetPublicData(ExchangeEnvironment.Testnet, MarketType.Spot);

        var info = await data.GetExchangeInfoAsync(Ct);
        var symbol = Assert.Single(info.Symbols, s => s.Symbol == "BTCUSDT");
        var price = (await data.GetPriceAsync("BTCUSDT", Ct)).Price;
        var limitPrice = symbol.Filters.RoundPriceDownToTick(price * 0.5m);
        var quantity = symbol.Filters.RoundQuantityDownToStep(Math.Max(symbol.Filters.MinNotional * 1.5m / limitPrice, symbol.Filters.MinQuantity) + symbol.Filters.StepSize);
        Assert.True(symbol.Filters.MeetsMinNotional(limitPrice, quantity));

        var events = new List<UserDataEvent>();
        using var subscription = client.SubscribeUserData((e, _) => { lock (events) events.Add(e); return ValueTask.CompletedTask; });
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        var clientId = NewId("ecn-test");
        var placed = await client.PlaceOrderAsync(new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, clientId, quantity, limitPrice), Ct);
        Assert.Equal(clientId, placed.ClientOrderId);
        Assert.True(placed.Status.IsOpen());

        var fetched = await client.GetOrderAsync("BTCUSDT", OrderRef.ByClientId(clientId), Ct);
        Assert.Equal(placed.OrderId, fetched.OrderId);
        Assert.Contains(await client.GetOpenOrdersAsync("BTCUSDT", Ct), o => o.OrderId == placed.OrderId);

        var cancelled = await client.CancelOrderAsync("BTCUSDT", OrderRef.ById(placed.OrderId), Ct);
        Assert.Equal(OrderStatus.Canceled, cancelled.Status);

        var unknown = await Assert.ThrowsAsync<ExchangeRequestException>(() => client.CancelOrderAsync("BTCUSDT", OrderRef.ById(placed.OrderId), Ct));
        Assert.Equal(ExchangeErrorKind.UnknownOrder, unknown.Kind);

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

    [Fact]
    public async Task OcoExitRoundTrip()
    {
        var key = LiveTestKeys.RequireSpotTestnet();
        await using var services = LiveTestKeys.BuildServices();
        var factory = services.GetRequiredService<IExchangeClientFactory>();
        var client = factory.GetSpotClient(key);
        var data = factory.GetPublicData(ExchangeEnvironment.Testnet, MarketType.Spot);

        var info = await data.GetExchangeInfoAsync(Ct);
        var symbol = Assert.Single(info.Symbols, s => s.Symbol == "BTCUSDT");
        var price = (await data.GetPriceAsync("BTCUSDT", Ct)).Price;
        var quantity = symbol.Filters.RoundQuantityDownToStep(Math.Max(symbol.Filters.MinNotional * 1.5m / price, symbol.Filters.MinQuantity) + symbol.Filters.StepSize);
        var balance = (await client.GetBalancesAsync(Ct)).FirstOrDefault(b => b.Asset == "BTC");
        if (balance is null || balance.Available < quantity)
            Assert.Skip("Live test: the spot testnet account holds no BTC to place an OCO sell.");

        var listId = NewId("ecn-oco");
        var list = await client.PlaceOcoAsync(new SpotOcoRequest(
            "BTCUSDT", OrderSide.Sell, quantity,
            TakeProfitPrice: symbol.Filters.RoundPriceDownToTick(price * 1.5m),
            StopPrice: symbol.Filters.RoundPriceDownToTick(price * 0.5m),
            StopLimitPrice: symbol.Filters.RoundPriceDownToTick(price * 0.49m),
            ListClientOrderId: listId, TakeProfitClientOrderId: listId + "-tp", StopClientOrderId: listId + "-sl"), Ct);

        Assert.Equal(listId, list.ListClientOrderId);
        Assert.Equal(2, list.Orders.Count);

        var fetched = await client.GetOcoAsync(listId, Ct);
        Assert.Equal(list.OrderListId, fetched.OrderListId);

        var cancelled = await client.CancelOcoAsync("BTCUSDT", listId, Ct);
        Assert.Equal(OcoListStatus.AllDone, cancelled.Status);
    }
}
