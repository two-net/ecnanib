using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Public exchange data, with no keys. Market Data (#22) adds candles, prices and the order book here.
/// See Exchange adapter in docs/architecture.md.
/// </summary>
public interface IExchangeMarketData
{
    /// <summary>
    /// The market's symbols and their filters (EXT-BIN-06). Held in memory and refreshed once it's older
    /// than Binance:ExchangeInfoRefreshHours. When a refresh fails, the last copy is returned.
    /// </summary>
    /// <exception cref="ExchangeException">There's no copy yet, and Binance couldn't provide one.</exception>
    Task<ExchangeInfo> GetExchangeInfoAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken);
}

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
    NotionalRules? Notional)
{
    public bool IsTrading => Status == "TRADING";
}

public sealed record PriceRules(decimal Minimum, decimal Maximum, decimal TickSize);

public sealed record QuantityRules(decimal Minimum, decimal Maximum, decimal StepSize);

/// <param name="Maximum">Null when Binance sets none.</param>
public sealed record NotionalRules(decimal Minimum, decimal? Maximum);
