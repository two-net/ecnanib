using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// NFR-REL-03, AC-14. .NET's handler sends a request with no body again, up to three times, when its
/// connection closes before an answer. For an order, that would be a second order. The adapter gives
/// every order and cancel an empty body, which stops it. This guards that against a change in .NET, on
/// a real socket, because a fake handler replaces the code that retries.
/// </summary>
public sealed class RetryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task A_trade_whose_connection_drops_is_sent_once_and_left_unknown(string method)
    {
        var cancellation = TestContext.Current.CancellationToken;

        // The time request opens a connection, which the trade then reuses, as it would in production.
        using var server = new LoopbackServer(head => head.StartsWith("GET /api/v3/time ", StringComparison.Ordinal)
            ? LoopbackServer.Json($$"""{"serverTime":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""")
            : null);
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["Binance:Spot:DemoBaseUrl"] = server.BaseUrl });
        app.Start();
        var trading = app.Services.GetRequiredService<IExchangeTrading>();
        var key = ExchangeHarness.HmacKey();

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => method == "POST"
            ? trading.PlaceOrderAsync(
                Market.Spot,
                key,
                new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 0.001m, "a1-c2-entry", RequestPriority.Normal),
                cancellation)
            : trading.CancelOrderAsync(
                Market.Spot,
                key,
                new OrderReference("BTCUSDT", "a1-c2-entry", OrderType.Limit),
                RequestPriority.Normal,
                cancellation));

        Assert.True(exception.MayHaveExecuted);
        var trade = Assert.Single(server.Requests, head => head.StartsWith(method, StringComparison.Ordinal));
        Assert.Contains("Content-Length: 0\n", trade, StringComparison.OrdinalIgnoreCase);
        Assert.Single(server.Requests, head => head.StartsWith("GET /api/v3/time ", StringComparison.Ordinal));
    }
}
