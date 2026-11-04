using System.Globalization;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>FR-EXE-10, EXT-BIN-03.</summary>
public sealed class ClockTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task The_timestamp_follows_Binance_s_clock()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.Skew = TimeSpan.FromSeconds(7);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));

        await exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/account"));
        var timestamp = long.Parse(request.Parameter("timestamp")!, CultureInfo.InvariantCulture);
        Assert.InRange(exchange.Binance.ServerTime() - timestamp, 0, 1000);
        Assert.InRange(exchange.Usage(ExchangeServer.SpotDemo).ClockOffset!.Value.TotalSeconds, 6.5, 7.5);
    }

    [Fact]
    public async Task The_clock_is_synced_once_and_again_after_ten_minutes()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v3/account", _ => FakeBinance.Json(BinanceSamples.FuturesAccount));
        var key = ExchangeHarness.HmacKey();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation)));
        Assert.Single(exchange.Binance.To("/fapi/v1/time"));

        exchange.Clock.Advance(TimeSpan.FromMinutes(9));
        await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);
        Assert.Single(exchange.Binance.To("/fapi/v1/time"));

        exchange.Clock.Advance(TimeSpan.FromMinutes(2));
        await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);
        Assert.Equal(2, exchange.Binance.To("/fapi/v1/time").Count);
    }

    [Fact]
    public async Task A_timestamp_outside_the_receive_window_resyncs_the_clock_and_is_sent_once_more()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/api/v3/account",
            _ =>
            {
                // Binance's clock jumps after the first sync.
                exchange.Binance.Skew = TimeSpan.FromSeconds(30);
                return FakeBinance.Error(HttpStatusCode.BadRequest, -1021, "Timestamp for this request is outside of the recvWindow.");
            },
            _ => FakeBinance.Json(BinanceSamples.SpotAccount));

        var account = await exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation);

        Assert.Equal(true, account.CanTrade);
        Assert.Equal(2, exchange.Binance.To("/api/v3/time").Count);
        var retried = exchange.Binance.To("/api/v3/account")[1];
        Assert.InRange(exchange.Binance.ServerTime() - long.Parse(retried.Parameter("timestamp")!, CultureInfo.InvariantCulture), 0, 1000);
    }

    [Fact]
    public async Task A_second_timestamp_rejection_is_not_retried_again()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(
            HttpMethod.Get,
            "/api/v3/account",
            _ => FakeBinance.Error(HttpStatusCode.BadRequest, -1021, "Timestamp for this request is outside of the recvWindow."));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, -1021), (exception.Failure, exception.Code));
        Assert.Equal(2, exchange.Binance.To("/api/v3/account").Count);
    }

    [Fact]
    public async Task A_failed_routine_sync_keeps_the_previous_offset_and_waits_before_trying_again()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        var key = ExchangeHarness.HmacKey();
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        var syncedAt = exchange.Usage(ExchangeServer.SpotDemo).ClockSyncedAt;

        exchange.Clock.Advance(TimeSpan.FromMinutes(11));
        exchange.Binance.On(HttpMethod.Get, "/api/v3/time", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);

        Assert.Equal(3, exchange.Binance.To("/api/v3/account").Count);
        Assert.Equal(2, exchange.Binance.To("/api/v3/time").Count);
        Assert.Equal(syncedAt, exchange.Usage(ExchangeServer.SpotDemo).ClockSyncedAt);

        exchange.Clock.Advance(TimeSpan.FromSeconds(31));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        Assert.Equal(3, exchange.Binance.To("/api/v3/time").Count);
    }

    [Fact]
    public async Task With_no_offset_yet_a_failed_sync_sends_nothing()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/time", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 0.1m, "a1-c2-stop", RequestPriority.Protective, stopPrice: 58000m),
            Cancellation));

        Assert.Equal((ExchangeFailure.Unavailable, false), (exception.Failure, exception.MayHaveExecuted));
        Assert.Empty(exchange.Binance.To("/fapi/v1/algoOrder"));
    }

    [Fact]
    public async Task A_timestamp_rejection_that_cannot_be_resynced_is_still_a_rejection()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", _ =>
        {
            exchange.Binance.On(HttpMethod.Get, "/api/v3/time", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));
            return FakeBinance.Error(HttpStatusCode.BadRequest, -1021, "Timestamp for this request is outside of the recvWindow.");
        });

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Spot,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 0.001m, "a1-c2-entry", RequestPriority.Normal),
            Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, -1021), (exception.Failure, exception.Code));
        Assert.Single(exchange.Binance.To("/api/v3/order"));
    }

    [Fact]
    public async Task The_receive_window_comes_from_the_configuration()
    {
        await using var exchange = new ExchangeHarness(postgres, new() { ["Binance:RecvWindowMs"] = "2500" });
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));

        await exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation);

        Assert.Equal("2500", Assert.Single(exchange.Binance.To("/api/v3/account")).Parameter("recvWindow"));
    }
}
