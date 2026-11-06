using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Quantities on Binance's grid for a stop: <c>Minimum + n × StepSize</c>, by the stricter of LOT_SIZE and
/// MARKET_LOT_SIZE, because a stop becomes a market order when it triggers (FR-RISK-04).
/// </summary>
internal static class Lots
{
    public static Lot Of(ExchangeSymbolInfo symbol)
    {
        var lot = symbol.Quantity;
        var market = symbol.MarketQuantity is { StepSize: > 0m } found ? found : null;

        return new Lot(
            Math.Max(lot?.Minimum ?? 0m, market?.Minimum ?? 0m),
            Math.Min(lot is { Maximum: > 0m } ? lot.Maximum : decimal.MaxValue, market is { Maximum: > 0m } ? market.Maximum : decimal.MaxValue),
            Math.Max(lot?.StepSize ?? 0m, market?.StepSize ?? 0m),
            symbol.Notional?.Minimum ?? 0m);
    }

    /// <summary>Rounded down onto the grid, and no more than its maximum. Zero when it's below the minimum.</summary>
    public static decimal Down(decimal quantity, Lot lot)
    {
        if (quantity <= 0m || quantity < lot.Minimum)
        {
            return 0m;
        }

        var down = lot.StepSize > 0m ? lot.Minimum + (Math.Floor((quantity - lot.Minimum) / lot.StepSize) * lot.StepSize) : quantity;

        return Math.Min(down, lot.Maximum);
    }

    /// <summary>
    /// Whether Binance takes a stop for this quantity. Futures reduce-only orders are exempt from the minimum notional,
    /// and on Spot the stop's own price measures it, which is roughly what it sells at.
    /// </summary>
    public static bool Placeable(decimal quantity, decimal stopPrice, AgentSession session)
    {
        var lot = Of(session.Symbol);

        return quantity > 0m && quantity >= lot.Minimum
            && (session.Futures || lot.MinNotional <= 0m || quantity * stopPrice >= lot.MinNotional);
    }
}

internal sealed record Lot(decimal Minimum, decimal Maximum, decimal StepSize, decimal MinNotional);
