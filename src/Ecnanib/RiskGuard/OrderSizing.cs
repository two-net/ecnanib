using Ecnanib.Shared;

namespace Ecnanib.RiskGuard;

/// <summary>
/// Computes the size of the order that a plan opens: its quantity, and on Futures its leverage
/// (FR-RSK-5). Models choose neither, and nothing hands a check a size: <see cref="GuardCheck"/>
/// asks here, once for a check, with the facts as its rules read them. See "The size of an
/// order" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The quantity is the risk that was approved, divided by the distance to the stop-loss:</b>
/// the agent's equity times the risk fraction of the plan is what is lost when the stop-loss is
/// hit. The fraction is held to the agent's risk per trade here as well, whatever was validated
/// before.
/// </para>
/// <para>
/// <b>On Spot, the position is worth at most the agent's equity. On Futures, the leverage is the
/// lowest that fits the margin of the position within the agent's equity, up to the leverage
/// cap.</b> A quantity that would be worth more is reduced, and so is one above the most that the
/// exchange takes in an order.
/// </para>
/// <para>
/// <b>A quantity is rounded down, and never up:</b> to the quantity step of the symbol, so that
/// rounding adds no risk and breaks no cap. It is not raised to a minimum of the exchange: an
/// order that is too small is for the rules to reject.
/// </para>
/// </remarks>
internal static class OrderSizing
{
    // What a quantity is rounded down to where the symbol has no quantity step: 8 decimal places.
    private const decimal SmallestStep = 0.00000001m;

    /// <summary>
    /// The size of a plan whose facts are there and give no order: no quantity. The rules of a
    /// quantity do not hold for it, so the plan is rejected.
    /// </summary>
    public static readonly OrderSize Nothing = new(Quantity: 0m, Leverage: 1);

    /// <summary>The size of the order that a plan opens.</summary>
    /// <param name="case">The plan and the facts.</param>
    /// <param name="side">The side that the plan opens, or null when it is not known: a reverse of a position that is gone.</param>
    /// <param name="entry">The price that the plan enters at, or null when it is not known.</param>
    /// <param name="stop">The stop-loss of the plan, or null when it names none that is a price.</param>
    /// <param name="symbol">The rules of the symbol, or null when they are not known.</param>
    /// <returns>
    /// Null for a plan that opens nothing, and when a fact that the size follows from is not
    /// there. <see cref="Nothing"/> when the facts are there and give no order: a plan with no
    /// stop-loss on the losing side of its entry, an agent with no equity, or a quantity that
    /// comes to less than one step.
    /// </returns>
    public static OrderSize? Of(GuardCase @case, TradeSide? side, decimal? entry, decimal? stop, SymbolRules? symbol)
    {
        if (!@case.OpensPosition)
        {
            return null;
        }

        // The plan itself gives no order: it has no stop-loss to measure its risk by, or it
        // names a limit price that is no price.
        if (stop is not { } stopLoss || @case.EntryPrice is <= 0m)
        {
            return Nothing;
        }

        if (@case.RiskFraction is not { } fraction || side is not { } opened || entry is not { } price || symbol is null)
        {
            return null;
        }

        try
        {
            return Sized(@case, opened, price, stopLoss, fraction, symbol);
        }
        catch (OverflowException)
        {
            // Numbers that cannot be worked out with are no numbers of a market: no order.
            return Nothing;
        }
    }

    private static OrderSize Sized(GuardCase @case, TradeSide side, decimal entry, decimal stop, decimal fraction, SymbolRules symbol)
    {
        var limits = @case.Limits;
        var equity = @case.Equity;
        var onFutures = @case.Market == Market.Futures;

        // Counted in the direction of the trade: a stop-loss that is not on the losing side of
        // the entry is no risk to size an order by.
        var distance = side == TradeSide.Long ? entry - stop : stop - entry;

        if (distance <= 0m || equity <= 0m || fraction <= 0m || limits.RiskPerTradePercent <= 0m || (onFutures && limits.LeverageCap < 1))
        {
            return Nothing;
        }

        // What is lost when the stop-loss is hit: the share of the equity that was approved,
        // and at most the agent's risk per trade.
        var risked = equity * Math.Min(fraction, limits.RiskPerTradePercent / 100m);

        // The most that the position is worth: the equity on Spot, and on Futures what the
        // equity is the margin of at the leverage cap.
        var mostValue = onFutures ? equity * limits.LeverageCap : equity * FixedLimits.SpotPositionPercentOfEquity / 100m;

        var quantity = Math.Min(risked / distance, mostValue / entry);

        if (symbol.MaxQuantity > 0m)
        {
            quantity = Math.Min(quantity, symbol.MaxQuantity);
        }

        // Down to the step, so that rounding adds no risk and breaks no cap.
        var step = symbol.QuantityStep > 0m ? symbol.QuantityStep : SmallestStep;

        quantity = Math.Floor(quantity / step) * step;

        // A division rounds in its last digit, which may have carried the quantity onto the
        // step above. Products decide: one step down keeps the risk and the cap.
        if (quantity * distance > risked || quantity * entry > mostValue)
        {
            quantity -= step;
        }

        if (quantity <= 0m || quantity * distance > risked || quantity * entry > mostValue)
        {
            return Nothing;
        }

        return new OrderSize(quantity, onFutures ? LowestLeverage(quantity * entry, equity, limits.LeverageCap) : 1);
    }

    /// <summary>
    /// The lowest whole leverage at which the margin of a position is within the equity: its
    /// value divided by the leverage. The value is within the equity times the cap.
    /// </summary>
    private static int LowestLeverage(decimal value, decimal equity, int cap)
    {
        var leverage = (int)Math.Clamp(Math.Ceiling(value / equity), 1m, cap);

        // The division rounds as well, so products decide here too: one lower when that fits,
        // and one higher when this does not.
        if (leverage > 1 && value <= equity * (leverage - 1))
        {
            leverage--;
        }
        else if (leverage < cap && value > equity * leverage)
        {
            leverage++;
        }

        return leverage;
    }
}
