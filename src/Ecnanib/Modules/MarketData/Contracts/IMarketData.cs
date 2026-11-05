using Ecnanib.Kernel;

namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>
/// Steps 3 and 4 of a decision cycle (FR-CYC-02): candles for the agent's timeframe and its higher
/// timeframe, the price and the top of the book, Futures funding, and indicators computed from the
/// candles. Public data is fetched once per symbol, timeframe and candle, and shared by every agent
/// that needs it (FR-CYC-09). The position's liquidation price is the account's, so it comes from
/// reconciliation, not from here. See Market data in docs/architecture.md.
/// </summary>
public interface IMarketData
{
    /// <summary>
    /// Everything a cycle needs about its symbol at the close of its candle. It waits for a fetch other
    /// agents started, and a caller that stops waiting doesn't stop that fetch.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, or a candle that hasn't closed yet.</exception>
    /// <exception cref="MarketDataException">Binance couldn't provide the data.</exception>
    Task<MarketSnapshot> CollectAsync(MarketDataRequest request, CancellationToken cancellationToken);
}

/// <param name="Symbol">Such as BTCUSDT.</param>
/// <param name="CandleClose">
/// When the cycle's candle closed: the cycle's scheduled time, without its delay (FR-CYC-01). A later time
/// within the next candle means the same candle. Every candle returned closed by then.
/// </param>
public sealed record MarketDataRequest(Market Market, TradingEnvironment Environment, string Symbol, Timeframe Timeframe, DateTimeOffset CandleClose);

/// <param name="Candles">The agent's timeframe.</param>
/// <param name="HigherTimeframe">The context SRS A.1 gives the timeframe: 1H for 15m, 1D for 1H and 4H, and 1W for 1D.</param>
/// <param name="Futures">Null on Spot.</param>
public sealed record MarketSnapshot(
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    Timeframe Timeframe,
    CandleSeries Candles,
    CandleSeries HigherTimeframe,
    Quote Quote,
    FuturesData? Futures);

/// <summary>
/// Closed candles, oldest first, up to the candle that closed at the request's time, and the indicators
/// computed from them. There are at most <see cref="Window"/>, fewer when the symbol is newer than that.
/// When Binance has no candle for a time, as during maintenance, there's a gap, so check the last
/// candle's open time against <see cref="LastOpenTime"/>.
/// </summary>
/// <param name="Interval">The candles' length, such as 1 hour, or 7 days for 1W.</param>
/// <param name="LastOpenTime">The open time of the candle that closed at the request's time.</param>
public sealed record CandleSeries(TimeSpan Interval, DateTimeOffset LastOpenTime, IReadOnlyList<Candle> Candles, IndicatorSet Indicators)
{
    /// <summary>The most candles a series holds: enough for EMA 200 with room to settle.</summary>
    public const int Window = 500;
}

/// <summary>
/// One closed candle. Prices are in the symbol's quote asset, and <paramref name="Volume"/> in its base
/// asset. <see cref="IExchangeSymbols"/> names both.
/// </summary>
/// <param name="QuoteVolume">The volume in the quote asset.</param>
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    long Trades);

/// <summary>The last price and the top of the book. Prices are in the quote asset, and quantities in the base asset.</summary>
/// <param name="At">When it was fetched, by this server's clock.</param>
public sealed record Quote(decimal LastPrice, decimal BidPrice, decimal BidQuantity, decimal AskPrice, decimal AskQuantity, DateTimeOffset At);

/// <summary>A Futures symbol's mark price and funding.</summary>
/// <param name="FundingRate">The last funding rate as a fraction, such as 0.0001. Null for a contract without funding.</param>
/// <param name="NextFundingTime">Null for a contract without funding.</param>
/// <param name="At">When it was fetched, by this server's clock.</param>
public sealed record FuturesData(decimal MarkPrice, decimal IndexPrice, decimal? FundingRate, DateTimeOffset? NextFundingTime, DateTimeOffset At);
