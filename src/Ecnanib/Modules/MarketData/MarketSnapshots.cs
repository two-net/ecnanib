using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.MarketData;

/// <summary>Steps 3 and 4 of a cycle: both candle series and the quote, fetched together, each shared between agents.</summary>
internal sealed class MarketSnapshots(CandleFeed candles, QuoteFeed quotes, TimeProvider time, ILogger<MarketSnapshots> logger) : IMarketData
{
    public async Task<MarketSnapshot> CollectAsync(MarketDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Failures.IsSymbol(request.Symbol))
        {
            throw new ArgumentException("A symbol is upper-case letters and digits, such as BTCUSDT.", nameof(request));
        }

        var interval = Timeframes.Interval(request.Timeframe);
        var higher = Timeframes.Higher(request.Timeframe);
        var close = Timeframes.Floor(interval, request.CandleClose);

        if (close > time.GetUtcNow())
        {
            throw new ArgumentException("The candle hasn't closed yet.", nameof(request));
        }

        var (market, environment, symbol) = (request.Market, request.Environment, request.Symbol);
        var series = candles.GetAsync(new SeriesKey(market, environment, symbol, interval), close - Timeframes.Length(interval), cancellationToken);
        var context = candles.GetAsync(new SeriesKey(market, environment, symbol, higher), Timeframes.LastClosed(higher, close), cancellationToken);
        var quote = quotes.GetAsync(market, environment, symbol, close, cancellationToken);

        try
        {
            await Task.WhenAll(series, context, quote);
        }
        catch (ExchangeException exception)
        {
            var failure = Failures.From(exception);
            logger.LogWarning(
                exception,
                "Market data for {Market} {Environment} {Symbol} {Timeframe} at {CandleClose:O} couldn't be collected: {Failure}",
                market,
                environment,
                symbol,
                request.Timeframe,
                close,
                failure.Failure);

            throw failure;
        }

        var (book, futures) = await quote;

        return new MarketSnapshot(market, environment, symbol, request.Timeframe, await series, await context, book, futures);
    }
}
