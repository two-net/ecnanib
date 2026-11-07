using Ecnanib.Kernel;
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
        Guid userId, Guid agentId, TradingEnvironment environment, DateTimeOffset now, DateTimeOffset activeSince, CancellationToken cancellationToken)
    {
        var dayStart = RiskPeriods.DayStart(now);
        var weekStart = RiskPeriods.WeekStart(now);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var closed = context.Set<TradeRow>()
            .AsNoTracking()
            .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Environment == environment && trade.Status == TradeStatus.Closed);

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
                row => row.UserId == userId && row.AgentId == agentId && row.Environment == environment && row.Leg == OrderLeg.Entry
                    && row.CreatedAt >= dayStart && !NeverPlaced.Contains(row.Status),
                cancellationToken);

        return new TradeResults(
            week.Where(trade => trade.ClosedAt >= dayStart).Sum(trade => trade.Pnl),
            week.Sum(trade => trade.Pnl),
            recent.TakeWhile(pnl => pnl < 0m).Count(),
            entries,
            lastLoss);
    }

    public async Task<IReadOnlyList<JournalTotals>> ReadTotalsAsync(
        Guid userId, Guid agentId, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var trades = context.Set<TradeRow>()
            .AsNoTracking()
            .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Environment == environment);

        var rows = await trades
            .Select(trade => new
            {
                trade.QuoteAsset,
                trade.Status,
                trade.SettledAt,
                trade.Fees,
                trade.Funding,
                Pnl = trade.RealizedPnl ?? 0m,

                // Read while it was open, or before it was settled. Settling replaces them, so they're only added before.
                FeesSoFar = context.Set<FillRow>().Where(fill => fill.TradeId == trade.Id).Sum(fill => fill.FeeQuote ?? 0m),
                FundingSoFar = context.Set<FundingPaymentRow>().Where(payment => payment.TradeId == trade.Id).Sum(payment => payment.Amount),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .GroupBy(row => row.QuoteAsset)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var closed = group.Where(row => row.Status == TradeStatus.Closed).ToList();
                    var open = group.Where(row => row.Status == TradeStatus.Open).ToList();

                    // A settled trade's P&L is net. One not settled yet is gross from its orders, less what was read so far.
                    var realized = closed.Sum(row => row.SettledAt is null ? row.Pnl - row.FeesSoFar + row.FundingSoFar : row.Pnl);

                    return new JournalTotals(
                        group.Key,
                        realized,
                        closed.Count,
                        closed.Count(row => row.SettledAt is null),
                        closed.Count(row => row.SettledAt is not null && (row.Fees is null || row.Funding is null)),
                        open.Count,
                        open.Sum(row => row.FeesSoFar),
                        open.Sum(row => row.FundingSoFar));
                }),
        ];
    }

    public async Task<IReadOnlyList<TradeRecord>> ListTradesAsync(Guid userId, Guid agentId, TradeQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var trades = context.Set<TradeRow>()
            .AsNoTracking()
            .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Environment == query.Environment);

        if (query.Symbol is { } symbol)
        {
            trades = trades.Where(trade => trade.Symbol == symbol);
        }

        if (query.From is { } from)
        {
            trades = trades.Where(trade => trade.ClosedAt == null || trade.ClosedAt >= from);
        }

        if (query.To is { } to)
        {
            trades = trades.Where(trade => trade.OpenedAt <= to);
        }

        var rows = await trades
            .OrderByDescending(trade => trade.OpenedAt)
            .ThenByDescending(trade => trade.Id)
            .Take(Math.Clamp(query.Limit, 1, TradeQuery.MaxLimit))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToRecord)];
    }

    private static TradeRecord ToRecord(TradeRow trade) => new(
        trade.Id,
        trade.Market,
        trade.Environment,
        trade.Symbol,
        trade.BaseAsset,
        trade.QuoteAsset,
        trade.Side,
        trade.Status,
        trade.OpenedAt,
        trade.ClosedAt,
        trade.Quantity,
        trade.EntryPrice,
        trade.InitialStop,
        trade.ExitQuantity,
        trade.ExitPrice,
        trade.UnpricedQuantity,
        trade.Fees,
        trade.Funding,
        trade.RealizedPnl,
        trade.RMultiple,
        trade.ExitReason,
        trade.SettledAt is not null);
}
