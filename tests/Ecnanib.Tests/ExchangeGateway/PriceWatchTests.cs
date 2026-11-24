using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Prices update in the UI within 2 seconds (FR-NTF-2): a price that a page follows is read once
/// a second, for everyone who follows it, with the shared reads of the market data (FR-BIN-3),
/// and only while the limit of its market has room (FR-BIN-4). A price that nobody follows is
/// not read. See "Following a price" in docs/exchange.md.
/// </summary>
/// <remarks>The clock is the test's, with the timers that run on it: a second passes when a test says so.</remarks>
public class PriceWatchTests
{
    private const string SpotPath = "/api/v3/ticker/price";
    private const string FuturesPath = "/fapi/v2/ticker/price";
    private const string MarkPath = "/fapi/v1/premiumIndex";

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_price_that_is_followed_is_read_once_a_second_for_everyone_who_follows_it()
    {
        using var watching = new Watching();
        using var one = watching.Follow();
        using var another = watching.Follow();

        // Two pages show the symbol, and Binance is asked once.
        await watching.ReachedAsync(SpotPath, 1);
        await one.HeardAsync(1);
        await another.HeardAsync(1);

        Assert.Equal(new WatchedPrice(104250.01m, BinanceCalls.Now), Assert.Single(one.Heard));
        Assert.Equal(one.Heard, another.Heard);

        // Nothing is asked before the second is over, and one thing when it is.
        watching.Clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, watching.CallsTo(SpotPath));

        watching.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await watching.ReachedAsync(SpotPath, 2);
        await one.HeardAsync(2);
        await another.HeardAsync(2);

        // Second by second, for as long as it is followed.
        for (var read = 3; read <= 6; read++)
        {
            watching.Clock.Advance(Second);
            await watching.ReachedAsync(SpotPath, read);
            await one.HeardAsync(read);
        }

        Assert.Equal(6, watching.CallsTo(SpotPath));
        Assert.Equal(BinanceCalls.Now + (5 * Second), one.Heard[^1].ReadAt);

