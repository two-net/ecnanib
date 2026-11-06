using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>An agent's results, from <c>trades</c> and <c>orders</c>. See The trade journal in docs/architecture.md.</summary>
internal sealed class TradeJournal(IDbContextFactory<EcnanibDbContext> contextFactory) : ITradeJournal
{
    /// <summary>Entries that Binance may have taken: every status but those that show it never did.</summary>
    private static readonly OrderStatus[] NeverPlaced = [OrderStatus.NotPlaced, OrderStatus.Rejected];

    public async Task<TradeResults> ReadResultsAsync(
        Guid userId, Guid agentId, DateTimeOffset now, DateTimeOffset activeSince, CancellationToken cancellationToken)
    {
        var dayStart = RiskPeriods.DayStart(now);
        var weekStart = RiskPeriods.WeekStart(now);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var closed = context.Set<TradeRow>()
            .AsNoTracking()
            .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Status == TradeStatus.Closed);

        var week = await closed
            .Where(trade => trade.ClosedAt >= weekStart)
            .Select(trade => new { ClosedAt = trade.ClosedAt!.Value, Pnl = trade.RealizedPnl ?? 0m })
            .ToListAsync(cancellationToken);

        // Enough for any limit: a streak at the highest one pauses the agent.
        var recent = await closed
            .Where(trade => trade.ClosedAt >= activeSince && trade.ExitQuantity > 0m)
            .OrderByDescending(trade => trade.ClosedAt)
            .Take(RiskLimits.MaxCount + 1)
            .Select(trade => trade.RealizedPnl ?? 0m)
            .ToListAsync(cancellationToken);

        var lastLoss = await closed
            .Where(trade => trade.ExitQuantity > 0m && trade.RealizedPnl < 0m)
            .MaxAsync(trade => trade.ClosedAt, cancellationToken);

        var entries = await context.Set<OrderRow>()
            .CountAsync(
                row => row.UserId == userId && row.AgentId == agentId && row.Leg == OrderLeg.Entry
                    && row.CreatedAt >= dayStart && !NeverPlaced.Contains(row.Status),
                cancellationToken);

        return new TradeResults(
            week.Where(trade => trade.ClosedAt >= dayStart).Sum(trade => trade.Pnl),
            week.Sum(trade => trade.Pnl),
            recent.TakeWhile(pnl => pnl < 0m).Count(),
            entries,
            lastLoss);
    }
}
