using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Execution.Services;

/// <summary>
/// The trade lifecycle (FR-UI-03 e) from what is stored: the entry decision's evaluation and decision, the entry order,
/// the initial protection (the first stop and the entry decision's targets), every later stop from <c>stop_history</c>,
/// the managing decisions, the exit fills and the close. Replaced protective orders are not listed one by one: the stop
/// history and the managing decisions already say what changed and why.
/// </summary>
internal sealed class TradeTimelineReader : ITradeTimelineReader
{
    private static readonly OrderStatus[] Ended = [OrderStatus.Canceled, OrderStatus.Expired, OrderStatus.Rejected];

    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public TradeTimelineReader(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<TradeTimelineView?> GetAsync(Guid userId, Guid tradeId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var trade = await db.Trades.AsNoTracking()
            .Include(t => t.EntryDecision).ThenInclude(d => d.Evaluation)
            .SingleOrDefaultAsync(t => t.Id == tradeId && t.Agent.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (trade is null)
            return null;
        return (await BuildAsync(db, [trade], cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<IReadOnlyList<TradeTimelineView>> ListRecentAsync(Guid userId, Guid agentId, int count, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Agents.AnyAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false))
            return [];
        var trades = await db.Trades.AsNoTracking()
            .Include(t => t.EntryDecision).ThenInclude(d => d.Evaluation)
            .Where(t => t.AgentId == agentId)
            .OrderBy(t => t.ClosedAt != null).ThenByDescending(t => t.OpenedAt)
            .Take(Math.Clamp(count, 1, 50))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync(db, trades, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TradeTimelineView>> BuildAsync(EcnanibDbContext db, IReadOnlyList<Trade> trades, CancellationToken cancellationToken)
    {
        if (trades.Count == 0)
            return [];
        var tradeIds = trades.Select(t => t.Id).ToList();
        var entryDecisionIds = trades.Select(t => t.EntryDecisionId).ToList();
        var orders = await db.Orders.AsNoTracking()
            .Where(o => (o.TradeId != null && tradeIds.Contains(o.TradeId.Value)) || (o.DecisionId != null && entryDecisionIds.Contains(o.DecisionId.Value) && o.Purpose == OrderPurpose.Entry))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var managing = await db.Decisions.AsNoTracking()
            .Where(d => d.TradeId != null && tradeIds.Contains(d.TradeId.Value))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return trades.Select(trade => Build(trade,
            orders.Where(o => o.TradeId == trade.Id || (o.DecisionId == trade.EntryDecisionId && o.Purpose == OrderPurpose.Entry)).ToList(),
            managing.Where(d => d.TradeId == trade.Id).ToList())).ToList();
    }

    internal static TradeTimelineView Build(Trade trade, IReadOnlyList<Order> orders, IReadOnlyList<Decision> managing)
    {
        var events = new List<TimelineEvent>();
        var decision = trade.EntryDecision;
        if (decision is not null)
        {
            if (decision.Evaluation is { } evaluation)
                events.Add(new TimelineEvent(evaluation.StartedAt, TimelineKind.Evaluated, EvaluationId: evaluation.Id));
            events.Add(new TimelineEvent(decision.CreatedAt, TimelineKind.Decided, decision.EntryPrice, Action: decision.FinalAction, EvaluationId: decision.EvaluationId));
        }

        foreach (var entry in orders.Where(o => o.Purpose == OrderPurpose.Entry))
        {
            events.Add(new TimelineEvent(entry.CreatedAt, TimelineKind.EntryPlaced, entry.Price, entry.Quantity, Status: entry.Status, Purpose: entry.Purpose, OrderId: entry.Id));
            if (entry.FilledAt is { } filledAt)
                events.Add(new TimelineEvent(filledAt, TimelineKind.EntryFilled, entry.AverageFillPrice ?? entry.Price, entry.FilledQuantity > 0 ? entry.FilledQuantity : entry.Quantity, Status: entry.Status, Purpose: entry.Purpose, OrderId: entry.Id));
            else if (Ended.Contains(entry.Status))
                events.Add(new TimelineEvent(entry.UpdatedAt, TimelineKind.EntryEnded, entry.Price, entry.Quantity, Status: entry.Status, Purpose: entry.Purpose, OrderId: entry.Id));
        }

        var stops = TradeJson.StopHistoryEntries(trade.StopHistory);
        var targets = TradeJson.Targets(decision?.Targets);
        if (stops.Count > 0)
            events.Add(new TimelineEvent(stops[0].At, TimelineKind.Protected, stops[0].Price, Reason: stops[0].Reason, Targets: targets));
        else if (trade.CurrentStop is { } stop)
            events.Add(new TimelineEvent(trade.OpenedAt, TimelineKind.Protected, stop, Targets: targets));
        foreach (var move in stops.Skip(1))
            events.Add(new TimelineEvent(move.At, TimelineKind.StopMoved, move.Price, Reason: move.Reason));

        foreach (var managed in managing)
        {
            events.Add(new TimelineEvent(managed.CreatedAt, TimelineKind.Managed, managed.StopPrice, Action: managed.FinalAction, EvaluationId: managed.EvaluationId, Reason: managed.Rationale));
            var changed = TradeJson.Targets(managed.Targets);
            if (managed.FinalAction == DecisionAction.Modify && changed.Count > 0)
                events.Add(new TimelineEvent(managed.CreatedAt, TimelineKind.TargetsChanged, Targets: changed, EvaluationId: managed.EvaluationId));
        }

        var exits = orders.Where(o => o.TradeId == trade.Id && o.Purpose != OrderPurpose.Entry && o.FilledAt != null).OrderBy(o => o.FilledAt).ToList();
        for (var i = 0; i < exits.Count; i++)
        {
            var exit = exits[i];
            var last = trade.ClosedAt is not null && i == exits.Count - 1;
            events.Add(new TimelineEvent(exit.FilledAt!.Value, last ? TimelineKind.Exit : TimelineKind.PartialExit, exit.AverageFillPrice ?? exit.Price ?? exit.StopPrice,
                exit.FilledQuantity > 0 ? exit.FilledQuantity : exit.Quantity, Purpose: exit.Purpose, Status: exit.Status, RealizedPnl: exit.RealizedPnl, OrderId: exit.Id));
        }

        if (trade.ClosedAt is { } closedAt)
            events.Add(new TimelineEvent(closedAt, TimelineKind.Closed, trade.ExitPrice, trade.Size, ExitReason: trade.ExitReason, RealizedPnl: trade.RealizedPnl));

        return new TradeTimelineView(trade.Id, trade.AgentId, trade.Symbol, trade.Side, trade.ClosedAt is null, trade.IsPaper, trade.EntryPrice, trade.Size, trade.CurrentStop,
            trade.RealizedPnl, trade.OpenedAt, trade.ClosedAt, trade.ExitReason, events.OrderBy(e => e.At).ThenBy(e => e.Kind).ToList());
    }
}
