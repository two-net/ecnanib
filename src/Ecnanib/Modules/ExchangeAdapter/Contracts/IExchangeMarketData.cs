using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Public exchange data, with no keys: exchange information, candles, prices, the top of the book and
/// Futures funding. Spot Live comes from the market-data server (EXT-BIN-02), and Demo from the Demo
/// servers (EXT-BIN-08). Every call is normal priority, so it stops at the weight headroom. Market Data
/// shares what it fetches between agents (FR-CYC-09). See Exchange adapter in docs/architecture.md.
/// </summary>
public interface IExchangeMarketData
{
    /// <summary>The most candles one call returns.</summary>
    const int MaximumCandles = 1000;

    /// <summary>The funding interval of a Futures symbol Binance hasn't adjusted.</summary>
    const int DefaultFundingIntervalHours = 8;

    /// <summary>
    /// The market's symbols and their filters (EXT-BIN-06). Held in memory and refreshed once it's older
    /// than Binance:ExchangeInfoRefreshHours. When a refresh fails, the last copy is returned.
    /// </summary>
    /// <exception cref="ExchangeException">There's no copy yet, and Binance couldn't provide one.</exception>
    Task<ExchangeInfo> GetExchangeInfoAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the copy stale, so the next <see cref="GetExchangeInfoAsync"/> fetches a new one, as FR-RISK-04
    /// requires after Binance rejects an order on a filter. A copy younger than a minute is kept, so a run of
    /// rejections can't spend the weight a fetch costs again and again.
    /// </summary>
    /// <returns>False when the copy was kept because it's that new, or there's none yet.</returns>
    bool ExpireExchangeInfo(Market market, TradingEnvironment environment);

