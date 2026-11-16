using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The application behind a Binance of the test's, on a clock that only the test moves, with the
/// timers that run on it. Binance answers a call for candles with those that it has when the
/// clock shows what it shows, so a candle closes when a test says so. A test can hold Binance's
/// answers back, to ask again while a read is under way.
/// </summary>
internal sealed class MarketService : IDisposable
{
    /// <summary>The symbol that a test asks for, where it does not matter which. The configuration file of the tests lists it.</summary>
    public const string Symbol = "BTCUSDT";

    /// <summary>The other symbol of that file.</summary>
    public const string OtherSymbol = "SOLUSDT";

    private volatile TaskCompletionSource? _heldBack;

    /// <param name="now">What the clock shows as the application starts, when that is not <see cref="BinanceCalls.Now"/>.</param>
    /// <param name="database">The database, when it is not the shared one of the test server.</param>
    public MarketService(DateTimeOffset? now = null, TestDatabase? database = null)
    {
        Clock = new FakeTimeProvider(now ?? BinanceCalls.Now);
        Binance = new StandInSystem(async (request, givenUp) =>
        {
            if (_heldBack is { } heldBack)
            {
                await heldBack.Task.WaitAsync(givenUp);
            }

            return Answer?.Invoke(request) ?? Documented(request);
        });
        Application = BinanceCalls.Behind(Binance, clock: Clock, database: database);
        Data = Application.Services.GetRequiredService<IExchangeMarketData>();
    }

    public FakeTimeProvider Clock { get; }

    public StandInSystem Binance { get; }

    public LoggedApplication Application { get; }

    public IExchangeMarketData Data { get; }

    /// <summary>What Binance answers a call with, when it is not what its documentation gives: null is that.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Answer { get; set; }

    /// <summary>
    /// What Binance's documentation gives as the answer to a call. Candles are those of the
    /// length that the call asks for, as Binance has them when the clock shows what it shows.
    /// </summary>
    public HttpResponseMessage Documented(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;

        return StandInSystem.Json(
            HttpStatusCode.OK,
            path.EndsWith("/klines", StringComparison.Ordinal)
                ? BinanceCalls.CandlesAt(Clock.GetUtcNow(), BinanceCalls.IntervalOf(request))
                : BinanceCalls.AnswerTo(path));
    }

    /// <summary>How many calls reached Binance at a path.</summary>
    public int CallsTo(string path) => Binance.Calls.Count(call => call.Address.AbsolutePath == path);

    /// <summary>The candles of a quarter of an hour of <see cref="Symbol"/> on the Spot market of the real exchange.</summary>
    public Task<ExchangeCandles> CandlesAsync(
        Market market = Market.Spot,
        TradingMode mode = TradingMode.Live,
        string symbol = Symbol,
        CandleInterval interval = CandleInterval.FifteenMinutes,
        CancellationToken? cancellationToken = null) =>
        Data.GetCandlesAsync(market, mode, symbol, interval, cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Holds back every answer of Binance from now on, until <see cref="Release"/>.</summary>
    public void HoldBack() => _heldBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets Binance answer the calls that it holds back, and those that follow.</summary>
    public void Release()
    {
        var heldBack = _heldBack;
        _heldBack = null;
        heldBack?.TrySetResult();
    }

    /// <summary>Waits until a number of calls has reached Binance.</summary>
    public Task ReachedAsync(int calls) =>
        Wait.UntilAsync(() => Binance.Calls.Count >= calls, $"{calls} calls to reach Binance");

    public void Advance(TimeSpan time) => Clock.Advance(time);

    public void Dispose()
    {
        // A call that is still held back ends with the application.
        Release();
        Application.Dispose();
    }
}
