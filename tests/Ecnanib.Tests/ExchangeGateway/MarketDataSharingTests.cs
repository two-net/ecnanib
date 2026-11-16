using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Market data is fetched once per symbol and timeframe, and shared by all agents that need it
/// (FR-BIN-3): Binance counts its limit on requests against the address of the service, so a
/// fetch for each agent would spend the budget of all users (risk R2). Whoever asks first causes
/// the read, whoever asks while it is under way waits for the same read, and whoever asks while
/// what was read still counts gets it with no call. How a read ended is shared as well.
/// </summary>
/// <remarks>
/// The clock is the test's, with the timers that run on it. An agent is whatever asks: nothing
/// that runs a cycle is built yet, and the gateway does not know who asks.
/// </remarks>
public class MarketDataSharingTests
{
    private const string Symbol = MarketService.Symbol;

    // Each kind of market data: what asks for it, and the call that reads it.
    public static TheoryData<string, string> Kinds => new()
    {
        { "Candles", "/api/v3/klines" },
        { "Price", "/fapi/v2/ticker/price" },
        { "Symbols", "/api/v3/exchangeInfo" },
        { "MarkPrice", "/fapi/v1/premiumIndex" },
        { "OpenInterest", "/fapi/v1/openInterest" },
    };

    // The figures of the moment.
    public static TheoryData<string, string> Moments => new()
    {
        { "Price", "/fapi/v2/ticker/price" },
        { "MarkPrice", "/fapi/v1/premiumIndex" },
        { "OpenInterest", "/fapi/v1/openInterest" },
    };

