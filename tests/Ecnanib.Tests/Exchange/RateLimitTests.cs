using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-04, FR-EXE-09.</summary>
public sealed class RateLimitTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    private static OrderRequest Order(RequestPriority priority) =>
        new("BTCUSDT", OrderSide.Buy, OrderType.Limit, 0.002m, $"o-{Guid.NewGuid():N}"[..20], priority, price: 60000m);

    [Fact]
    public async Task Used_weight_is_read_from_the_headers()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/api/v3/account",
            _ => FakeBinance.Json(BinanceSamples.SpotAccount, headers: ("X-MBX-USED-WEIGHT-1M", "1234")),

            // An answer to an earlier request, overtaken on the way back: weight only grows within a minute.
            _ => FakeBinance.Json(BinanceSamples.SpotAccount, headers: ("X-MBX-USED-WEIGHT-1M", "1100")));

        await exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation);
        await exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), Cancellation);

        var usage = exchange.Usage(ExchangeServer.SpotDemo);
        Assert.Equal((1234, 6000, 30), (usage.UsedWeight, usage.WeightLimit, usage.HeadroomPercent));
        Assert.Equal(0, exchange.Usage(ExchangeServer.SpotLive).UsedWeight);
    }

    [Fact]
    public async Task Normal_requests_stop_at_the_headroom_while_protective_ones_carry_on_until_the_minute_ends()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var key = ExchangeHarness.HmacKey();

        // 30% of 6000 is kept back, so normal requests stop at 4200. The account read weighs 20.
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount, headers: ("X-MBX-USED-WEIGHT-1M", "4190")));
        exchange.Binance.On(HttpMethod.Get, "/api/v3/order", request => FakeBinance.Json(BinanceSamples.SpotOrder(request.Parameter("origClientOrderId")!), headers: ("X-MBX-USED-WEIGHT-1M", "4194")));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);

        var refused = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation));
        Assert.Equal(ExchangeFailure.Throttled, refused.Failure);
        Assert.InRange(refused.RetryAfter!.Value.TotalSeconds, 1, 60);
        Assert.Single(exchange.Binance.To("/api/v3/account"));

        var protective = await exchange.Account.GetOrderAsync(
            Market.Spot, key, new OrderReference("BTCUSDT", "stop-1", OrderType.StopMarket), RequestPriority.Protective, Cancellation);
        Assert.NotNull(protective);

        exchange.Clock.Advance(TimeSpan.FromMinutes(1));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        Assert.Equal(2, exchange.Binance.To("/api/v3/account").Count);
    }

    [Fact]
    public async Task HTTP_429_backs_the_server_off_until_Retry_After()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v3/account",
            _ => FakeBinance.Error(HttpStatusCode.TooManyRequests, -1003, "Too many requests.", ("Retry-After", "30")),
            _ => FakeBinance.Json(BinanceSamples.FuturesAccount));

        var throttled = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation));
        Assert.Equal((ExchangeFailure.Throttled, -1003), (throttled.Failure, throttled.Code));
        Assert.InRange(throttled.RetryAfter!.Value.TotalSeconds, 29, 30);

        var refused = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation));
        Assert.Equal(ExchangeFailure.Throttled, refused.Failure);
        Assert.Single(exchange.Binance.To("/fapi/v3/account"));
        Assert.NotNull(exchange.Usage(ExchangeServer.FuturesDemo).BackoffUntil);

        exchange.Clock.Advance(TimeSpan.FromSeconds(31));
        await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);
        Assert.Equal(2, exchange.Binance.To("/fapi/v3/account").Count);
    }

    [Fact]
    public async Task HTTP_418_halts_only_the_server_that_sent_it_and_reports_the_ban_once()
    {
        var bans = new BanLog();
        await using var exchange = new ExchangeHarness(postgres, services: services => services.AddSingleton<IExchangeBanObserver>(bans));
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/fapi/v3/account", _ => FakeBinance.Json(BinanceSamples.FuturesAccount));
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/api/v3/account",
            _ => FakeBinance.Error((HttpStatusCode)418, -1003, "Way too much request weight used; IP banned until 1700000000000.", ("Retry-After", "120")),
            _ => FakeBinance.Json(BinanceSamples.SpotAccount));

        var banned = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation));
        Assert.Equal(ExchangeFailure.Banned, banned.Failure);

        // Even a protective request waits: Binance would refuse it anyway.
        var halted = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetOrderAsync(
            Market.Spot, key, new OrderReference("BTCUSDT", "stop-1", OrderType.StopMarket), RequestPriority.Protective, Cancellation));
        Assert.Equal(ExchangeFailure.Banned, halted.Failure);
        Assert.Empty(exchange.Binance.To("/api/v3/order"));

        await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);

        var ban = Assert.Single(bans.Bans);
        Assert.Equal(ExchangeServer.SpotDemo, ban.Server);
        Assert.InRange((ban.Until - exchange.Clock.GetUtcNow()).TotalSeconds, 110, 120);
        Assert.Equal(ban.Until, exchange.Usage(ExchangeServer.SpotDemo).BannedUntil);

        exchange.Clock.Advance(TimeSpan.FromSeconds(121));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        Assert.Single(bans.Bans);
    }

    [Fact]
    public async Task A_418_lasts_until_the_time_in_its_message_whatever_Retry_After_says()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.Skew = TimeSpan.FromSeconds(5);
        var until = exchange.Clock.GetUtcNow() + exchange.Binance.Skew + TimeSpan.FromMinutes(10);
        exchange.Binance.On(
            HttpMethod.Get,
            "/fapi/v3/account",
            _ => FakeBinance.Error((HttpStatusCode)418, -1003, $"Way too many requests; IP banned until {until.ToUnixTimeMilliseconds()}.", ("Retry-After", "30")));

        await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Futures, ExchangeHarness.HmacKey(), Cancellation));

        // Binance's time, turned into this server's.
        var bannedUntil = exchange.Usage(ExchangeServer.FuturesDemo).BannedUntil!.Value;
        Assert.InRange((until - exchange.Binance.Skew - bannedUntil).Duration().TotalSeconds, 0, 1);
    }

    [Fact]
    public async Task Requests_banned_together_report_one_ban()
    {
        var bans = new BanLog();
        await using var exchange = new ExchangeHarness(postgres, services: services => services.AddSingleton<IExchangeBanObserver>(bans));
        var key = ExchangeHarness.HmacKey();
        var arrived = 0;
        var both = new TaskCompletionSource();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", async (_, _) =>
        {
            var number = Interlocked.Increment(ref arrived);

            if (number == 2)
            {
                both.SetResult();
            }

            await both.Task;

            // A second later, as the second answer would be.
            return FakeBinance.Error((HttpStatusCode)418, -1003, "Way too much request weight used.", ("Retry-After", number == 1 ? "120" : "121"));
        });

        await Task.WhenAll(
            Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation)),
            Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation)));

        Assert.Single(bans.Bans);
        Assert.InRange((exchange.Usage(ExchangeServer.SpotDemo).BannedUntil!.Value - exchange.Clock.GetUtcNow()).TotalSeconds, 119, 121);
    }

    [Fact]
    public async Task Order_counts_are_tracked_per_key_and_normal_orders_stop_at_the_headroom()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var busy = ExchangeHarness.HmacKey();
        var quiet = ExchangeHarness.HmacKey();

        // Spot allows 100 orders in 10 seconds, and 30% of that is kept for protective orders.
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", request => FakeBinance.Json(
            BinanceSamples.SpotOrder(request.Parameter("newClientOrderId")!),
            headers: ("X-MBX-ORDER-COUNT-10S", "70")));

        await exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Normal), Cancellation);

        var refused = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Normal), Cancellation));
        Assert.Equal(ExchangeFailure.Throttled, refused.Failure);
        Assert.Single(exchange.Binance.To("/api/v3/order"));

        await exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Protective), Cancellation);
        await exchange.Trading.PlaceOrderAsync(Market.Spot, quiet, Order(RequestPriority.Normal), Cancellation);
        Assert.Equal(3, exchange.Binance.To("/api/v3/order").Count);
    }

    [Fact]
    public async Task Too_many_orders_backs_off_only_that_key()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var busy = ExchangeHarness.HmacKey();
        var quiet = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        exchange.Binance.OnSequence(
            HttpMethod.Post,
            "/api/v3/order",
            _ => FakeBinance.Error(HttpStatusCode.TooManyRequests, -1015, "Too many new orders; current limit is 100 orders per TEN_SECONDS.", ("Retry-After", "8")),
            request => FakeBinance.Json(BinanceSamples.SpotOrder(request.Parameter("newClientOrderId")!)));

        var throttled = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Normal), Cancellation));
        Assert.Equal((ExchangeFailure.Throttled, -1015), (throttled.Failure, throttled.Code));

        await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Protective), Cancellation));
        await exchange.Account.GetAccountAsync(Market.Spot, busy, Cancellation);
        await exchange.Trading.PlaceOrderAsync(Market.Spot, quiet, Order(RequestPriority.Normal), Cancellation);
        Assert.Null(exchange.Usage(ExchangeServer.SpotDemo).BackoffUntil);
        Assert.Equal(2, exchange.Binance.To("/api/v3/order").Count);

        exchange.Clock.Advance(TimeSpan.FromSeconds(9));
        await exchange.Trading.PlaceOrderAsync(Market.Spot, busy, Order(RequestPriority.Normal), Cancellation);
        Assert.Equal(3, exchange.Binance.To("/api/v3/order").Count);
    }

    [Fact]
    public async Task Exchange_information_sets_the_limits()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo(weightLimit: 1200)));
        Assert.Equal(6000, exchange.Usage(ExchangeServer.SpotLive).WeightLimit);

        await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Live, Cancellation);

        Assert.Equal(1200, exchange.Usage(ExchangeServer.SpotLive).WeightLimit);
        Assert.Equal(1200, exchange.Usage(ExchangeServer.SpotMarketData).WeightLimit);
        Assert.Equal(6000, exchange.Usage(ExchangeServer.SpotDemo).WeightLimit);
    }

    private sealed class BanLog : IExchangeBanObserver
    {
        private readonly ConcurrentQueue<ExchangeBan> _bans = new();

        public IReadOnlyList<ExchangeBan> Bans => [.. _bans];

        public void OnBanned(ExchangeBan ban) => _bans.Enqueue(ban);
    }
}
