using System.Net;
using System.Runtime.CompilerServices;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Candles are cached only for as long as cycles need them, and the database is not a
/// market-data warehouse (NFR-DAT-6). A cycle takes its candles as it starts, and the cycles
/// that a close starts do so within half a minute (NFR-PRF-3). So candles are held in memory
/// for a minute at most, and never past the close of the candle that is in progress, after which
/// a cycle needs newer ones. What is no longer held is given to nobody and is let go of. Nothing
/// of the market data is written to the database.
/// </summary>
/// <remarks>
/// The clock is the test's, with the timers that run on it, and Binance answers with the candles
/// that it has when the clock shows what it shows. A candle of a quarter of an hour is in
/// progress at 09:30, which closes at 09:45.
/// </remarks>
public class CandleCacheTests
{
    private const string Candles = "/api/v3/klines";

    private static readonly DateTimeOffset Day = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Candles_are_held_for_a_minute_and_no_longer()
    {
        using var service = new MarketService();

        var read = await service.CandlesAsync();
        Assert.Equal(Day.AddHours(9.5), read.Candles[^1].ClosedAt);

        // The cycles that the close started take them, without a call.
        service.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(read.Candles, (await service.CandlesAsync()).Candles);
        Assert.Equal(1, service.CallsTo(Candles));

        // After the minute, whoever asks causes a read. Nothing has closed since, so it reads
        // the same candles, and they are other ones than those that were held.
        service.Advance(TimeSpan.FromSeconds(2));
        var fresh = await service.CandlesAsync();

        Assert.Equal(read.Candles, fresh.Candles);
        Assert.NotSame(read.Candles, fresh.Candles);
        Assert.Equal(BinanceCalls.Now.AddSeconds(61), fresh.ReadAt);
        Assert.Equal(2, service.CallsTo(Candles));
    }

    [Fact]
    public async Task Candles_are_not_given_past_the_close_of_the_candle_that_is_in_progress()
    {
        // Half a minute before a candle closes.
        using var service = new MarketService(now: Day.AddHours(9.75).AddSeconds(-30));

        var before = await service.CandlesAsync();
        Assert.Equal(Day.AddHours(9.5), before.Candles[^1].ClosedAt);

        // Up to the close they are the newest closed candles there are.
        service.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(before.Candles, (await service.CandlesAsync()).Candles);
        Assert.Equal(1, service.CallsTo(Candles));

        // The candle has closed, well within the minute: a cycle that it starts needs it, so
        // what was held does not count, and the cycle causes a read that has it.
        service.Advance(TimeSpan.FromSeconds(2));
        var after = await service.CandlesAsync();

        Assert.Equal(Day.AddHours(9.75), after.Candles[^1].ClosedAt);
        Assert.Equal(2, service.CallsTo(Candles));
    }

    [Fact]
    public async Task The_candle_that_binance_has_not_closed_yet_is_not_given_and_what_is_short_of_it_is_not_held()
    {
        // A tenth of a second after a close, by the clock of the service. Binance's clock is a
        // little behind, and it has not begun the next candle: the newest row of its answer is
        // the candle that the service takes for closed.
        var close = Day.AddHours(9.75);
        using var service = new MarketService(now: close.AddMilliseconds(100));
        var behind = true;
        service.Answer = request => behind && request.RequestUri!.AbsolutePath == Candles
            ? StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.CandlesAt(close.AddMilliseconds(-200), BinanceCalls.IntervalOf(request)))
            : null;

        // Which candle is closed is Binance's word, and not the clock's: the candle of 09:45 is
        // not given, and the newest says so. It is no failure.
        var early = await service.CandlesAsync();
        Assert.Equal(ExchangeCallOutcome.Succeeded, early.Outcome);
        Assert.Equal(close.AddMinutes(-15), early.Candles[^1].ClosedAt);

        // A cycle that needs the candle of 09:45 asks again, and that is a read again: what
        // came short of the close is held for nobody.
        var again = await service.CandlesAsync();
        Assert.Equal(close.AddMinutes(-15), again.Candles[^1].ClosedAt);
        Assert.Equal(2, service.CallsTo(Candles));