    /// <summary>
    /// Candles whose open times fall between <paramref name="from"/> and <paramref name="to"/>, both
    /// included, oldest first, and at most <paramref name="limit"/> of them. Without <paramref name="from"/>,
    /// the latest <paramref name="limit"/> up to <paramref name="to"/>. The last one may still be open: a
    /// candle is final once its <see cref="ExchangeCandle.CloseTime"/> has passed.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, a limit outside 1 to <see cref="MaximumCandles"/>, or <paramref name="from"/> after <paramref name="to"/>.</exception>
    /// <exception cref="ExchangeException">The call failed. An unknown symbol is <see cref="ExchangeFailure.Rejected"/> with -1121.</exception>
    Task<IReadOnlyList<ExchangeCandle>> GetCandlesAsync(
        Market market,
        TradingEnvironment environment,
        string symbol,
        CandleInterval interval,
        DateTimeOffset? from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The last price and the top of the book. Spot reads both from one 24-hour ticker. Futures reads the
    /// book ticker and the price ticker.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept.</exception>
    /// <exception cref="ExchangeException">The call failed. An unknown symbol is <see cref="ExchangeFailure.Rejected"/> with -1121.</exception>
    Task<ExchangeQuote> GetQuoteAsync(Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken);

    /// <summary>A Futures symbol's mark price, index price and funding.</summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept.</exception>
    /// <exception cref="ExchangeException">The call failed. An unknown symbol is <see cref="ExchangeFailure.Rejected"/> with -1121.</exception>
    Task<ExchangePremiumIndex> GetPremiumIndexAsync(TradingEnvironment environment, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// The Futures symbols whose funding interval Binance adjusted, with the interval in hours, from
    /// <c>GET /fapi/v1/fundingInfo</c>. Binance lists only those, so any other symbol's is
    /// <see cref="DefaultFundingIntervalHours"/>. It's one list for every symbol, so ask once and share it.
    /// </summary>
    /// <exception cref="ExchangeException">The call failed.</exception>
    Task<IReadOnlyDictionary<string, int>> GetFundingIntervalsAsync(TradingEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// A Futures symbol's funding rates whose funding times fall between <paramref name="from"/> and
    /// <paramref name="to"/>, both included, oldest first, from <c>GET /fapi/v1/fundingRate</c>, at most 1000. The
    /// trade journal records the rate beside each payment.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, or <paramref name="from"/> after <paramref name="to"/>.</exception>
    /// <exception cref="ExchangeException">The call failed.</exception>
    Task<IReadOnlyList<ExchangeFundingRate>> GetFundingRatesAsync(
        TradingEnvironment environment,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <summary>A Futures symbol's funding rate at one funding time.</summary>
/// <param name="Rate">As a fraction, such as 0.0001. Positive means longs pay shorts.</param>
public sealed record ExchangeFundingRate(string Symbol, DateTimeOffset Time, decimal Rate);

/// <summary>
/// A candle length Binance serves, and Ecnanib uses: each agent timeframe, 1W for a 1D agent's higher timeframe, and
/// 1m for the trade journal's fee conversions and excursions.
/// </summary>
public enum CandleInterval
{
    FifteenMinutes,
    OneHour,
    FourHours,
    OneDay,

    /// <summary>Binance's weeks start on Monday at 00:00 UTC.</summary>
    OneWeek,

    OneMinute,
}

/// <summary>One candle, as Binance sends it. Prices are in the quote asset, and <paramref name="Volume"/> in the base asset.</summary>
/// <param name="CloseTime">The last millisecond the candle covers.</param>
/// <param name="QuoteVolume">The volume in the quote asset.</param>
public sealed record ExchangeCandle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    DateTimeOffset CloseTime,
    decimal QuoteVolume,
    long Trades);

/// <summary>A symbol's last price and the top of its book. Prices are in the quote asset, and quantities in the base asset.</summary>
/// <param name="FetchedAt">When the answer arrived, by this server's clock.</param>
public sealed record ExchangeQuote(
    string Symbol,
    decimal LastPrice,
    decimal BidPrice,
    decimal BidQuantity,
    decimal AskPrice,
    decimal AskQuantity,
    DateTimeOffset FetchedAt);

/// <summary>A Futures symbol's mark price, index price and funding.</summary>
/// <param name="FundingRate">The last funding rate, as a fraction, such as 0.0001. Null for a contract without funding.</param>
/// <param name="NextFundingTime">Null for a contract without funding.</param>
/// <param name="FetchedAt">When the answer arrived, by this server's clock.</param>
public sealed record ExchangePremiumIndex(
    string Symbol,
    decimal MarkPrice,
    decimal IndexPrice,
    decimal? FundingRate,
    DateTimeOffset? NextFundingTime,
    DateTimeOffset FetchedAt);

/// <param name="RefreshedAt">When it was fetched, by this server's clock.</param>
/// <param name="Symbols">By symbol, such as BTCUSDT.</param>
public sealed record ExchangeInfo(
    Market Market,
    TradingEnvironment Environment,
    DateTimeOffset RefreshedAt,
    IReadOnlyDictionary<string, SymbolRules> Symbols)
{
    public SymbolRules? Find(string symbol) => Symbols.GetValueOrDefault(symbol);
}

/// <summary>
/// What Binance allows for a symbol. A filter Binance didn't send is null. Rounding to these is the
/// Risk Engine's job (NFR-MNT-04).
/// </summary>
/// <param name="Status">As Binance sends it, such as TRADING or BREAK.</param>
/// <param name="ContractType">Futures only, such as PERPETUAL.</param>
/// <param name="OrderTypes">As Binance names them, such as LIMIT and STOP_LOSS.</param>
/// <param name="Price">PRICE_FILTER.</param>
/// <param name="Quantity">LOT_SIZE.</param>
/// <param name="MarketQuantity">MARKET_LOT_SIZE. A step of zero means LOT_SIZE's step applies.</param>
/// <param name="Notional">Spot's NOTIONAL or Futures' MIN_NOTIONAL.</param>
/// <param name="PercentPrice">Spot's PERCENT_PRICE_BY_SIDE or PERCENT_PRICE, or Futures' PERCENT_PRICE.</param>
public sealed record SymbolRules(
    string Symbol,
    string Status,
    string BaseAsset,
    string QuoteAsset,
    string? ContractType,
    IReadOnlyList<string> OrderTypes,
    PriceRules? Price,
    QuantityRules? Quantity,
    QuantityRules? MarketQuantity,
    NotionalRules? Notional,
    PercentPriceRules? PercentPrice = null)
{
    public bool IsTrading => Status == "TRADING";
}

public sealed record PriceRules(decimal Minimum, decimal Maximum, decimal TickSize);

public sealed record QuantityRules(decimal Minimum, decimal Maximum, decimal StepSize);

/// <param name="Maximum">Null when Binance sets none.</param>
public sealed record NotionalRules(decimal Minimum, decimal? Maximum);

/// <summary>
/// How far an order's limit price may be from the reference price, as multipliers of it: the mark price on
/// Futures, and the average price over <paramref name="AveragePriceMinutes"/> on Spot. A 0 is no bound. Futures
/// bounds a buy from above and a sell from below only, so its other two are 0.
/// </summary>
/// <param name="AveragePriceMinutes">Spot only.</param>
public sealed record PercentPriceRules(decimal BuyUp, decimal BuyDown, decimal SellUp, decimal SellDown, int? AveragePriceMinutes);