        // Only that call was made, to the market and the environment that was asked for.
        Assert.All(watching.Binance.Calls, call => Assert.Equal(BinanceCalls.HostOf(Market.Spot, TradingMode.Live), call.Address.Host));
    }

    [Fact]
    public async Task What_begins_to_follow_is_told_the_price_that_the_watch_has_at_once_with_no_read()
    {
        using var watching = new Watching();
        using var first = watching.Follow();
        await first.HeardAsync(1);

        // A page that is written asks for what is known, and never waits for Binance.
        Assert.Equal(first.Heard[0], watching.Watch.Latest(Market.Spot, TradingMode.Live, MarketService.Symbol, ExchangePriceKind.LastTraded));

        using var late = watching.Follow();

        Assert.Equal(first.Heard, late.Heard);
        Assert.Equal(1, watching.CallsTo(SpotPath));

        // A price that nobody follows is not known, and asking for it reads nothing.
        Assert.Null(watching.Watch.Latest(Market.Spot, TradingMode.Demo, MarketService.Symbol, ExchangePriceKind.LastTraded));
        Assert.Null(watching.Watch.Latest(Market.Spot, TradingMode.Live, MarketService.OtherSymbol, ExchangePriceKind.LastTraded));
        Assert.Single(watching.Binance.Calls);
    }

    [Fact]
    public async Task A_price_that_nobody_follows_any_more_is_not_read()
    {
        using var watching = new Watching();
        var one = watching.Follow();
        var another = watching.Follow();
        await watching.ReachedAsync(SpotPath, 1);

        // One page goes, and the other still follows.
        one.Dispose();
        watching.Clock.Advance(Second);
        await watching.ReachedAsync(SpotPath, 2);
        await another.HeardAsync(2);
        Assert.Single(one.Heard);

        // The last page goes.
        another.Dispose();

        for (var second = 0; second < 5; second++)
        {
            watching.Clock.Advance(Second);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, watching.CallsTo(SpotPath));
        Assert.Null(watching.Watch.Latest(Market.Spot, TradingMode.Live, MarketService.Symbol, ExchangePriceKind.LastTraded));

        // Whoever follows next begins anew.
        using var next = watching.Follow();
        await watching.ReachedAsync(SpotPath, 3);
        await next.HeardAsync(1);
    }

    [Fact]
    public async Task While_the_limit_has_no_room_nothing_is_read_and_what_follows_keeps_the_price_that_it_has()
    {
        using var watching = new Watching();
        using var follows = watching.Follow();
        await follows.HeardAsync(1);

        // What places and protects orders needs the room: the pages wait.
        watching.Room.Has = false;

        for (var second = 0; second < 5; second++)
        {
            watching.Clock.Advance(Second);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, watching.CallsTo(SpotPath));
        Assert.Single(follows.Heard);
        Assert.Equal(
            (104250.01m, BinanceCalls.Now),
            watching.Watch.Latest(Market.Spot, TradingMode.Live, MarketService.Symbol, ExchangePriceKind.LastTraded) is { } kept
                ? (kept.Price, kept.ReadAt)
                : default);

        // The watch asked about the limit of the market and the environment of the price.
        Assert.All(watching.Room.Asked, asked => Assert.Equal((Market.Spot, TradingMode.Live), asked));

        // With room again, the next second reads.
        watching.Room.Has = true;
        watching.Clock.Advance(Second);
        await watching.ReachedAsync(SpotPath, 2);
        await follows.HeardAsync(2);
    }

    [Fact]
    public async Task Each_price_is_followed_on_its_own_and_the_mark_price_is_of_the_futures_market_alone()
    {
        using var watching = new Watching();
        using var traded = watching.Follow(Market.Futures, TradingMode.Demo, ExchangePriceKind.LastTraded);
        using var mark = watching.Follow(Market.Futures, TradingMode.Demo, ExchangePriceKind.Mark);
        using var real = watching.Follow(Market.Futures, TradingMode.Live, ExchangePriceKind.Mark);

        await traded.HeardAsync(1);
        await mark.HeardAsync(1);
        await real.HeardAsync(1);

        // The price that the symbol last traded at, and the price that the exchange values a position at.
        Assert.Equal(104251.70m, traded.Heard[0].Price);
        Assert.Equal(104250.64871234m, mark.Heard[0].Price);

        Assert.Equal(
            [
                $"{BinanceCalls.AddressOf(Market.Futures, TradingMode.Demo)}{MarkPath}",
                $"{BinanceCalls.AddressOf(Market.Futures, TradingMode.Demo)}{FuturesPath}",
                $"{BinanceCalls.AddressOf(Market.Futures, TradingMode.Live)}{MarkPath}",
            ],
            watching.Binance.Calls.Select(call => call.Endpoint).Order(StringComparer.Ordinal));

        // The Spot market has no mark price, so none is followed there.
        Assert.Throws<ArgumentException>(() => watching.Watch.Follow(Market.Spot, TradingMode.Live, MarketService.Symbol, ExchangePriceKind.Mark, _ => { }));
    }

    [Fact]
    public async Task A_price_that_binance_does_not_give_is_left_at_that_and_asked_for_again()
    {
        using var watching = new Watching();
        watching.Answer = _ => StandInSystem.Json(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""");

        using var follows = watching.Follow();
        await watching.ReachedAsync(SpotPath, 1);

        // A read that failed is shared for five seconds, as every read of the market data is.
        for (var second = 0; second < 4; second++)
        {
            watching.Clock.Advance(Second);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, watching.CallsTo(SpotPath));
        Assert.Empty(follows.Heard);

        // Binance answers again, and so does the watch.
        watching.Answer = null;
        watching.Clock.Advance(Second);
        await watching.ReachedAsync(SpotPath, 2);
        await follows.HeardAsync(1);
    }

    [Fact]
    public async Task A_symbol_that_the_file_does_not_list_ends_no_watch_and_is_said_once()
    {
        using var watching = new Watching();

        // A position keeps its symbol, also when the file no longer lists it.
        using var gone = watching.Follow(symbol: "DOGEUSDT");
        using var listed = watching.Follow();
        await listed.HeardAsync(1);

        for (var read = 2; read <= 4; read++)
        {
            watching.Clock.Advance(Second);
            await listed.HeardAsync(read);
        }

        Assert.Empty(gone.Heard);
        Assert.All(watching.Binance.Calls, call => Assert.Contains("symbol=BTCUSDT", call.Address.Query));

        var said = Assert.Single(
            watching.Application.Events(),
            written => written.Area == "Ecnanib.ExchangeGateway.ExchangePriceWatch" && written.Level == "Warning");
        Assert.Equal("DOGEUSDT", said.Text("Symbol"));
    }

    [Fact]
    public async Task What_the_watch_logs_is_of_no_request_and_no_user()
    {
        using var watching = new Watching();

        // The reads of a price begin with the first that follows it, on the connection of a
        // page of a user.
        Follows gone;

        using (watching.Application.Logger().BeginCorrelation(new Correlation(RequestId: "of-a-page", UserId: "of-a-user")))
        {
            gone = watching.Follow(symbol: "DOGEUSDT");
        }

        using (gone)
        {
            await Wait.UntilAsync(
                () => watching.Application.Events().Any(written => written.Level == "Warning"),
                "the watch to say that the price of a symbol cannot be read");
        }

        // They are for everyone who follows that price: what they log says nothing of who came first.
        var said = Assert.Single(
            watching.Application.Events(),
            written => written.Area == "Ecnanib.ExchangeGateway.ExchangePriceWatch" && written.Level == "Warning");
        Assert.False(said.Has("RequestId"), said.ToString());
        Assert.False(said.Has("UserId"), said.ToString());
    }

    [Fact]
    public async Task What_follows_and_fails_keeps_the_price_from_nobody_else()
    {
        using var watching = new Watching();
        using var failing = watching.Watch.Follow(
            Market.Spot, TradingMode.Live, MarketService.Symbol, ExchangePriceKind.LastTraded, _ => throw new InvalidOperationException("The page is gone."));
        using var stays = watching.Follow();

        await stays.HeardAsync(1);

        watching.Clock.Advance(Second);
        await stays.HeardAsync(2);

        Assert.Contains(
            watching.Application.Events(),
            written => written.Area == "Ecnanib.ExchangeGateway.ExchangePriceWatch" && written.Level == "Error");
    }

    /// <summary>
    /// The application behind a Binance of the test's, on a clock that only the test moves, with
    /// a limit that has room when the test says so.
    /// </summary>
    private sealed class Watching : IDisposable
    {
        public Watching()
        {
            Clock = new FakeTimeProvider(BinanceCalls.Now);
            Binance = new StandInSystem(request =>
                Answer?.Invoke(request) ?? StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request.RequestUri!.AbsolutePath)));
            Application = Binance.Behind(
                ExchangeGatewayModule.HttpClientName,
                services: services =>
                {
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddSingleton<IExchangeLimits>(Room);
                });
            Watch = Application.Services.GetRequiredService<IExchangePriceWatch>();
        }

        public FakeTimeProvider Clock { get; }

        public StandInSystem Binance { get; }

        public LoggedApplication Application { get; }

        public IExchangePriceWatch Watch { get; }

        public Room Room { get; } = new();

        /// <summary>What Binance answers a call with, when it is not what its documentation gives: null is that.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Answer { get; set; }

        public int CallsTo(string path) => Binance.Calls.Count(call => call.Address.AbsolutePath == path);

        public Task ReachedAsync(string path, int calls) =>
            Wait.UntilAsync(() => CallsTo(path) >= calls, $"{calls} calls to reach Binance at {path}. There were {CallsTo(path)}");

        public Follows Follow(
            Market market = Market.Spot,
            TradingMode mode = TradingMode.Live,
            ExchangePriceKind kind = ExchangePriceKind.LastTraded,
            string symbol = MarketService.Symbol) =>
            new(Watch, market, mode, symbol, kind);

        public void Dispose() => Application.Dispose();
    }

    /// <summary>What follows a price in a test, and keeps what it heard.</summary>
    private sealed class Follows : IDisposable
    {
        private readonly List<WatchedPrice> _heard = [];
        private readonly IDisposable _following;

        public Follows(IExchangePriceWatch watch, Market market, TradingMode mode, string symbol, ExchangePriceKind kind)
        {
            _following = watch.Follow(
                market,
                mode,
                symbol,
                kind,
                price =>
                {
                    lock (_heard)
                    {
                        _heard.Add(price);
                    }
                });
        }

        public IReadOnlyList<WatchedPrice> Heard
        {
            get
            {
                lock (_heard)
                {
                    return [.. _heard];
                }
            }
        }

        public Task HeardAsync(int prices) =>
            Wait.UntilAsync(() => Heard.Count >= prices, $"{prices} prices to be heard. There were {Heard.Count}");

        public void Dispose() => _following.Dispose();
    }

    /// <summary>Stands for the limiter, where a test says whether a limit has room.</summary>
    private sealed class Room : IExchangeLimits
    {
        private readonly List<(Market Market, TradingMode Environment)> _asked = [];

        public bool Has { get; set; } = true;

        public IReadOnlyList<(Market Market, TradingMode Environment)> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _asked];
                }
            }
        }

        public IReadOnlyList<ExchangeLimitUsage> Usage() => [];

        public bool HasRoom(Market market, TradingMode environment)
        {
            lock (_asked)
            {
                _asked.Add((market, environment));
            }

            return Has;
        }
    }
}