    [Fact]
    public async Task Two_agents_on_the_same_symbol_and_timeframe_cause_one_fetch_and_not_two()
    {
        using var service = new MarketService();

        // Both ask as a candle has closed, and Binance has not answered the first yet.
        service.HoldBack();
        var first = service.CandlesAsync();
        var second = service.CandlesAsync();
        await service.ReachedAsync(1);
        Assert.False(first.IsCompleted || second.IsCompleted);

        service.Release();

        // Both get the candles, and they are the same ones: of the one read.
        var candles = await first;
        Assert.Equal(ExchangeCallOutcome.Succeeded, candles.Outcome);
        Assert.Equal(BinanceCalls.ClosedAt(BinanceCalls.Now, CandleInterval.FifteenMinutes), candles.Candles);
        Assert.Same(candles.Candles, (await second).Candles);

        // A third that asks a moment later, when the read is over, gets them too.
        service.Advance(TimeSpan.FromSeconds(10));
        Assert.Same(candles.Candles, (await service.CandlesAsync()).Candles);

        Assert.Single(service.Binance.Calls);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Whoever_asks_while_a_read_is_under_way_waits_for_the_same_read(string kind, string path)
    {
        using var service = new MarketService();

        service.HoldBack();
        var asked = Enumerable.Range(0, 5).Select(_ => AskAsync(service, kind)).ToList();
        await service.ReachedAsync(1);

        service.Release();
        var given = await Task.WhenAll(asked);

        // Five asked, and Binance was asked once. Each got what the one read gave.
        Assert.All(given, one => Assert.Equal(ExchangeCallOutcome.Succeeded, one.Outcome));
        Assert.All(given, one => Assert.Same(given[0].Read, one.Read));
        Assert.Equal(path, Assert.Single(service.Binance.Calls).Address.AbsolutePath);
    }

    [Fact]
    public async Task Another_symbol_timeframe_market_or_environment_is_a_read_of_its_own()
    {
        using var service = new MarketService();

        Task<ExchangeCandles>[] Asked() =>
        [
            service.CandlesAsync(),
            service.CandlesAsync(symbol: MarketService.OtherSymbol),
            service.CandlesAsync(interval: CandleInterval.OneHour),
            service.CandlesAsync(market: Market.Futures),
            service.CandlesAsync(mode: TradingMode.Demo),
        ];

        var first = await Task.WhenAll(Asked());
        var again = await Task.WhenAll(Asked());

        // Five reads, each to the address of its market in its environment, for its symbol and
        // its timeframe. Asking for all of them again reads nothing.
        Assert.Equal(
            [
                "https://futures-live.example/fapi/v1/klines?symbol=BTCUSDT&interval=15m&limit=499",
                "https://spot-demo.example/api/v3/klines?symbol=BTCUSDT&interval=15m&limit=499",
                "https://spot-live.example/api/v3/klines?symbol=BTCUSDT&interval=15m&limit=499",
                "https://spot-live.example/api/v3/klines?symbol=BTCUSDT&interval=1h&limit=499",
                "https://spot-live.example/api/v3/klines?symbol=SOLUSDT&interval=15m&limit=499",
            ],
            service.Binance.Calls.Select(call => call.Address.ToString()).Order(StringComparer.Ordinal));

        // What was read for one is not given for another.
        Assert.Equal(5, first.Select(candles => candles.Candles).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(first.Zip(again), pair => Assert.Same(pair.First.Candles, pair.Second.Candles));
    }

    [Fact]
    public async Task The_figures_of_a_symbol_are_each_read_once_for_all_who_ask_in_each_market_and_environment()
    {
        using var service = new MarketService();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Three agents on one symbol, in each market and each environment, at one moment.
        for (var agent = 0; agent < 3; agent++)
        {
            foreach (var (market, mode) in BinanceCalls.Pairs)
            {
                await service.Data.GetPriceAsync(market, mode, Symbol, cancellationToken);
                await service.Data.GetSymbolsAsync(market, mode, cancellationToken);
            }

            foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
            {
                await service.Data.GetMarkPriceAsync(mode, Symbol, cancellationToken);
                await service.Data.GetOpenInterestAsync(mode, Symbol, cancellationToken);
            }
        }

        // A price and the symbols on each of the four, and the two figures of the Futures
        // market in each environment: twelve reads, and not thirty-six.
        Assert.Equal(12, service.Binance.Calls.Count);
        Assert.Equal(12, service.Binance.Calls.Select(call => call.Address.ToString()).Distinct().Count());
    }

    [Fact]
    public async Task An_agent_that_stops_waiting_ends_nothing_for_the_others()
    {
        using var service = new MarketService();
        using var impatient = new CancellationTokenSource();

        service.HoldBack();
        var gaveUp = service.CandlesAsync(cancellationToken: impatient.Token);
        var waits = service.CandlesAsync();
        await service.ReachedAsync(1);

        // The one that cancels ends with the error of a cancelled call, and at once.
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.False(waits.IsCompleted);

        // The read goes on, and the other gets what it gives. So does whoever asks after it.
        service.Release();
        var candles = await waits;
        Assert.Equal(ExchangeCallOutcome.Succeeded, candles.Outcome);
        Assert.Same(candles.Candles, (await service.CandlesAsync()).Candles);
        Assert.Single(service.Binance.Calls);

        // One that has cancelled before it asks does not ask.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CandlesAsync(symbol: MarketService.OtherSymbol, cancellationToken: impatient.Token));
        Assert.Single(service.Binance.Calls);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_read_that_failed_is_given_to_all_who_ask_and_is_not_made_again_for_five_seconds(string kind, string path)
    {
        using var service = new MarketService();
        service.Answer = _ => StandInSystem.Json(HttpStatusCode.BadGateway, string.Empty);

        // Those who ask while Binance does not answer well, at once and one after the other.
        service.HoldBack();
        var asked = Enumerable.Range(0, 3).Select(_ => AskAsync(service, kind)).ToList();
        await service.ReachedAsync(1);
        service.Release();

        Assert.All(await Task.WhenAll(asked), one => Assert.Equal((ExchangeCallOutcome.Failed, null), (one.Outcome, one.Read)));
        Assert.Equal(ExchangeCallOutcome.Failed, (await AskAsync(service, kind)).Outcome);

        service.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(ExchangeCallOutcome.Failed, (await AskAsync(service, kind)).Outcome);
        Assert.Equal(1, service.CallsTo(path));

        // Then the next who asks causes a read again, and Binance answers.
        service.Answer = null;
        service.Advance(TimeSpan.FromSeconds(2));

        var read = await AskAsync(service, kind);
        Assert.Equal(ExchangeCallOutcome.Succeeded, read.Outcome);
        Assert.NotNull(read.Read);
        Assert.Equal(2, service.CallsTo(path));
    }

    [Theory]
    [MemberData(nameof(Moments))]
    public async Task A_figure_of_the_moment_counts_for_a_second(string kind, string path)
    {
        using var service = new MarketService();

        var read = await AskAsync(service, kind);

        // Within the second, whoever asks gets what was read, and it says when that was.
        service.Advance(TimeSpan.FromMilliseconds(900));
        var shared = await AskAsync(service, kind);
        Assert.Same(read.Read, shared.Read);
        Assert.Equal(BinanceCalls.Now, shared.ReadAt);
        Assert.Equal(1, service.CallsTo(path));

        // After it, the next who asks causes a read.
        service.Advance(TimeSpan.FromMilliseconds(200));
        var fresh = await AskAsync(service, kind);
        Assert.NotSame(read.Read, fresh.Read);
        Assert.Equal(BinanceCalls.Now.AddMilliseconds(1100), fresh.ReadAt);
        Assert.Equal(2, service.CallsTo(path));
    }

    [Fact]
    public async Task The_rules_of_the_symbols_count_for_an_hour()
    {
        // On a database of its own: the clock passes an hour, at which the jobs of the
        // application are due, and on the shared database they would find the keys of other tests.
        using var service = new MarketService(database: PostgresServer.CreateDatabase());
        const string path = "/fapi/v1/exchangeInfo";

        var read = await service.Data.GetSymbolsAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken);

        service.Advance(TimeSpan.FromMinutes(59));
        Assert.Same(
            read.Symbols,
            (await service.Data.GetSymbolsAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken)).Symbols);
        Assert.Equal(1, service.CallsTo(path));

        service.Advance(TimeSpan.FromMinutes(2));
        var fresh = await service.Data.GetSymbolsAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken);
        Assert.Equal(read.Symbols, fresh.Symbols);
        Assert.NotSame(read.Symbols, fresh.Symbols);
        Assert.Equal(2, service.CallsTo(path));
    }