        // Binance has begun the next candle. Now the candle of 09:45 is closed, and is held.
        behind = false;
        var closed = await service.CandlesAsync();
        Assert.Equal(close, closed.Candles[^1].ClosedAt);
        Assert.Same(closed.Candles, (await service.CandlesAsync()).Candles);
        Assert.Equal(3, service.CallsTo(Candles));
    }

    [Fact]
    public async Task Candles_that_are_no_longer_held_are_let_go_of_without_another_ask()
    {
        using var service = new MarketService();

        var candles = await ReadAsync(service);

        // While they are held, the gateway is what keeps them: the test has let go of them.
        Collect();
        Assert.True(candles.IsAlive);

        // The minute passes, and nobody asks. They are gone from memory.
        service.Advance(TimeSpan.FromSeconds(61));
        Collect();

        Assert.False(candles.IsAlive);
        Assert.Equal(1, service.CallsTo(Candles));
    }

    [Fact]
    public async Task Nothing_of_the_market_data_is_written_to_the_database()
    {
        var database = PostgresServer.CreateDatabase();
        using var service = new MarketService(now: Day.AddHours(9.75).AddSeconds(-30), database: database);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The application has started on its database, which it has migrated.
        _ = service.Application.Services;
        var tables = database.Tables();
        var stored = EverythingStored(database);

        // Everything is read, for every symbol of the configuration file, in each market and
        // each environment: before a candle closes, and twice after it, each time when nothing
        // of the time before is held any more.
        foreach (var passes in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(61), TimeSpan.FromMinutes(2) })
        {
            service.Advance(passes);

            foreach (var symbol in new[] { MarketService.Symbol, MarketService.OtherSymbol })
            {
                foreach (var (market, mode) in BinanceCalls.Pairs)
                {
                    foreach (var (interval, _, _) in BinanceCalls.Intervals)
                    {
                        Assert.NotEmpty((await service.Data.GetCandlesAsync(market, mode, symbol, interval, cancellationToken)).Candles);
                    }

                    Assert.NotNull((await service.Data.GetPriceAsync(market, mode, symbol, cancellationToken)).Price);
                    Assert.NotEmpty((await service.Data.GetSymbolsAsync(market, mode, cancellationToken)).Symbols);
                }

                foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
                {
                    Assert.NotNull((await service.Data.GetMarkPriceAsync(mode, symbol, cancellationToken)).MarkPrice);
                    Assert.NotNull((await service.Data.GetOpenInterestAsync(mode, symbol, cancellationToken)).OpenInterest);
                }
            }
        }

        // Binance was read from, three times over.
        Assert.Equal(3 * 2 * 4 * 5, service.CallsTo(Candles) + service.CallsTo("/fapi/v1/klines"));

        // The database has the tables that it had, which are those of the model: no table of
        // candles. And every table holds the rows that it held.
        Assert.Equal(tables, database.Tables());
        Assert.Equal(stored, EverythingStored(database));

        using var context = database.Context();
        Assert.Equal(
            context.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Distinct().Append("__EFMigrationsHistory").Order(StringComparer.Ordinal),
            database.Tables());
    }

    /// <summary>
    /// Every row of every table, as the database would hand it to whoever reads it. But for the
    /// state of the scheduled jobs: a job writes its row in its own time, which is nothing that
    /// a read of market data did.
    /// </summary>
    private static string EverythingStored(TestDatabase database) =>
        string.Join(
            Environment.NewLine,
            database.Tables().Where(table => table != "background_jobs").SelectMany(table =>
                database.Column($"""SELECT row_to_json(stored)::text FROM "{table}" stored""")));

    /// <summary>
    /// Reads candles and keeps no hold of them: what it gives says whether anything still does.
    /// It is a method of its own, so that nothing of the test's keeps them in memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReadAsync(MarketService service)
    {
        var read = await service.CandlesAsync();
        Assert.NotEmpty(read.Candles);

        return new WeakReference(read.Candles);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
