namespace Ecnanib.Exchange.Abstractions;

public sealed record SymbolInfo(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    bool IsTrading,
    string Status,
    int PricePrecision,
    int QuantityPrecision,
    bool OcoAllowed,
    SymbolFilters Filters);

/// <summary>
/// Trading filters of a symbol (tick size, lot size, notional, order counts). The rounding helpers implement the
/// arithmetic of FR-EXE-04 ("rounded down to the symbol's filters"); sizing policy itself belongs to the execution
/// module. <see cref="MaxNumAlgoOrders"/> bounds the open stop and take-profit orders per symbol (each OCO stop leg
/// counts on spot); null when the exchange reports no such filter.
/// </summary>
public sealed record SymbolFilters(
    decimal TickSize,
    decimal MinPrice,
    decimal MaxPrice,
    decimal StepSize,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal? MarketStepSize,
    decimal? MarketMinQuantity,
    decimal? MarketMaxQuantity,
    decimal MinNotional,
    decimal? MaxNotional,
    bool MinNotionalAppliesToMarketOrders,
    int? MaxNumOrders = null,
    int? MaxNumAlgoOrders = null)
{
    /// <summary>Rounds a price to the nearest tick (midpoint away from zero).</summary>
    public decimal RoundPriceToTick(decimal price) => RoundToIncrement(price, TickSize, MidpointRounding.AwayFromZero);

    /// <summary>Rounds a price down to the tick grid.</summary>
    public decimal RoundPriceDownToTick(decimal price) => FloorToIncrement(price, TickSize);

    /// <summary>Rounds a price up to the tick grid.</summary>
    public decimal RoundPriceUpToTick(decimal price) => CeilToIncrement(price, TickSize);

    /// <summary>
    /// Rounds a quantity down to the lot-size step grid (never up: FR-EXE-04). LOT_SIZE applies to every order;
    /// for market orders MARKET_LOT_SIZE applies in addition.
    /// </summary>
    public decimal RoundQuantityDownToStep(decimal quantity, bool marketOrder = false)
    {
        var rounded = FloorToIncrement(quantity, StepSize);
        if (marketOrder && MarketStepSize is > 0)
            rounded = FloorToIncrement(rounded, MarketStepSize.Value);
        return rounded;
    }

    public bool IsValidPrice(decimal price)
    {
        if (price <= 0)
            return false;
        if (MinPrice > 0 && price < MinPrice)
            return false;
        if (MaxPrice > 0 && price > MaxPrice)
            return false;
        return TickSize <= 0 || IsOnGrid(price, TickSize);
    }

    public bool IsValidQuantity(decimal quantity, bool marketOrder = false)
    {
        if (quantity <= 0)
            return false;
        if (!Within(quantity, MinQuantity, MaxQuantity, StepSize))
            return false;
        return !marketOrder || Within(quantity, MarketMinQuantity ?? 0, MarketMaxQuantity ?? 0, MarketStepSize ?? 0);
    }

    public bool MeetsMinNotional(decimal price, decimal quantity, bool marketOrder = false)
    {
        if (marketOrder && !MinNotionalAppliesToMarketOrders)
            return true;
        var notional = price * quantity;
        if (MinNotional > 0 && notional < MinNotional)
            return false;
        return MaxNotional is not > 0 || notional <= MaxNotional.Value;
    }

    private static bool Within(decimal value, decimal min, decimal max, decimal step)
    {
        if (min > 0 && value < min)
            return false;
        if (max > 0 && value > max)
            return false;
        return step <= 0 || IsOnGrid(value, step);
    }

    private static bool IsOnGrid(decimal value, decimal increment) => decimal.Remainder(value, increment) == 0;

    private static decimal FloorToIncrement(decimal value, decimal increment) =>
        increment <= 0 ? value : Normalize(decimal.Floor(value / increment) * increment);

    private static decimal CeilToIncrement(decimal value, decimal increment) =>
        increment <= 0 ? value : Normalize(decimal.Ceiling(value / increment) * increment);

    private static decimal RoundToIncrement(decimal value, decimal increment, MidpointRounding mode) =>
        increment <= 0 ? value : Normalize(decimal.Round(value / increment, 0, mode) * increment);

    /// <summary>Strips trailing zeros so 0.1230000 and 0.123 compare and print identically.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
