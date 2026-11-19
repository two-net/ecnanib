using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The market data of Binance, read once and shared by all who need it (FR-BIN-3): candles,
/// prices, and the rules of the symbols, and on the Futures market the mark price with its
/// funding, and the open interest. Binance counts its limit on requests against the address of
/// the service, so a read for each agent would spend the budget of all users. See
/// docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// A read is the service's own call: it needs no credential, and takes the turn of the service
/// at the limiter. <see cref="BinanceCall"/> makes it, and the calls it can make are those of
/// <see cref="BinanceEndpoint"/>. Each kind of data is shared with a <see cref="BinanceShared{TKey, TValue}"/>,
/// which keeps what was read in memory for as long as it counts, and no longer. Nothing of it
/// is written to the database, and there is no range of the past to ask for (NFR-DAT-6).
/// </para>
/// <para>
/// A read that failed is shared as well, for a few seconds: agents that ask one after the other
/// while Binance does not answer do not each make the call.
/// </para>
/// <para>
/// Only the symbols of the configuration file are served. So what is held has a bound, and no
/// text of a caller is sent to Binance. The price that a symbol last traded at is served for
/// the symbols that price a fee as well, <c>Binance:FeeSymbols</c>, and nothing else of those:
/// they are priced, and not traded (FR-BIN-10).
/// </para>
/// </remarks>
internal sealed class BinanceMarketData : IExchangeMarketData, IDisposable
{
    /// <summary>
    /// The longest that candles are held. A cycle takes its candles as it starts, and the cycles
    /// that a close starts do so within half a minute (NFR-PRF-3). This is twice that.
    /// </summary>
    public static readonly TimeSpan CandlesCount = TimeSpan.FromMinutes(1);

    /// <summary>For how long a price, a mark price, and an open interest count: they are of the moment.</summary>
    public static readonly TimeSpan MomentCounts = TimeSpan.FromSeconds(1);

    /// <summary>For how long the rules of the symbols count. Binance changes them seldom, and says so before.</summary>
    public static readonly TimeSpan SymbolsCount = TimeSpan.FromHours(1);

    /// <summary>For how long a read that failed is not made again.</summary>
    public static readonly TimeSpan FailureCounts = TimeSpan.FromSeconds(5);

    private readonly BinanceCall _call;
    private readonly BinanceSettings _settings;
    private readonly HashSet<string> _listed;
    private readonly HashSet<string> _priced;

    private readonly BinanceShared<(Market Market, TradingMode Mode, string Symbol, CandleInterval Interval), ExchangeCandles> _candles;
    private readonly BinanceShared<(Market Market, TradingMode Mode, string Symbol), ExchangePrice> _prices;
    private readonly BinanceShared<(Market Market, TradingMode Mode), ExchangeSymbols> _symbols;
    private readonly BinanceShared<(TradingMode Mode, string Symbol), ExchangeMarkPrice> _markPrices;
    private readonly BinanceShared<(TradingMode Mode, string Symbol), ExchangeOpenInterest> _openInterests;

    public BinanceMarketData(BinanceCall call, BinanceSettings settings, TimeProvider clock, IHostApplicationLifetime lifetime)
    {
        _call = call;
        _settings = settings;
        _listed = new HashSet<string>(settings.Symbols, StringComparer.Ordinal);
        _priced = new HashSet<string>(settings.Symbols.Concat(settings.FeeSymbols), StringComparer.Ordinal);

        // A read ends when the application stops, and not when an asker cancels.
        var stopping = lifetime.ApplicationStopping;

        _candles = new(clock, stopping);
        _prices = new(clock, stopping);
        _symbols = new(clock, stopping);
        _markPrices = new(clock, stopping);
        _openInterests = new(clock, stopping);
    }

    public Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        var endpoint = BinanceEndpoint.CandlesOf(market, Listed(symbol), interval);
        Known(market, mode);

