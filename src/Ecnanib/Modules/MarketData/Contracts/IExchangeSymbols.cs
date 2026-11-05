using Ecnanib.Kernel;

namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>
/// The symbols Binance lists for a market and environment, with their filters (EXT-BIN-06). It reads
/// the exchange adapter's copy, which refreshes itself daily, and keeps it in <c>exchange_symbols</c>.
/// When Binance can't be reached, it answers from that table, so an outage after a restart doesn't
/// lose them. Agents validate a trading pair with it (FR-AGT-02), and the Risk Engine's caller takes the
/// filters from it.
/// </summary>
public interface IExchangeSymbols
{
    /// <summary>The symbol, or null when Binance doesn't list it.</summary>
    /// <exception cref="MarketDataException">Binance can't be reached, and nothing has been stored yet.</exception>
    Task<ExchangeSymbolInfo?> FindAsync(Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken);

    /// <summary>Every symbol Binance lists, including those that aren't trading, ordered by symbol.</summary>
    /// <exception cref="MarketDataException">Binance can't be reached, and nothing has been stored yet.</exception>
    Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the filters again, and stores them, after Binance rejected an order on one (FR-RISK-04,
    /// <c>ExchangeException.IsFilterRejection</c>). A copy younger than a minute is kept, so a run of rejections
    /// can't spend the weight again and again. A failure keeps the copy there is.
    /// </summary>
    Task RefreshAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken);
}

/// <summary>
/// What Binance allows for a symbol. A filter Binance didn't send is null. Rounding to these is the Risk
/// Engine's job (NFR-MNT-04). Prices are in <paramref name="QuoteAsset"/>, and quantities in
/// <paramref name="BaseAsset"/>.
/// </summary>
/// <param name="Status">As Binance sends it, such as TRADING or BREAK.</param>
/// <param name="ContractType">Futures only, such as PERPETUAL.</param>
/// <param name="OrderTypes">As Binance names them, such as LIMIT and STOP_LOSS.</param>
/// <param name="Price">PRICE_FILTER.</param>
/// <param name="Quantity">LOT_SIZE.</param>
/// <param name="MarketQuantity">MARKET_LOT_SIZE. A step of zero means LOT_SIZE's step applies.</param>
/// <param name="Notional">Spot's NOTIONAL or Futures' MIN_NOTIONAL, in the quote asset.</param>
/// <param name="PercentPrice">How far a limit price may be from the reference price (PERCENT_PRICE).</param>
/// <param name="RefreshedAt">When Binance gave this copy, by this server's clock.</param>
public sealed record ExchangeSymbolInfo(
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    string Status,
    string BaseAsset,
    string QuoteAsset,
    string? ContractType,
    IReadOnlyList<string> OrderTypes,
    PriceFilter? Price,
    QuantityFilter? Quantity,
    QuantityFilter? MarketQuantity,
    NotionalFilter? Notional,
    DateTimeOffset RefreshedAt,
    PercentPriceFilter? PercentPrice = null)
{
    public bool IsTrading => Status == "TRADING";
}

public sealed record PriceFilter(decimal Minimum, decimal Maximum, decimal TickSize);

public sealed record QuantityFilter(decimal Minimum, decimal Maximum, decimal StepSize);

/// <param name="Maximum">Null when Binance sets none.</param>
public sealed record NotionalFilter(decimal Minimum, decimal? Maximum);

/// <summary>
/// Multipliers of the reference price that bound a limit price: the mark price on Futures, and the average price
/// over <paramref name="AveragePriceMinutes"/> on Spot. A 0 is no bound. Futures bounds a buy from above and a
/// sell from below only.
/// </summary>
/// <param name="AveragePriceMinutes">Spot only.</param>
public sealed record PercentPriceFilter(decimal BuyUp, decimal BuyDown, decimal SellUp, decimal SellDown, int? AveragePriceMinutes);