    [Fact]
    public async Task Nothing_is_read_from_binance_that_nobody_asked_for()
    {
        using var service = new MarketService();

        // The application runs, and time passes: over the close of a candle, and more.
        _ = service.Application.Services;
        service.Advance(TimeSpan.FromMinutes(20));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(service.Binance.Calls);
    }

    /// <summary>Asks for one kind of market data, and gives how it ended, what was read, and when.</summary>
    private static async Task<(ExchangeCallOutcome Outcome, object? Read, DateTimeOffset? ReadAt)> AskAsync(MarketService service, string kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        switch (kind)
        {
            case "Candles":
                var candles = await service.CandlesAsync();

                return (candles.Outcome, candles.Outcome == ExchangeCallOutcome.Succeeded ? candles.Candles : null, candles.ReadAt);

            case "Price":
                var price = await service.Data.GetPriceAsync(Market.Futures, TradingMode.Live, Symbol, cancellationToken);

                return (price.Outcome, price.Outcome == ExchangeCallOutcome.Succeeded ? price : null, price.ReadAt);

            case "Symbols":
                var symbols = await service.Data.GetSymbolsAsync(Market.Spot, TradingMode.Demo, cancellationToken);

                return (symbols.Outcome, symbols.Outcome == ExchangeCallOutcome.Succeeded ? symbols.Symbols : null, symbols.ReadAt);

            case "MarkPrice":
                var markPrice = await service.Data.GetMarkPriceAsync(TradingMode.Live, Symbol, cancellationToken);

                return (markPrice.Outcome, markPrice.Outcome == ExchangeCallOutcome.Succeeded ? markPrice : null, markPrice.ReadAt);

            case "OpenInterest":
                var openInterest = await service.Data.GetOpenInterestAsync(TradingMode.Demo, Symbol, cancellationToken);

                return (openInterest.Outcome, openInterest.Outcome == ExchangeCallOutcome.Succeeded ? openInterest : null, openInterest.ReadAt);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
