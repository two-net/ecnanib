using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Execution.Services;

/// <summary>The trade arithmetic shared by fills and reconciliation (FR-EXP-02, FR-EXE-12), the same as the paper simulator's; the JSON shapes are <see cref="TradeJson"/>.</summary>
internal static class TradeMath
{
    /// <summary>The realized P&amp;L of one exit fill against the entry, net of its fee.</summary>
    public static decimal Realized(Trade trade, decimal exitPrice, decimal quantity, decimal fee) =>
        (exitPrice - trade.EntryPrice) * Direction(trade) * quantity - fee;

    public static decimal Direction(Trade trade) => trade.Side == PositionSide.Long ? 1m : -1m;

    public static decimal OpenSize(Trade trade) => trade.RemainingSize > 0 ? trade.RemainingSize : trade.ClosedAt is null ? trade.Size : 0m;

    /// <summary>Reduces the open size and accumulates the exit's realized P&amp;L and fee; the first exit also takes the fees paid so far (the entry's) off the realized P&amp;L, so it is net of every fee.</summary>
    public static void ApplyExit(Trade trade, decimal quantity, decimal fee, decimal realized)
    {
        var open = OpenSize(trade);
        var opening = trade.RealizedPnl ?? -trade.Fees;
        trade.RemainingSize = Math.Max(0m, open - quantity);
        trade.Fees += fee;
        trade.RealizedPnl = opening + realized;
    }

    /// <summary>Closes the trade: the quantity-weighted exit price, the gross R multiple against the initial stop and the reason (FR-EXP-02).</summary>
    public static void Close(Trade trade, decimal lastExitPrice, TradeExitReason reason, DateTimeOffset at)
    {
        trade.RemainingSize = 0m;
        var gross = (trade.RealizedPnl ?? 0m) + trade.Fees - trade.Funding;
        trade.ExitPrice = trade.Size > 0 ? trade.EntryPrice + Direction(trade) * gross / trade.Size : lastExitPrice;
        var initialStop = TradeJson.InitialStop(trade.StopHistory) ?? trade.CurrentStop;
        var risk = initialStop is { } stop ? Math.Abs(trade.EntryPrice - stop) : 0m;
        trade.RMultiple = risk > 0 && trade.Size > 0 ? gross / trade.Size / risk : null;
        trade.ExitReason = reason;
        trade.ClosedAt = at;
    }

    public static TradeExitReason ReasonFor(Order order) => order.Purpose switch
    {
        OrderPurpose.StopLoss => TradeExitReason.Stop,
        OrderPurpose.TakeProfit => TradeExitReason.Target,
        OrderPurpose.Close when order.DecisionId is null => TradeExitReason.KillSwitch,
        _ => TradeExitReason.Council,
    };
}
