using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.MarketData;

/// <summary>
/// The application with its own database, clock and <see cref="FakeBinance"/>, which answers candles from
/// <see cref="BinanceSamples.Klines"/> and quotes from the samples. Market Data keeps what it shares in
/// memory, so each test gets its own. A second app on the same database stands in for a restart.
/// </summary>
internal sealed class MarketDataApp : IAsyncDisposable
{
    public const string Symbol = "BTCUSDT";

    private static readonly string[] KlinePaths = ["/api/v3/klines", "/fapi/v1/klines"];

    public MarketDataApp(PostgresFixture postgres, Dictionary<string, string?>? settings = null, string? connectionString = null)
    {
        Exchange = new ExchangeHarness(postgres, settings, connectionString: connectionString);
        AnswerKlines();
        Binance.On(HttpMethod.Get, "/api/v3/ticker/24hr", _ => FakeBinance.Json(BinanceSamples.SpotTicker24h));
        Binance.On(HttpMethod.Get, "/fapi/v1/ticker/bookTicker", _ => FakeBinance.Json(BinanceSamples.FuturesBookTicker));
        Binance.On(HttpMethod.Get, "/fapi/v1/ticker/price", _ => FakeBinance.Json(BinanceSamples.FuturesPrice));
        Binance.On(HttpMethod.Get, "/fapi/v1/premiumIndex", _ => FakeBinance.Json(BinanceSamples.PremiumIndex()));
    }

    public ExchangeHarness Exchange { get; }

    public FakeBinance Binance => Exchange.Binance;

    public TestClock Clock => Exchange.Clock;

    public IMarketData MarketData => Exchange.App.Services.GetRequiredService<IMarketData>();

    public IExchangeSymbols Symbols => Exchange.App.Services.GetRequiredService<IExchangeSymbols>();

    public string ConnectionString => Exchange.App.ConnectionString();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Candles as Binance sends them, without those <paramref name="missing"/> picks out.</summary>
    public void AnswerKlines(Func<DateTimeOffset, bool>? missing = null)
    {
        foreach (var path in KlinePaths)
        {
            Binance.On(HttpMethod.Get, path, request => FakeBinance.Json(BinanceSamples.Klines(request, Clock.GetUtcNow(), missing)));
        }
    }

    /// <summary>The candle requests for one interval, such as 1h, in the order sent.</summary>
    public IReadOnlyList<RecordedRequest> Klines(string interval) =>
        [.. Binance.Requests.Where(request => KlinePaths.Contains(request.Path) && request.Parameter("interval") == interval)];

    /// <summary>When the latest candle of <paramref name="interval"/> closed.</summary>
    public DateTimeOffset LastClose(string interval) => BinanceSamples.Floor(interval, Clock.GetUtcNow());

    public Task<MarketSnapshot> CollectAsync(
        Timeframe timeframe,
        DateTimeOffset close,
        Market market = Market.Spot,
        TradingEnvironment environment = TradingEnvironment.Live,
        CancellationToken? cancellationToken = null) =>
        MarketData.CollectAsync(new MarketDataRequest(market, environment, Symbol, timeframe, close), cancellationToken ?? Cancellation);

    public ValueTask DisposeAsync() => Exchange.DisposeAsync();
}
