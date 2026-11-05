using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// The price, the top of the book and Futures funding, shared by the cycles that close a candle together
/// (FR-CYC-09). A fetch is reused when it began at or after the candle's close and less than
/// <see cref="MaximumAge"/> ago, so cycles spread by jitter share it, but a catch-up cycle minutes later
/// doesn't get an old price. Callers share a fetch none of them can cancel, as with candles. Nothing here
/// is persisted: the cycle's trace keeps what it used.
/// </summary>
internal sealed class QuoteFeed(IExchangeMarketData exchange, TimeProvider time)
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<(Market, TradingEnvironment, string), Fetch> _latest = [];

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
            if (_latest.TryGetValue(key, out var latest)
                && latest.StartedAt >= candleClose
                && now - latest.StartedAt < MaximumAge
                && !latest.Task.IsFaulted
                && !latest.Task.IsCanceled)
            {
                fetch = latest.Task;
            }
            else
            {
                fetch = FetchAsync(market, environment, symbol);
                _latest[key] = new Fetch(now, fetch);
            }
        }

        return fetch.WaitAsync(cancellationToken);
    }

    private async Task<(Quote, FuturesData?)> FetchAsync(Market market, TradingEnvironment environment, string symbol)
    {
        await Task.Yield();

        var quote = exchange.GetQuoteAsync(market, environment, symbol, CancellationToken.None);
        var premium = market == Market.Futures ? exchange.GetPremiumIndexAsync(environment, symbol, CancellationToken.None) : null;
        await Task.WhenAll(premium is null ? [quote] : [quote, premium]);

        var book = await quote;
        var index = premium is null ? null : await premium;

        return (
            new Quote(book.LastPrice, book.BidPrice, book.BidQuantity, book.AskPrice, book.AskQuantity, book.FetchedAt),
            index is null ? null : new FuturesData(index.MarkPrice, index.IndexPrice, index.FundingRate, index.NextFundingTime, index.FetchedAt));
    }

    private sealed record Fetch(DateTimeOffset StartedAt, Task<(Quote, FuturesData?)> Task);
}
