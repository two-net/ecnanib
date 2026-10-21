using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// Every write of a live order or trade row: the row-first reservation of an order before it is sent (so a crash
/// between the write and the exchange's answer is reconciled by client id, FR-EXE-07), the trade a filled entry
/// opens, the exits that reduce or close it (FR-EXE-06, FR-EXE-12) and the stop history (FR-AI-15). Writes follow the
/// paper simulator's shape: a transaction through the retrying execution strategy, a re-read and a status guard, so a
/// duplicate event or a concurrent writer is a no-op; a stale row is read again once and the change re-applied.
/// </summary>
internal sealed class TradeLedger
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IStrategyCatalog _catalog;
    private readonly IAgentEvents _events;
    private readonly TimeProvider _time;
    private readonly ILogger<TradeLedger> _logger;

    public TradeLedger(IDbContextFactory<EcnanibDbContext> factory, IStrategyCatalog catalog, IAgentEvents events, TimeProvider time, ILogger<TradeLedger> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _events = events;
        _time = time;
        _logger = logger;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    // --- Orders ---------------------------------------------------------------------------------------------------

    /// <summary>Inserts the row before the request is sent; the row already there with the same client id is returned instead (FR-EXE-07).</summary>
    public async Task<(Order Row, bool Existed)> ReserveAsync(Order row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Orders.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (row, false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            var existing = await db.Orders.AsNoTracking().SingleAsync(o => o.ClientOrderId == row.ClientOrderId, cancellationToken).ConfigureAwait(false);
            return (existing, true);
        }
    }

    /// <summary>Applies a change to an order row and returns it; null when the row is gone. A concurrent change is read again and the change re-applied once.</summary>
    public async Task<Order?> UpdateOrderAsync(Guid orderId, Action<Order> apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apply);
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
            if (row is null)
                return null;
            apply(row);
            row.UpdatedAt = Now;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return row;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                _logger.LogDebug("Order {OrderId} changed meanwhile; the update is applied again", orderId);
            }
        }
    }

    public async Task<Order?> OrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Order?> OrderByClientIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.ClientOrderId == clientOrderId && !o.IsPaper, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Order?> OrderByExchangeIdAsync(long exchangeOrderId, string symbol, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.AsNoTracking().Include(o => o.Agent)
            .Where(o => o.ExchangeOrderId == exchangeOrderId && !o.IsPaper && o.Agent.Symbol == symbol)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The open live rows of a trade, oldest first.</summary>
    public async Task<List<Order>> OpenRowsAsync(Guid tradeId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.AsNoTracking()
            .Where(o => o.TradeId == tradeId && !o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountRowsAsync(Guid tradeId, OrderPurpose purpose, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.CountAsync(o => o.TradeId == tradeId && o.Purpose == purpose, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountFilledRowsAsync(Guid tradeId, OrderPurpose purpose, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.CountAsync(o => o.TradeId == tradeId && o.Purpose == purpose && o.Status == OrderStatus.Filled, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Order>> OpenRowsOfAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Orders.AsNoTracking()
            .Where(o => o.AgentId == agentId && !o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // --- Trades ---------------------------------------------------------------------------------------------------

    public async Task<Trade?> TradeAsync(Guid tradeId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tradeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Trade>> OpenTradesOfAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId && !t.IsPaper && t.ClosedAt == null).OrderBy(t => t.OpenedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The targets of the decision that opened an entry and the strategy's partial fraction, for the protection units (Appendix A).</summary>
    public async Task<(IReadOnlyList<decimal> Targets, decimal PartialFraction)> ProtectionPlanAsync(Order entry, Agent agent, CancellationToken cancellationToken)
    {
        IReadOnlyList<decimal> targets = [];
        if (entry.DecisionId is { } decisionId)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var stored = await db.Decisions.AsNoTracking().Where(d => d.Id == decisionId).Select(d => d.Targets).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            targets = TradeJson.Targets(stored);
        }

        var descriptor = _catalog.Find(agent.StrategyId, agent.StrategyVersion) ?? _catalog.Find(agent.StrategyId);
        var fraction = descriptor is null ? 0.5m : ProtectionUnits.PartialFraction(descriptor.Resolve(StrategyParameters.Read(agent.Parameters)));
        return (targets, fraction);
    }

    /// <summary>
    /// An entry fill: the first fill opens the trade for the filled quantity, a later fill of the same entry grows it
    /// (the average entry price weighted). The entry row carries the fill and the fee. Returns the trade as it now is
    /// and whether this call opened it; null when the entry row is gone or already closed out.
    /// </summary>
    public async Task<(Trade Trade, bool Opened)?> ApplyEntryFillAsync(Guid entryOrderId, Agent agent, decimal cumulativeQuantity, decimal averagePrice, decimal fee, bool complete, IReadOnlyList<decimal> targets, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Trade? trade = null;
        var opened = false;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            opened = false;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == entryOrderId, cancellationToken).ConfigureAwait(false);
            if (order is null || order.Purpose != OrderPurpose.Entry || order.Status.IsTerminal() && order.TradeId is not null && order.FilledQuantity >= cumulativeQuantity)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var previous = order.FilledQuantity;
            var added = cumulativeQuantity - previous;
            if (added <= 0 && order.TradeId is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                trade = await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == order.TradeId, cancellationToken).ConfigureAwait(false);
                return;
            }

            order.FilledQuantity = cumulativeQuantity;
            order.AverageFillPrice = averagePrice;
            order.Fee = (order.Fee ?? 0m) + fee;
            order.Status = complete ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            order.FilledAt = complete ? at : order.FilledAt ?? at;
            order.UpdatedAt = at;

            Trade row;
            if (order.TradeId is { } tradeId && await db.Trades.SingleOrDefaultAsync(t => t.Id == tradeId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                row = existing;
                var grown = Math.Max(row.Size, cumulativeQuantity);
                row.RemainingSize = Math.Max(0m, TradeMath.OpenSize(row) + (grown - row.Size));
                row.Size = grown;
                row.EntryPrice = averagePrice;
                row.Fees += fee;
            }
            else
            {
                row = new Trade
                {
                    Id = Guid.CreateVersion7(),
                    AgentId = agent.Id,
                    EntryDecisionId = order.DecisionId ?? throw new InvalidOperationException("A live entry order carries its decision."),
                    Symbol = agent.Symbol,
                    MarketType = agent.MarketType,
                    Timeframe = agent.Timeframe,
                    Side = order.Side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short,
                    EntryPrice = averagePrice,
                    Size = cumulativeQuantity,
                    RemainingSize = cumulativeQuantity,
                    CurrentStop = order.StopPrice,
                    StopHistory = order.StopPrice is { } stop ? TradeJson.StopHistory(at, stop) : JsonValues.EmptyArray,
                    Targets = JsonSerializer.SerializeToElement(targets),
                    Fees = fee,
                    IsPaper = false,
                    OpenedAt = at,
                };
                db.Trades.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                order.TradeId = row.Id;
                opened = true;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = row;
        }).ConfigureAwait(false);

        if (trade is null)
            return null;
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, at, entryOrderId, complete ? OrderStatus.Filled : OrderStatus.PartiallyFilled));
        _events.Publish(new TradeChanged(agent.Id, agent.UserId, at, trade.Id, IsOpen: true));
        return (trade, opened);
    }

    /// <summary>
    /// An exit fill on a stop, take-profit, close or partial-close row: the row is filled with its fee and realized
    /// P&amp;L, the trade reduced, and closed with its outcome when nothing is left (or when the row was a stop or a
    /// close); the other open rows of a closed trade are marked cancelled locally. Returns the trade, or null when the
    /// fill was already recorded.
    /// </summary>
    public async Task<Trade?> ApplyExitFillAsync(Guid exitOrderId, Agent agent, decimal quantity, decimal price, decimal fee, decimal? exchangeRealized, decimal funding, bool complete, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Trade? trade = null;
        var changed = new List<Order>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            changed.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == exitOrderId, cancellationToken).ConfigureAwait(false);
            if (order is null || order.TradeId is not { } tradeId || order.Status == OrderStatus.Filled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == tradeId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.ClosedAt is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var taken = Math.Min(quantity, TradeMath.OpenSize(row));
            if (taken <= 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var realized = exchangeRealized is { } r ? r - fee : TradeMath.Realized(row, price, taken, fee);
            TradeMath.ApplyExit(row, taken, fee, realized);
            var filledBefore = order.FilledQuantity;
            order.Status = complete ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            order.FilledQuantity = filledBefore + taken;
            order.AverageFillPrice = order.AverageFillPrice is { } previous && filledBefore > 0 ? (previous * filledBefore + price * taken) / order.FilledQuantity : price;
            order.FilledAt = complete ? at : order.FilledAt ?? at;
            order.UpdatedAt = at;
            order.Fee = (order.Fee ?? 0m) + fee;
            order.RealizedPnl = (order.RealizedPnl ?? 0m) + realized;
            changed.Add(order);

            // The other open rows of a closed trade stay open here: the caller cancels them on the exchange, which marks them.
            // A stop leg covers one unit on spot (one OCO per unit) and the whole remaining size on futures, so what closes the trade is nothing left or a complete close.
            var closes = row.RemainingSize <= 0 || (complete && order.Purpose == OrderPurpose.Close);
            if (closes)
            {
                row.Funding += funding;
                row.RealizedPnl = (row.RealizedPnl ?? 0m) + funding;
                TradeMath.Close(row, price, TradeMath.ReasonFor(order), at);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = row;
        }).ConfigureAwait(false);

        if (trade is null)
            return null;
        foreach (var order in changed)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, at, order.Id, order.Status));
        _events.Publish(new TradeChanged(agent.Id, agent.UserId, at, trade.Id, IsOpen: trade.ClosedAt is null));
        return trade;
    }

    /// <summary>The exchange closed or shrank a position the system did not (FR-EXE-08): the trade adopts it as truth.</summary>
    public async Task<Trade?> AdoptExternalAsync(Guid tradeId, Agent agent, decimal remaining, decimal? exitPrice, decimal fees, decimal realized, decimal funding, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Trade? trade = null;
        var changed = new List<Order>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            changed.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == tradeId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.ClosedAt is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var taken = Math.Max(0m, TradeMath.OpenSize(row) - remaining);
            TradeMath.ApplyExit(row, taken, fees, realized);
            if (remaining <= 0)
            {
                row.Funding += funding;
                row.RealizedPnl = (row.RealizedPnl ?? 0m) + funding;
                TradeMath.Close(row, exitPrice ?? row.EntryPrice, TradeExitReason.External, at);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = row;
        }).ConfigureAwait(false);

        if (trade is null)
            return null;
        foreach (var order in changed)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, at, order.Id, order.Status));
        _events.Publish(new TradeChanged(agent.Id, agent.UserId, at, trade.Id, IsOpen: trade.ClosedAt is null));
        return trade;
    }

    /// <summary>Records a stop move on the trade (FR-AI-15): the current stop and one more history entry.</summary>
    public async Task<Trade?> SetStopAsync(Guid tradeId, decimal stop, string reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == tradeId && t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
            if (row is null)
                return null;
            row.CurrentStop = stop;
            row.StopHistory = TradeJson.AppendStop(row.StopHistory, at, stop, reason);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return row;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                _logger.LogDebug("Trade {TradeId} changed meanwhile; the stop is applied again", tradeId);
            }
        }
    }

    public async Task<Trade?> SetTargetsAsync(Guid tradeId, IReadOnlyList<decimal> targets, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == tradeId && t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;
        row.Targets = JsonSerializer.SerializeToElement(targets);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public void Publish(AgentEvent agentEvent) => _events.Publish(agentEvent);
}