        return _candles.AskAsync(
            (market, mode, symbol, interval),
            async stopping =>
            {
                var answer = await _call.AskAsync(endpoint, mode, BinanceMarketAnswers.Candles, stopping);

                if (answer.Value is not { } rows)
                {
                    return new(new ExchangeCandles(answer.Outcome, [], null), FailureCounts);
                }

                // No candle at all: the symbol has not traded yet.
                if (rows.Count == 0)
                {
                    return new(new ExchangeCandles(answer.Outcome, [], answer.SentAt), CandlesCount);
                }

                // Which candle is closed is Binance's word: one that another follows. The newest
                // is the one in progress, which is never given, and its close is when a cycle
                // needs a newer read. When Binance has not begun the next candle yet, the newest
                // is one that the clock says is over: its close is behind, so what was read is
                // given to who waited, one candle short, and is not held.
                var inProgress = rows[^1];
                rows.RemoveAt(rows.Count - 1);

                return new(
                    new ExchangeCandles(answer.Outcome, rows.AsReadOnly(), answer.SentAt), CandlesCount, Until: inProgress.ClosedAt);
            },
            cancellationToken);
    }

    public Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        var endpoint = BinanceEndpoint.PriceOf(market, Priced(symbol));
        Known(market, mode);

        return _prices.AskAsync(
            (market, mode, symbol),
            async stopping =>
            {
                var answer = await _call.AskAsync(endpoint, mode, BinanceMarketAnswers.Price, stopping);

                return answer.Value is { } price
                    ? new(new ExchangePrice(answer.Outcome, price, answer.SentAt), MomentCounts)
                    : new(new ExchangePrice(answer.Outcome, null, null), FailureCounts);
            },
            cancellationToken);
    }

    public Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken)
    {
        var endpoint = BinanceEndpoint.SymbolsOf(market);
        Known(market, mode);

        return _symbols.AskAsync(
            (market, mode),
            async stopping =>
            {
                var answer = await _call.AskAsync(
                    endpoint, mode, body => BinanceMarketAnswers.Symbols(body, market, _settings.Symbols), stopping);

                return answer.Value is { } symbols
                    ? new(new ExchangeSymbols(answer.Outcome, symbols.AsReadOnly(), answer.SentAt), SymbolsCount)
                    : new(new ExchangeSymbols(answer.Outcome, [], null), FailureCounts);
            },
            cancellationToken);
    }

    public Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        var endpoint = BinanceEndpoint.MarkPriceOf(Listed(symbol));
        Known(Market.Futures, mode);

        return _markPrices.AskAsync(
            (mode, symbol),
            async stopping =>
            {
                var answer = await _call.AskAsync(endpoint, mode, BinanceMarketAnswers.MarkPrice, stopping);

                return answer.Value is { } read
                    ? new(new ExchangeMarkPrice(answer.Outcome, read.MarkPrice, read.FundingRate, read.NextFundingAt, answer.SentAt), MomentCounts)
                    : new(new ExchangeMarkPrice(answer.Outcome, null, null, null, null), FailureCounts);
            },
            cancellationToken);
    }

    public Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        var endpoint = BinanceEndpoint.OpenInterestOf(Listed(symbol));
        Known(Market.Futures, mode);

        return _openInterests.AskAsync(
            (mode, symbol),
            async stopping =>
            {
                var answer = await _call.AskAsync(endpoint, mode, BinanceMarketAnswers.OpenInterest, stopping);

                return answer.Value is { } openInterest
                    ? new(new ExchangeOpenInterest(answer.Outcome, openInterest, answer.SentAt), MomentCounts)
                    : new(new ExchangeOpenInterest(answer.Outcome, null, null), FailureCounts);
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _candles.Dispose();
        _prices.Dispose();
        _symbols.Dispose();
        _markPrices.Dispose();
        _openInterests.Dispose();
    }

    /// <summary>
    /// A symbol that the configuration file lists. Any other is no call: it is said without
    /// what was given, and nothing is sent.
    /// </summary>
    private string Listed(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return _listed.Contains(symbol)
            ? symbol
            : throw new ArgumentException("The symbol is not one of Binance:Symbols of the configuration file.", nameof(symbol));
    }

    /// <summary>
    /// A symbol whose price is served: one that the configuration file lists, to be traded or to
    /// price a fee. Any other is no call.
    /// </summary>
    private string Priced(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return _priced.Contains(symbol)
            ? symbol
            : throw new ArgumentException(
                "The symbol is not one of Binance:Symbols or Binance:FeeSymbols of the configuration file.", nameof(symbol));
    }

    /// <summary>Says so before anything is held or sent when a market has no such environment.</summary>
    private void Known(Market market, TradingMode mode) => _ = _settings.BaseUrl(market, mode);
}
