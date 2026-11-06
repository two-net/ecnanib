using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// The price, the top of the book and Futures funding, shared by the cycles that close a candle together
/// (FR-CYC-09). A fetch is reused when it began at or after the candle's close and less than
/// <see cref="MaximumAge"/> ago, so cycles spread by jitter share it, but a catch-up cycle minutes later
/// doesn't get an old price. Binance's list of adjusted funding intervals covers every symbol, so it's
/// shared per environment by the same rule: one call per close, whatever the number of symbols. Callers
/// share a fetch none of them can cancel, as with candles. Nothing here is persisted: the cycle's trace
/// keeps what it used.
/// </summary>
internal sealed class QuoteFeed(IExchangeMarketData exchange, TimeProvider time)
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<(Market, TradingEnvironment, string), Fetch<(Quote, FuturesData?)>> _latest = [];
    private readonly Dictionary<TradingEnvironment, Fetch<IReadOnlyDictionary<string, int>>> _fundingIntervals = [];

    /// <exception cref="ExchangeException">Binance couldn't provide it.</exception>
    public Task<(Quote Quote, FuturesData? Futures)> GetAsync(
        Market market,
        TradingEnvironment environment,
        string symbol,
        DateTimeOffset candleClose,
        CancellationToken cancellationToken)
    {
        var key = (market, environment, symbol);
        var now = time.GetUtcNow();
        Task<(Quote, FuturesData?)> fetch;

        lock (_lock)
        {
            if (_latest.TryGetValue(key, out var latest) && latest.Reusable(candleClose, now))
            {
                fetch = latest.Task;
            }
            else
            {
                var intervals = market == Market.Futures ? FundingIntervals(environment, candleClose, now) : null;
                fetch = FetchAsync(market, environment, symbol, intervals);
                _latest[key] = new Fetch<(Quote, FuturesData?)>(now, fetch);
            }
        }

        return fetch.WaitAsync(cancellationToken);
    }

    /// <summary>The environment's adjusted funding intervals, shared like a quote. Called under the lock.</summary>
    private Task<IReadOnlyDictionary<string, int>> FundingIntervals(TradingEnvironment environment, DateTimeOffset candleClose, DateTimeOffset now)
    {
        if (_fundingIntervals.TryGetValue(environment, out var latest) && latest.Reusable(candleClose, now))
        {
            return latest.Task;
        }

        var fetch = FetchFundingIntervalsAsync(environment);
        _fundingIntervals[environment] = new Fetch<IReadOnlyDictionary<string, int>>(now, fetch);

        return fetch;
    }

    private async Task<IReadOnlyDictionary<string, int>> FetchFundingIntervalsAsync(TradingEnvironment environment)
    {
        await Task.Yield();

        return await exchange.GetFundingIntervalsAsync(environment, CancellationToken.None);
    }

    private async Task<(Quote, FuturesData?)> FetchAsync(
        Market market, TradingEnvironment environment, string symbol, Task<IReadOnlyDictionary<string, int>>? fundingIntervals)
    {
        await Task.Yield();

        var quote = exchange.GetQuoteAsync(market, environment, symbol, CancellationToken.None);
        var premium = market == Market.Futures ? exchange.GetPremiumIndexAsync(environment, symbol, CancellationToken.None) : null;
        await Task.WhenAll(premium is null || fundingIntervals is null ? [quote] : [quote, premium, fundingIntervals]);

        var book = await quote;
        var index = premium is null ? null : await premium;
        var interval = fundingIntervals is null
            ? IExchangeMarketData.DefaultFundingIntervalHours
            : (await fundingIntervals).GetValueOrDefault(symbol, IExchangeMarketData.DefaultFundingIntervalHours);

        return (
            new Quote(book.LastPrice, book.BidPrice, book.BidQuantity, book.AskPrice, book.AskQuantity, book.FetchedAt),
            index is null ? null : new FuturesData(index.MarkPrice, index.IndexPrice, index.FundingRate, index.NextFundingTime, index.FetchedAt, interval));
    }

    private sealed record Fetch<T>(DateTimeOffset StartedAt, Task<T> Task)
    {
        /// <summary>It began at or after the candle's close, less than <see cref="MaximumAge"/> ago, and hasn't failed.</summary>
        public bool Reusable(DateTimeOffset candleClose, DateTimeOffset now) =>
            StartedAt >= candleClose && now - StartedAt < MaximumAge && !Task.IsFaulted && !Task.IsCanceled;
    }
}
