using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Keeps the orders, the executions, and the positions of agents, with the funding of the
/// positions: every read of them, and every write. It calls no exchange, and knows nothing of
/// one: what places the orders, and what watches the positions, tell it what the exchange said.
/// See "The record of an order" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An order is begun before it is sent.</b> Beginning one writes its row, inside a
/// transaction that first takes the lock of the agent's account, as the controls of the agent's
/// user do. Under that lock it is asked what the agent holds, so two that begin an order of the
/// same agent begin one after the other, and the second finds what the first wrote. An index
/// holds an agent to one order of each purpose that is not over, and to one open position,
/// whatever asks. The pair of a stop-loss and a take-profit of the Spot market is begun as two
/// rows, both or neither. On the Futures market the two are orders of their own: the stop-loss
/// is begun first, and the take-profit beside it once the stop-loss rests (FR-BIN-F3).
/// </para>
/// <para>
/// <b>What becomes of an order is written by comparing and setting:</b> a write names the
/// status that the row is expected to have, and writes nothing when the row has moved on. So of
/// two that learn the same thing of an order, one writes it, and what follows from it, a
/// position that is opened or closed, is written once, in the same transaction.
/// </para>
/// <para>
/// No transaction here is held while the exchange is asked.
/// </para>
/// </remarks>
internal sealed class StoredOrders(IDbContextFactory<EcnanibDbContext> contexts, IAgentTrading agents, TimeProvider clock)
{
    /// <summary>For how long the executions of an order that is over are still asked for, when they could not be read.</summary>
    public static readonly TimeSpan FillsWithin = TimeSpan.FromDays(1);

    /// <summary>
    /// For how long the funding of a position that is closed is still asked for, when it could
    /// not be read. A closed position that still says when its funding is read next after that
    /// is one whose funding is not all stored.
    /// </summary>
    public static readonly TimeSpan FundingWithin = TimeSpan.FromDays(1);

    /// <summary>
    /// For how long after an order came to be over the fees of its executions are still valued,
    /// when what values one could not be read as the execution was stored. A price is read
    /// again within seconds, so a fee that has no value after this is one that nothing values:
    /// it is in an asset that the configuration file names no symbol for. The result of its
    /// position does not wait longer.
    /// </summary>
    public static readonly TimeSpan FeesValuedWithin = TimeSpan.FromHours(1);

    // The millisecond of the id that was handed out last.
    private static long _lastMillisecond;

    /// <summary>One order, as it is stored now, or null when there is none of that id.</summary>
    public async Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Order>().AsNoTracking().SingleOrDefaultAsync(order => order.Id == orderId, cancellationToken);
    }

    /// <summary>The open position of an agent, or null when it has none.</summary>
    public async Task<Position?> OpenPositionAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await OpenPositionAsync(context, agentId, cancellationToken);
    }

    /// <summary>The orders of an agent that are not over, in the order they were written in.</summary>
    public async Task<IReadOnlyList<Order>> LiveOrdersAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await LiveOrdersAsync(context, agentId, cancellationToken);
    }

    /// <summary>
    /// The orders of an agent that are over, of which something was executed, and whose
    /// executions are not stored yet, as far as they are still asked for.
    /// </summary>
    public async Task<IReadOnlyList<Order>> WithoutFillsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - FillsWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Order>()
            .AsNoTracking()
            .Where(order => order.AgentId == agentId
                && !order.FillsRecorded
                && order.ExecutedQuantity > 0m
                && order.ExchangeOrderId != null
                && !OrderStatuses.Live.Contains(order.Status)
                && order.UpdatedAt >= since)
            .OrderBy(order => order.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the records of an agent hold nothing that is to be settled with the exchange: no
    /// order of which it is not known whether the exchange has it, no order that protected a
    /// position which is closed and is with the exchange still, and no open position without a
    /// stop-loss that rests on the exchange.
    /// </summary>
    public async Task<bool> IsSettledAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking();

        if (await orders.AnyAsync(order => order.AgentId == agentId && OrderStatuses.Unsettled.Contains(order.Status), cancellationToken))
        {
            return false;
        }

        // What is left of the protection of a position that is closed would act on the next one.
        if (await LeftBehind(context).AnyAsync(order => order.AgentId == agentId, cancellationToken))
        {
            return false;
        }

        return !await context.Set<Position>()
            .AsNoTracking()
            .AnyAsync(
                position => position.AgentId == agentId
                    && position.ClosedAt == null
                    && !orders.Any(order => order.PositionId == position.Id
                        && order.Purpose == OrderPurpose.StopLoss
                        && OrderStatuses.Resting.Contains(order.Status)),
                cancellationToken);
    }

    /// <summary>
    /// The agents of all users whose records are to be settled with the exchange, in the order
    /// of their ids: one with an order of unknown outcome, one with an entry that rests, one
    /// with an open position that has no stop-loss resting, one whose resting stop-loss or
    /// take-profit the exchange was last asked about before a moment, one with executions that
    /// are not stored yet, one with an order that protected a position which is closed, and one
    /// with a position of the Futures market whose take-profit does not rest beside its
    /// stop-loss (FR-BIN-F3).
    /// </summary>
    /// <param name="checkedBefore">An order that protects a position and that the exchange was asked about before this is asked about again.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<Guid>> ToSettleAsync(DateTimeOffset checkedBefore, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - FillsWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking();

        var ofOrders = orders
            .Where(order => OrderStatuses.Unsettled.Contains(order.Status)

                // An entry that rests is asked about with every run: when it is executed, what
                // it bought has no protection until that is seen (FR-BIN-S2).
                || (OrderStatuses.Resting.Contains(order.Status)
                    && (order.Purpose == OrderPurpose.Entry || order.CheckedAt == null || order.CheckedAt < checkedBefore))
                || (!order.FillsRecorded
                    && order.ExecutedQuantity > 0m
                    && order.ExchangeOrderId != null
                    && !OrderStatuses.Live.Contains(order.Status)
                    && order.UpdatedAt >= since))
            .Select(order => order.AgentId);

        var ofPositions = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt == null
                && !orders.Any(order => order.PositionId == position.Id
                    && order.Purpose == OrderPurpose.StopLoss
                    && OrderStatuses.Resting.Contains(order.Status)))
            .Select(position => position.AgentId);

        // What one order of a position left behind when the other closed it, which is taken off
        // with every run until it is gone.
        var ofLeftBehind = LeftBehind(context).Select(order => order.AgentId);

        // A take-profit of the Futures market is an order of its own, which is placed once the
        // stop-loss rests: one that is owed is placed with the next run.
        var ofTakeProfits = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt == null
                && position.Market == Market.Futures
                && position.TakeProfit != null
                && orders.Any(order => order.PositionId == position.Id
                    && order.Purpose == OrderPurpose.StopLoss
                    && order.Status == OrderStatus.Open)
                && !orders.Any(order => order.AgentId == position.AgentId
                    && order.Purpose == OrderPurpose.TakeProfit
                    && OrderStatuses.Live.Contains(order.Status)))
            .Select(position => position.AgentId);

        return await ofOrders.Union(ofPositions).Union(ofLeftBehind).Union(ofTakeProfits).OrderBy(id => id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The agents of all users that hold something which is reconciled with the exchange, in the
    /// order of their ids: one with an open position, and one with an order that is not over
    /// (FR-BIN-7). An agent that holds neither has nothing that an account could differ from.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ToReconcileAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await Holding(context).OrderBy(id => id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The accounts in which an agent holds something, of all users, each once: an account is
    /// that of a user on a market in an environment. They are the accounts whose event feed is
    /// followed (FR-BIN-7): what changes in another is nothing that the records could differ from.
    /// </summary>
    public async Task<IReadOnlyList<HeldAccount>> AccountsHoldingAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var ofOrders = context.Set<Order>()
            .AsNoTracking()
            .Where(order => OrderStatuses.Live.Contains(order.Status))
            .Select(order => new { order.UserId, order.Mode, order.Market });

        var ofPositions = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt == null)
            .Select(position => new { position.UserId, position.Mode, position.Market });

        var accounts = await ofOrders.Union(ofPositions).ToListAsync(cancellationToken);

        return
        [
            .. accounts
                .Select(account => new HeldAccount(account.UserId, account.Mode, account.Market))
                .OrderBy(account => account.UserId)
                .ThenBy(account => account.Mode)
                .ThenBy(account => account.Market),
        ];
    }

    /// <summary>
    /// The agents that hold something in an account, in the order of their ids: an open position
    /// or an order that is not over, of a user on a market in an environment, in one symbol, or
    /// in any when none is named.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> HoldingAsync(
        Guid userId, TradingMode mode, Market market, string? symbol, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var ofOrders = context.Set<Order>()
            .AsNoTracking()
            .Where(order => OrderStatuses.Live.Contains(order.Status)
                && order.UserId == userId
                && order.Mode == mode
                && order.Market == market
                && (symbol == null || order.Symbol == symbol))
            .Select(order => order.AgentId);

        var ofPositions = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt == null
                && position.UserId == userId
                && position.Mode == mode
                && position.Market == market
                && (symbol == null || position.Symbol == symbol))
            .Select(position => position.AgentId);

        return await ofOrders.Union(ofPositions).OrderBy(id => id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether an agent holds something that was there before a moment: an open position that
    /// was opened before it, or an order that is not over and was written before it. What an
    /// agent came to hold later is what the application placed since, and saw placed.
    /// </summary>
    public async Task<bool> HeldBeforeAsync(Guid agentId, DateTimeOffset moment, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Position>()
                .AsNoTracking()
                .AnyAsync(position => position.AgentId == agentId && position.ClosedAt == null && position.OpenedAt < moment, cancellationToken)
            || await context.Set<Order>()
                .AsNoTracking()
                .AnyAsync(
                    order => order.AgentId == agentId && OrderStatuses.Live.Contains(order.Status) && order.CreatedAt < moment,
                    cancellationToken);
    }

    /// <summary>
    /// Whether an order of an agent that is over was executed, in full or in part, and came to
    /// be over since a moment. What it did to what the account holds may not be in what the
    /// exchange says of the account yet.
    /// </summary>
    public async Task<bool> ExecutedSinceAsync(Guid agentId, DateTimeOffset moment, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Order>()
            .AsNoTracking()
            .AnyAsync(
                order => order.AgentId == agentId
                    && order.ExecutedQuantity > 0m
                    && !OrderStatuses.Live.Contains(order.Status)
                    && order.UpdatedAt >= moment,
                cancellationToken);
    }

    /// <summary>
    /// The open positions of a user on a market in an environment, of all their agents, in the
    /// order of their ids. On the Spot market they share what the account holds of an asset.
    /// </summary>
    public async Task<IReadOnlyList<Position>> OpenPositionsAsync(
        Guid userId, TradingMode mode, Market market, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.UserId == userId && position.Mode == mode && position.Market == market && position.ClosedAt == null)
            .OrderBy(position => position.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The orders of an agent that protected a position which is closed, and that are not over:
    /// the stop-loss of a position of the Futures market whose take-profit was executed, or the
    /// other way around. The exchange holds the two apart there, and ends neither for the other.
    /// </summary>
    public async Task<IReadOnlyList<Order>> LeftBehindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await LeftBehind(context).Where(order => order.AgentId == agentId).OrderBy(order => order.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether an agent takes new entries now: its state as it is under the lock of its account,
    /// read in a transaction of its own. An entry that rests is taken off the exchange for an
    /// agent that takes none (FR-BIN-S2).
    /// </summary>
    public async Task<bool> TakesEntriesAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        return await agents.StateUnderLockAsync(context, userId, agentId, cancellationToken) is { } state && AgentStates.TakesNewEntries(state);
    }

    /// <summary>
    /// Why no entry would be begun for an agent now, or null when one would: asked before
    /// anything that an entry takes is asked of the exchange, so that an agent that no longer
    /// opens positions, or holds one, is not asked about for nothing. It holds nothing:
    /// <see cref="BeginEntryAsync"/> asks again, as the row is written.
    /// </summary>
    public async Task<EntryRefusal?> EntryRefusalAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        return await EntryRefusalAsync(context, agent, cancellationToken);
    }

    /// <summary>
    /// Begins the entry of an agent: writes its row, before it is sent. It is written only for
    /// an agent that opens positions now, and holds nothing: no open position, and no order that
    /// is not over. Both are asked under the lock of the account, so a stop of the agent's user
    /// and an entry cannot cross.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cycleId">The cycle whose plan it is.</param>
    /// <param name="side">Whether it buys or sells.</param>
    /// <param name="quantity">The quantity that the Risk Guard computed.</param>
    /// <param name="stopLoss">The stop-loss of the position that it opens.</param>
    /// <param name="takeProfit">The take-profit of that position, or null when the plan named none.</param>
    /// <param name="limitPrice">The price of an entry at a price, or null for an entry at the market.</param>
    /// <param name="cancelAfter">When an entry at a price that still rests is taken off the exchange. Null for an entry at the market.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The order, or why none was begun.</returns>
    public async Task<(Order? Order, EntryRefusal? Refusal)> BeginEntryAsync(
        Agent agent,
        Guid cycleId,
        OrderSide side,
        decimal quantity,
        decimal stopLoss,
        decimal? takeProfit,
        decimal? limitPrice,
        DateTimeOffset? cancelAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await EntryRefusalAsync(context, agent, cancellationToken) is { } refusal)
        {
            return (null, refusal);
        }

        var now = clock.GetUtcNow();
        var id = NextId();

        var order = new Order(
            id,
            agent.Id,
            agent.UserId,
            agent.Mode,
            agent.Market,
            agent.Symbol,
            cycleId,
            positionId: null,
            Order.ClientOrderIdOf(id),
            exchangeOrderId: null,
            OrderPurpose.Entry,
            limitPrice is null ? OrderType.Market : OrderType.Limit,
            side,
            limitPrice,
            stopLoss,
            takeProfit,
            quantity,
            OrderStatus.Sending,
            executedQuantity: 0m,
            averagePrice: null,
            exitReason: null,
            sends: 1,
            fillsRecorded: false,
            now,
            now,
            checkedAt: null,
            pairId: null,
            limitPrice is null ? null : cancelAfter);

        context.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (order, null);
    }

    /// <summary>
    /// Begins the stop-loss of an open position, at the price that the position says, or the
    /// order that closes it at the market: writes its row, before it is sent. It is written
    /// only while the position is open and has no such order that is not over, and no order of
    /// the other kind either: a position is not protected while it is being closed, and not
    /// closed over a stop-loss that still rests. A stop-loss is begun beside the take-profit of
    /// the same position that is an order of its own, on the Futures market: a stop-loss that
    /// is gone is placed again whatever has become of the take-profit.
    /// </summary>
    /// <param name="positionId">The position.</param>
    /// <param name="purpose">A stop-loss, or a close.</param>
    /// <param name="reason">Why a close closes the position. Null for a stop-loss.</param>
    /// <param name="cycleId">The cycle that begins it, or null when none does.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The order, or null when none was begun.</returns>
    public async Task<Order?> BeginForPositionAsync(
        Guid positionId, OrderPurpose purpose, ExitReason? reason, Guid? cycleId, CancellationToken cancellationToken)
    {
        if (purpose is not (OrderPurpose.StopLoss or OrderPurpose.Close))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "An order of a position protects it or closes it.");
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var stop = purpose == OrderPurpose.StopLoss;

        if (await UnguardedAsync(context, positionId, besideTakeProfit: stop, cancellationToken) is not { } position)
        {
            return null;
        }

        var order = OrderOf(
            position,
            NextId(),
            cycleId,
            purpose,
            stop ? OrderType.Stop : OrderType.Market,
            stop ? position.StopLoss : null,
            stop ? null : reason,
            pairId: null,
            clock.GetUtcNow());

        context.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return order;
    }

    /// <summary>
    /// Begins the pair that protects an open position of the Spot market: the stop-loss and the
    /// take-profit, at the prices that the position says, both for what the position holds. It
    /// writes both rows, or neither, before the pair is sent, under the conditions of
    /// <see cref="BeginForPositionAsync"/>, for a position that says a take-profit. Both rows
    /// carry the id of the pair (FR-BIN-S2).
    /// </summary>
    /// <param name="positionId">The position.</param>
    /// <param name="cycleId">The cycle that begins it, or null when none does.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The two orders, or null when none was begun.</returns>
    public async Task<OrderPair?> BeginPairAsync(Guid positionId, Guid? cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await UnguardedAsync(context, positionId, besideTakeProfit: false, cancellationToken) is not { TakeProfit: { } takeProfitPrice } position)
        {
            return null;
        }

        var now = clock.GetUtcNow();

        // The stop-loss first: the rows of an agent are read in the order of their ids. The id
        // of the pair is the id of no row.
        var stopLossId = NextId();
        var takeProfitId = NextId();
        var pairId = NextId();

        var stopLoss = OrderOf(position, stopLossId, cycleId, OrderPurpose.StopLoss, OrderType.Stop, position.StopLoss, reason: null, pairId, now);
        var takeProfit = OrderOf(position, takeProfitId, cycleId, OrderPurpose.TakeProfit, OrderType.Stop, takeProfitPrice, reason: null, pairId, now);

        context.Add(stopLoss);
        context.Add(takeProfit);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new OrderPair(stopLoss, takeProfit);
    }

    /// <summary>
    /// Begins the take-profit of an open position of the Futures market, at the price that the
    /// position says, for what the position holds: writes its row, before it is sent. There the
    /// take-profit is an order of its own, which rests beside the stop-loss (FR-BIN-F3). It is
    /// written only while the stop-loss of the position rests, and the agent has no other order
    /// that is not over: the stop-loss comes first, and no take-profit rests without one.
    /// </summary>
    /// <param name="positionId">The position.</param>
    /// <param name="cycleId">The cycle that begins it, or null when none does.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The order, or null when none was begun.</returns>
    public async Task<Order?> BeginTakeProfitAsync(Guid positionId, Guid? cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await UnderLockAsync(context, positionId, cancellationToken) is not { Market: Market.Futures, TakeProfit: { } price } position)
        {
            return null;
        }

        var live = await LiveOrdersAsync(context, position.AgentId, cancellationToken);

        if (live is not [{ Purpose: OrderPurpose.StopLoss, PairId: null, Status: OrderStatus.Open } stopLoss] || stopLoss.PositionId != position.Id)
        {
            return null;
        }

        var order = OrderOf(
            position, NextId(), cycleId, OrderPurpose.TakeProfit, OrderType.Stop, price, reason: null, pairId: null, clock.GetUtcNow());

        context.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return order;
    }

    /// <summary>
    /// Writes what has become of a pair as a whole, to both of its rows or to neither, when both
    /// still are as they were read: that what the exchange made of the call is not known, or
    /// that the exchange has neither order. What the exchange says of one order of a pair is
    /// written with <see cref="MoveAsync"/>, for that order.
    /// </summary>
    /// <param name="pair">The two orders, as they were read.</param>
    /// <param name="status">What has become of both: nothing of either is executed.</param>
    /// <param name="said">Whether the exchange said it: the rows then say when they were last asked.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written. It was not when one of the rows has moved on.</returns>
    public async Task<bool> MovePairAsync(OrderPair pair, OrderStatus status, bool said, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);

        if (status is not (OrderStatus.Unknown or OrderStatus.NotPlaced or OrderStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "What the exchange says of one order of a pair is written for that order.");
        }

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var order in pair.Both)
        {
            var written = await context.Set<Order>()
                .Where(row => row.Id == order.Id && row.Status == order.Status && row.ExecutedQuantity == order.ExecutedQuantity)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(stored => stored.Status, status)
                        .SetProperty(stored => stored.UpdatedAt, status == order.Status ? order.UpdatedAt : now)
                        .SetProperty(stored => stored.CheckedAt, said ? now : order.CheckedAt),
                    cancellationToken);

            if (written == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Takes both orders of a pair whose outcome is unknown back to being sent, for the one time
    /// that the pair is sent again: the exchange has neither, and they can no longer arrive.
    /// </summary>
    /// <returns>Whether it was written. It was not when one of the rows has moved on, or the pair was sent twice already.</returns>
    public async Task<bool> SendPairAgainAsync(OrderPair pair, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var order in pair.Both)
        {
            var written = await context.Set<Order>()
                .Where(row => row.Id == order.Id && row.Status == order.Status && row.Sends == 1)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(stored => stored.Status, OrderStatus.Sending)
                        .SetProperty(stored => stored.Sends, 2)
                        .SetProperty(stored => stored.UpdatedAt, now),
                    cancellationToken);

            if (written == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>The two orders of a pair as they are stored now, or null when the pair is not two rows: a stop-loss and a take-profit.</summary>
    public async Task<OrderPair?> PairAsync(Guid pairId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = await context.Set<Order>().AsNoTracking().Where(order => order.PairId == pairId).ToListAsync(cancellationToken);

        return orders.SingleOrDefault(order => order.Purpose == OrderPurpose.StopLoss) is { } stopLoss
            && orders.SingleOrDefault(order => order.Purpose == OrderPurpose.TakeProfit) is { } takeProfit
            && orders.Count == 2
                ? new OrderPair(stopLoss, takeProfit)
                : null;
    }

    /// <summary>
    /// Writes what has become of an order, when the row still is as it was read. What follows
    /// for the position that the order protects or closes is written with it: what was executed
    /// of the order is no longer held, and a position of which nothing is held any more, or
    /// whose order is executed in full, is closed.
    /// </summary>
    /// <param name="order">The order, as it was read.</param>
    /// <param name="status">What has become of it.</param>
    /// <param name="exchangeOrderId">The id that the exchange knows it by, or null when the exchange said none.</param>
    /// <param name="executed">How much of it is executed in all, as the exchange said.</param>
    /// <param name="averagePrice">The average price of what is executed, or null when the exchange said none.</param>
    /// <param name="said">Whether the exchange said it: the row then says when it was last asked.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written. It was not when the row has moved on.</returns>
    public async Task<bool> MoveAsync(
        Order order,
        OrderStatus status,
        string? exchangeOrderId,
        decimal executed,
        decimal? averagePrice,
        bool said,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var now = clock.GetUtcNow();

        // What was executed since the row was read. The exchange counts up, and so does the row.
        var more = Math.Max(executed - order.ExecutedQuantity, 0m);
        var total = order.ExecutedQuantity + more;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var written = await context.Set<Order>()
            .Where(row => row.Id == order.Id && row.Status == order.Status && row.ExecutedQuantity == order.ExecutedQuantity)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Status, status)
                    .SetProperty(stored => stored.ExchangeOrderId, exchangeOrderId ?? order.ExchangeOrderId)
                    .SetProperty(stored => stored.ExecutedQuantity, total)
                    .SetProperty(stored => stored.AveragePrice, averagePrice ?? order.AveragePrice)

                    // The row says since when it has its status: a status that stays keeps its time.
                    .SetProperty(stored => stored.UpdatedAt, status == order.Status ? order.UpdatedAt : now)
                    .SetProperty(stored => stored.CheckedAt, said ? now : order.CheckedAt),
                cancellationToken);

        if (written == 0)
        {
            return false;
        }

        if (order.PositionId is { } positionId && order.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit or OrderPurpose.Close)
        {
            var reason = order.Purpose switch
            {
                OrderPurpose.StopLoss => DecisionEngine.ExitReason.StopLoss,
                OrderPurpose.TakeProfit => DecisionEngine.ExitReason.TakeProfit,
                _ => order.ExitReason,
            };

            // Executed in full: the position is closed, as it was held.
            if (status == OrderStatus.Filled)
            {
                await CloseAsync(context, positionId, reason, now, cancellationToken);
            }
            else if (more > 0m)
            {
                // A part: the position holds the rest, and is closed when there is none.
                await context.Set<Position>()
                    .Where(position => position.Id == positionId && position.ClosedAt == null && position.Quantity > more)
                    .ExecuteUpdateAsync(row => row.SetProperty(position => position.Quantity, position => position.Quantity - more), cancellationToken);

                await context.Set<Position>()
                    .Where(position => position.Id == positionId && position.ClosedAt == null && position.Quantity <= more)
                    .ExecuteUpdateAsync(
                        row => row
                            .SetProperty(position => position.ClosedAt, now)
                            .SetProperty(position => position.ExitReason, reason),
                        cancellationToken);
            }

            // A position that was closed because its account no longer held it, of which an
            // order is now found executed: that order is what closed it, and says why.
            if (more > 0m && reason is not null)
            {
                await context.Set<Position>()
                    .Where(position => position.Id == positionId && position.ExitReason == DecisionEngine.ExitReason.ClosedAtExchange)
                    .ExecuteUpdateAsync(row => row.SetProperty(position => position.ExitReason, reason), cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Writes that an entry is executed, and opens the position that it gave, with the
    /// executions where they are known, all of it or none: when the row of the entry still is as
    /// it was read.
    /// </summary>
    /// <param name="entry">The entry, as it was read.</param>
    /// <param name="status">What has become of it: it is over.</param>
    /// <param name="exchangeOrderId">The id that the exchange knows it by.</param>
    /// <param name="executed">How much of it is executed.</param>
    /// <param name="averagePrice">The average price of what is executed.</param>
    /// <param name="held">What the agent holds of it: what was executed, less a fee that was taken in the asset.</param>
    /// <param name="fills">Its executions, or none when they are not known yet.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The position, or null when nothing was written: the row has moved on.</returns>
    public async Task<Position?> OpenAsync(
        Order entry,
        OrderStatus status,
        string exchangeOrderId,
        decimal executed,
        decimal averagePrice,
        decimal held,
        IReadOnlyList<Fill> fills,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fills);

        if (entry.Purpose != OrderPurpose.Entry || entry.StopLoss is not { } stopLoss)
        {
            throw new ArgumentException("A position is opened by an entry, which says the stop-loss of the position.", nameof(entry));
        }

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The agent as it is now: the asset that its symbol is priced in, its timeframe, and the
        // models of its roles are those that the position is linked to for good (FR-EXP-1). None
        // of the first two changes while the agent has an order.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .Where(stored => stored.Id == entry.AgentId)
            .Select(stored => new { stored.QuoteAsset, stored.Timeframe })
            .SingleAsync(cancellationToken);

        var roles = await context.Set<AgentRole>()
            .AsNoTracking()
            .Where(role => role.AgentId == entry.AgentId)
            .ToListAsync(cancellationToken);

        var position = new Position(
            NextId(),
            entry.AgentId,
            entry.UserId,
            entry.Mode,
            entry.Market,
            entry.Symbol,
            agent.QuoteAsset,
            agent.Timeframe,
            entry.Side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short,
            averagePrice,
            held,
            stopLoss,
            entry.TakeProfit,
            now,
            closedAt: null,
            exitReason: null,
            alertedAt: null,

            // A position of the Futures market receives and pays funding, which is read for the
            // first time soon after it is opened (FR-BIN-F4). One of the Spot market has none.
            fundingDueAt: entry.Market == Market.Futures ? now : null);

        // The position first: the entry then names it, and so do the models of its roles.
        context.Add(position);
        context.AddRange(fills);
        context.AddRange(roles.Select(role => new PositionRole(position.Id, position.UserId, role.Role, role.ModelId)));
        await context.SaveChangesAsync(cancellationToken);

        var written = await context.Set<Order>()
            .Where(row => row.Id == entry.Id && row.Status == entry.Status && row.PositionId == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Status, status)
                    .SetProperty(stored => stored.ExchangeOrderId, exchangeOrderId)
                    .SetProperty(stored => stored.ExecutedQuantity, executed)
                    .SetProperty(stored => stored.AveragePrice, averagePrice)
                    .SetProperty(stored => stored.PositionId, position.Id)
                    .SetProperty(stored => stored.FillsRecorded, fills.Count > 0)
                    .SetProperty(stored => stored.UpdatedAt, now)
                    .SetProperty(stored => stored.CheckedAt, now),
                cancellationToken);

        if (written == 0)
        {
            // Another has written what became of the entry. Nothing of this is kept.
            await transaction.RollbackAsync(cancellationToken);

            return null;
        }

        await transaction.CommitAsync(cancellationToken);

        return position;
    }

    /// <summary>
    /// Takes an order whose outcome is unknown back to being sent, for the one time that it is
    /// sent again: the exchange does not have it, and it can no longer arrive.
    /// </summary>
    /// <returns>Whether it was written. It was not when the row has moved on, or was sent twice already.</returns>
    public async Task<bool> SendAgainAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Order>()
            .Where(row => row.Id == order.Id && row.Status == order.Status && row.Sends == 1)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Status, OrderStatus.Sending)
                    .SetProperty(stored => stored.Sends, 2)
                    .SetProperty(stored => stored.UpdatedAt, now),
                cancellationToken) > 0;
    }

    /// <summary>Stores the executions of an order that are not stored yet, and says that they are stored.</summary>
    public async Task RecordFillsAsync(Order order, IReadOnlyList<Fill> fills, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(fills);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row of the order is locked by the write, so two that store its executions store
        // them one after the other.
        await context.Set<Order>()
            .Where(row => row.Id == order.Id)
            .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.FillsRecorded, true), cancellationToken);

        var stored = await context.Set<Fill>()
            .AsNoTracking()
            .Where(fill => fill.OrderId == order.Id)
            .Select(fill => fill.TradeId)
            .ToListAsync(cancellationToken);

        context.AddRange(fills.Where(fill => !stored.Contains(fill.TradeId)));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The executions, of all users, whose fee has no value yet, as far as they are still
    /// valued, each with the order that it is of, in the order of their ids (FR-BIN-10). There
    /// are few: a fee is valued as its execution is stored, unless what values it could not be
    /// read then.
    /// </summary>
    public async Task<IReadOnlyList<(Fill Fill, Order Order)>> UnvaluedFeesAsync(CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - FeesValuedWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var rows = await (
            from fill in context.Set<Fill>().AsNoTracking()
            join order in context.Set<Order>().AsNoTracking() on fill.OrderId equals order.Id
            where fill.FeeValue == null && order.UpdatedAt >= since
            orderby fill.Id
            select new { fill, order })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.fill, row.order))];
    }

    /// <summary>Writes what the fee of an execution is worth, when the execution says nothing of it yet: it is said once.</summary>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> ValueFeeAsync(Guid fillId, decimal value, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Fill>()
            .Where(fill => fill.Id == fillId && fill.FeeValue == null)
            .ExecuteUpdateAsync(row => row.SetProperty(fill => fill.FeeValue, (decimal?)value), cancellationToken) > 0;
    }

    /// <summary>
    /// The positions of the Futures market, of all users, that are watched, in the order of
    /// their ids: every open one, whose margin is checked (FR-BIN-F5), and every one that was
    /// closed not long ago and whose funding is not all read yet (FR-BIN-F4).
    /// </summary>
    public async Task<IReadOnlyList<Position>> ToWatchAsync(CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - FundingWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.Market == Market.Futures
                && (position.ClosedAt == null || (position.FundingDueAt != null && position.ClosedAt >= since)))
            .OrderBy(position => position.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The time that the funding of a position is asked for now: from the newest of its funding
    /// that is stored, which is asked for again and stored once, and to this moment, or to when
    /// the position was closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A position with no funding yet is asked about from when its entry was begun, which is
    /// before anything of it was executed. When the position was opened is when the application
    /// saw that: an entry at a price may have been executed, and have paid funding, a while
    /// before.
    /// </para>
    /// <para>
    /// A closed position is asked about up to the last execution of the orders that closed it,
    /// by the clock of the exchange, where the executions are stored. When the position was
    /// closed is when the application saw that, which for a stop-loss may be minutes later: the
    /// account may hold the symbol for another agent of its user by then, and that one's
    /// funding is not this position's.
    /// </para>
    /// </remarks>
    /// <returns>The first and the last moment, both included. The last is before the first when there is nothing to ask for.</returns>
    public async Task<(DateTimeOffset From, DateTimeOffset To)> FundingWindowAsync(Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking().Where(order => order.PositionId == position.Id);

        var from = await context.Set<Funding>()
                .AsNoTracking()
                .Where(funding => funding.PositionId == position.Id)
                .MaxAsync(funding => (DateTimeOffset?)funding.At, cancellationToken)
            ?? await orders
                .Where(order => order.Purpose == OrderPurpose.Entry)
                .MinAsync(order => (DateTimeOffset?)order.CreatedAt, cancellationToken)
            ?? position.OpenedAt;

        if (position.ClosedAt is not { } closedAt)
        {
            return (from, clock.GetUtcNow());
        }

        var closing = orders.Where(order => order.Purpose != OrderPurpose.Entry).Select(order => order.Id);

        var executed = await context.Set<Fill>()
            .AsNoTracking()
            .Where(fill => closing.Contains(fill.OrderId))
            .MaxAsync(fill => (DateTimeOffset?)fill.At, cancellationToken);

        return (from, executed ?? closedAt);
    }

    /// <summary>
    /// Stores the funding of a position that is not stored yet, and writes when its funding is
    /// read next, both or neither: when the position still says the time that it said as it was
    /// read. So of two that read the funding of a position, one writes.
    /// </summary>
    /// <param name="position">The position, as it was read.</param>
    /// <param name="fundings">What the exchange said that it received and paid.</param>
    /// <param name="dueAt">When its funding is read next, or null when all of it is read.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many rows were stored, or null when nothing was written: the row of the position has moved on.</returns>
    public async Task<int?> RecordFundingAsync(
        Position position, IReadOnlyList<Funding> fundings, DateTimeOffset? dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(fundings);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row of the position is locked by the write, so two that store its funding store
        // it one after the other, and the second finds that the row has moved on.
        var written = await context.Set<Position>()
            .Where(row => row.Id == position.Id && row.FundingDueAt == position.FundingDueAt)
            .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.FundingDueAt, dueAt), cancellationToken);

        if (written == 0)
        {
            return null;
        }

        var stored = await context.Set<Funding>()
            .AsNoTracking()
            .Where(funding => funding.PositionId == position.Id)
            .Select(funding => funding.TransactionId)
            .ToListAsync(cancellationToken);

        var added = fundings
            .Where(funding => !stored.Contains(funding.TransactionId))
            .DistinctBy(funding => funding.TransactionId)
            .ToList();

        context.AddRange(added);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return added.Count;
    }

    /// <summary>Writes the price of the stop-loss of an open position.</summary>
    public async Task StopLossToAsync(Guid positionId, decimal stopLoss, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Position>()
            .Where(position => position.Id == positionId && position.ClosedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(position => position.StopLoss, stopLoss), cancellationToken);
    }

    /// <summary>Writes the price of the take-profit of an open position.</summary>
    public async Task TakeProfitToAsync(Guid positionId, decimal takeProfit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Position>()
            .Where(position => position.Id == positionId && position.ClosedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(position => position.TakeProfit, (decimal?)takeProfit), cancellationToken);
    }

    /// <summary>Writes the prices of the stop-loss and of the take-profit of an open position.</summary>
    public async Task ProtectionToAsync(Guid positionId, decimal stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Position>()
            .Where(position => position.Id == positionId && position.ClosedAt == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(position => position.StopLoss, stopLoss)
                    .SetProperty(position => position.TakeProfit, takeProfit),
                cancellationToken);
    }

    /// <summary>
    /// Writes that the user of an open position was told that it is unprotected and could not
    /// be closed, when they were not told yet.
    /// </summary>
    /// <returns>Whether it was written: whether this is the one that tells.</returns>
    public async Task<bool> AlertedAsync(Guid positionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Position>()
            .Where(position => position.Id == positionId && position.ClosedAt == null && position.AlertedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(position => position.AlertedAt, (DateTimeOffset?)now), cancellationToken) > 0;
    }

    /// <summary>Writes that an open position is protected again: its user is told the next time that it is not.</summary>
    public async Task ProtectedAgainAsync(Guid positionId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Position>()
            .Where(position => position.Id == positionId && position.AlertedAt != null)
            .ExecuteUpdateAsync(row => row.SetProperty(position => position.AlertedAt, (DateTimeOffset?)null), cancellationToken);
    }

    /// <summary>
    /// Writes that an open position is closed because the account of its user no longer holds
    /// it, as the exchange said when it was asked (FR-BIN-7, FR-BIN-8). The exchange is the
    /// source of truth: no order of the agent closed it, so no order says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is written under the lock of the account, and only when the position and the orders of
    /// its agent that are not over still are as they were read, which is as the exchange was
    /// asked about them: an order that another began or saw executed meanwhile is what explains
    /// the account, and then nothing is written.
    /// </para>
    /// <para>
    /// <b>What it held is counted at the price now</b> (FR-BIN-10). No order of the agent sold
    /// it, so no execution says what it was sold at, or for which fee: what it made is what it
    /// would have made at the price that its agent's equity valued it at a moment ago, and is
    /// kept with the position. So the equity of its agent does not move because the position is
    /// gone. Where the price is not known, it counts as closed at the price that it was entered
    /// at, and the position says all the same that its result is an estimate.
    /// </para>
    /// </remarks>
    /// <param name="position">The position, as it was read.</param>
    /// <param name="live">The orders of its agent that are not over, as they were read.</param>
    /// <param name="beside">
    /// The other open positions of the user that what the account holds was shared out among,
    /// each with what it held when the account was read, or null where the position shares
    /// nothing. Nothing is written when one of them holds something else by now: two positions
    /// that share an asset are worked out against each other, and the second of two that are
    /// corrected at the same moment is worked out anew.
    /// </param>
    /// <param name="price">The price that the position is valued at now, or null when the exchange did not say.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> ClosedAtExchangeAsync(
        Position position,
        IReadOnlyList<Order> live,
        IReadOnlyList<(Guid Id, decimal Quantity)>? beside,
        decimal? price,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(live);

        var now = clock.GetUtcNow();
        var made = MadeAt(position, position.Quantity, price);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await AsReadAsync(context, position, live, beside, cancellationToken))
        {
            return false;
        }

        // What writes that an order was executed takes no lock of the account: the row is
        // written only while it still holds what it held.
        var written = await context.Set<Position>()
            .Where(row => row.Id == position.Id && row.ClosedAt == null && row.Quantity == position.Quantity)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.ClosedAt, (DateTimeOffset?)now)
                    .SetProperty(stored => stored.ExitReason, (ExitReason?)ExitReason.ClosedAtExchange)
                    .SetProperty(stored => stored.MadeAtExchange, stored => (decimal?)((stored.MadeAtExchange ?? 0m) + made)),
                cancellationToken);

        if (written != 1)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Writes what an open position holds, as the exchange said that the account of its user
    /// holds it (FR-BIN-7, FR-BIN-8): less than the records said, or more. It is written as
    /// <see cref="ClosedAtExchangeAsync"/> writes, under the same conditions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What left it is counted at the price now,</b> as what a position held that was closed
    /// at the exchange is: see <see cref="ClosedAtExchangeAsync"/>. What was added to it made
    /// nothing yet.
    /// </para>
    /// <para>
    /// <b>The orders that closed a part of it earlier keep the value that they had.</b> Each is
    /// counted against the price that the position was entered at, which is another from now on
    /// where the exchange says another: what that would change of them is kept with the
    /// position, so that it changes nothing.
    /// </para>
    /// </remarks>
    /// <param name="position">The position, as it was read.</param>
    /// <param name="quantity">What it holds, which is more than nothing: a position that holds nothing is closed.</param>
    /// <param name="entryPrice">The average price that it was entered at, as the exchange says it, or null where the exchange says none.</param>
    /// <param name="live">The orders of its agent that are not over, as they were read.</param>
    /// <param name="beside">The other open positions of the user that share what the account holds, as they were read, or null.</param>
    /// <param name="price">The price that the position is valued at now, or null when the exchange did not say.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> HeldAsync(
        Position position,
        decimal quantity,
        decimal? entryPrice,
        IReadOnlyList<Order> live,
        IReadOnlyList<(Guid Id, decimal Quantity)>? beside,
        decimal? price,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var entered = entryPrice ?? position.EntryPrice;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await AsReadAsync(context, position, live, beside, cancellationToken))
        {
            return false;
        }

        var made = MadeAt(position, Math.Max(position.Quantity - quantity, 0m), price);

        if (entered != position.EntryPrice)
        {
            var closedEarlier = await context.Set<Order>()
                .AsNoTracking()
                .Where(order => order.PositionId == position.Id && order.Purpose != OrderPurpose.Entry)
                .SumAsync(order => order.ExecutedQuantity, cancellationToken);

            made += MadeAt(position, closedEarlier, entered);
        }

        var written = await context.Set<Position>()
            .Where(row => row.Id == position.Id && row.ClosedAt == null && row.Quantity == position.Quantity)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Quantity, quantity)
                    .SetProperty(stored => stored.EntryPrice, entered)
                    .SetProperty(stored => stored.MadeAtExchange, stored => (decimal?)((stored.MadeAtExchange ?? 0m) + made)),
                cancellationToken);

        if (written != 1)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>One position, as it is stored now, or null when there is none of that id.</summary>
    public async Task<Position?> PositionAsync(Guid positionId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Position>().AsNoTracking().SingleOrDefaultAsync(position => position.Id == positionId, cancellationToken);
    }

    /// <summary>The open position of an agent, read with a context that the caller holds.</summary>
    public static Task<Position?> OpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        context.Set<Position>()
            .AsNoTracking()
            .SingleOrDefaultAsync(position => position.AgentId == agentId && position.ClosedAt == null, cancellationToken);

    /// <summary>The orders of an agent that are not over, read with a context that the caller holds.</summary>
    public static async Task<IReadOnlyList<Order>> LiveOrdersAsync(
        EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        await context.Set<Order>()
            .AsNoTracking()
            .Where(order => order.AgentId == agentId && OrderStatuses.Live.Contains(order.Status))
            .OrderBy(order => order.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The id of the next order or position. An id starts with a time in milliseconds, and each
    /// one that is handed out is of a later millisecond than the one before. So the ids of the
    /// rows that this process writes are in the order the rows were written in, also for two
    /// rows of the same moment of the application's clock.
    /// </summary>
    private static Guid NextId()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long last;
        long next;
        do
        {
            last = Volatile.Read(ref _lastMillisecond);
            next = Math.Max(now, last + 1);
        }
        while (Interlocked.CompareExchange(ref _lastMillisecond, next, last) != last);

        return Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(next));
    }

    /// <summary>
    /// Why no entry is begun for an agent, or null when one is: its state as it is now, whatever
    /// the cycle was handed, for its user may have paused or stopped it while its team decided,
    /// and what it holds. Both are read under the lock of the account, in the transaction of the
    /// context.
    /// </summary>
    private async Task<EntryRefusal?> EntryRefusalAsync(EcnanibDbContext context, Agent agent, CancellationToken cancellationToken)
    {
        if (await agents.StateUnderLockAsync(context, agent.UserId, agent.Id, cancellationToken) is not { } state
            || !AgentStates.TakesNewEntries(state))
        {
            return EntryRefusal.NotRunning;
        }

        return await OpenPositionAsync(context, agent.Id, cancellationToken) is not null
            || (await LiveOrdersAsync(context, agent.Id, cancellationToken)).Count > 0
                ? EntryRefusal.Holding
                : null;
    }

    /// <summary>
    /// A position as it is under the lock of its account, when an order that protects or closes
    /// it may be begun: it is open, holds something, and its agent has no order that is not
    /// over. Null otherwise. In whatever state the agent is: protecting and closing a position
    /// reduce risk.
    /// </summary>
    /// <param name="context">The context, which holds a transaction.</param>
    /// <param name="positionId">The position.</param>
    /// <param name="besideTakeProfit">
    /// Whether the order may be begun beside the take-profit of the position that is an order
    /// of its own, and of no pair: a stop-loss may.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task<Position?> UnguardedAsync(
        EcnanibDbContext context, Guid positionId, bool besideTakeProfit, CancellationToken cancellationToken)
    {
        if (await UnderLockAsync(context, positionId, cancellationToken) is not { } position)
        {
            return null;
        }

        var live = await LiveOrdersAsync(context, position.AgentId, cancellationToken);

        return live.All(order => besideTakeProfit
            && order is { Purpose: OrderPurpose.TakeProfit, PairId: null }
            && order.PositionId == position.Id)
                ? position
                : null;
    }

    /// <summary>
    /// A position as it is under the lock of its account, when it is open and holds something.
    /// Null otherwise.
    /// </summary>
    private async Task<Position?> UnderLockAsync(EcnanibDbContext context, Guid positionId, CancellationToken cancellationToken)
    {
        var owner = await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.Id == positionId)
            .Select(position => new { position.UserId, position.AgentId })
            .SingleOrDefaultAsync(cancellationToken);

        if (owner is null || await agents.StateUnderLockAsync(context, owner.UserId, owner.AgentId, cancellationToken) is null)
        {
            return null;
        }

        // The position as it is under the lock.
        var position = await context.Set<Position>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == positionId, cancellationToken);

        return position is null || position.ClosedAt is not null || position.Quantity <= 0m ? null : position;
    }

    /// <summary>
    /// Whether a position and the orders of its agent that are not over still are as they were
    /// read, under the lock of the account: the position is open and holds what it held, and the
    /// orders are those that were read, each as far as it was, with nothing more executed. And
    /// the other open positions of the user, where what the account holds was shared out among
    /// them, are those that were read, each holding what it held: the lock is of the account,
    /// so of all of them. The context holds a transaction.
    /// </summary>
    private async Task<bool> AsReadAsync(
        EcnanibDbContext context,
        Position position,
        IReadOnlyList<Order> live,
        IReadOnlyList<(Guid Id, decimal Quantity)>? beside,
        CancellationToken cancellationToken)
    {
        if (await UnderLockAsync(context, position.Id, cancellationToken) is not { } now || now.Quantity != position.Quantity)
        {
            return false;
        }

        var orders = await LiveOrdersAsync(context, position.AgentId, cancellationToken);

        if (orders.Count != live.Count
            || !orders.Zip(live).All(both =>
                both.First.Id == both.Second.Id
                && both.First.Status == both.Second.Status
                && both.First.ExecutedQuantity == both.Second.ExecutedQuantity))
        {
            return false;
        }

        if (beside is null)
        {
            return true;
        }

        var others = await context.Set<Position>()
            .AsNoTracking()
            .Where(row => row.UserId == position.UserId
                && row.Mode == position.Mode
                && row.Market == position.Market
                && row.ClosedAt == null
                && row.Id != position.Id)
            .OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.Quantity })
            .ToListAsync(cancellationToken);

        return others.Count == beside.Count
            && others.Zip(beside.OrderBy(other => other.Id)).All(both =>
                both.First.Id == both.Second.Id && both.First.Quantity == both.Second.Quantity);
    }

    /// <summary>The agents that hold an open position, or an order that is not over.</summary>
    private static IQueryable<Guid> Holding(EcnanibDbContext context) =>
        context.Set<Order>()
            .AsNoTracking()
            .Where(order => OrderStatuses.Live.Contains(order.Status))
            .Select(order => order.AgentId)
            .Union(context.Set<Position>().AsNoTracking().Where(position => position.ClosedAt == null).Select(position => position.AgentId));

    /// <summary>
    /// The orders that protected a position which is closed, are of no pair, and are not over.
    /// The two orders of a pair are the exchange's to end, as one list.
    /// </summary>
    private static IQueryable<Order> LeftBehind(EcnanibDbContext context) =>
        context.Set<Order>()
            .AsNoTracking()
            .Where(order => (order.Purpose == OrderPurpose.StopLoss || order.Purpose == OrderPurpose.TakeProfit)
                && order.PairId == null
                && OrderStatuses.Live.Contains(order.Status)
                && context.Set<Position>().Any(position => position.Id == order.PositionId && position.ClosedAt != null));

    /// <summary>The row of an order that protects or closes a position, for what the position holds, as it is before it is sent.</summary>
    private static Order OrderOf(
        Position position,
        Guid id,
        Guid? cycleId,
        OrderPurpose purpose,
        OrderType type,
        decimal? price,
        ExitReason? reason,
        Guid? pairId,
        DateTimeOffset now) => new(
            id,
            position.AgentId,
            position.UserId,
            position.Mode,
            position.Market,
            position.Symbol,
            cycleId,
            position.Id,
            Order.ClientOrderIdOf(id),
            exchangeOrderId: null,
            purpose,
            type,
            position.ClosingSide,
            price,
            stopLoss: null,
            takeProfit: null,
            position.Quantity,
            OrderStatus.Sending,
            executedQuantity: 0m,
            averagePrice: null,
            reason,
            sends: 1,
            fillsRecorded: false,
            now,
            now,
            checkedAt: null,
            pairId,
            cancelAfter: null);

    /// <summary>
    /// What a quantity of a position makes at a price, counted from the price that the position
    /// was entered at. Nothing where the price is not known, and where the numbers cannot be
    /// worked out with, which are no numbers of a market.
    /// </summary>
    private static decimal MadeAt(Position position, decimal quantity, decimal? price)
    {
        if (price is not { } at || quantity <= 0m)
        {
            return 0m;
        }

        try
        {
            return Position.Made(position.Side, quantity, position.EntryPrice, at);
        }
        catch (OverflowException)
        {
            return 0m;
        }
    }

    private static Task CloseAsync(
        EcnanibDbContext context, Guid positionId, ExitReason? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Set<Position>()
            .Where(position => position.Id == positionId && position.ClosedAt == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(position => position.ClosedAt, now)
                    .SetProperty(position => position.ExitReason, reason),
                cancellationToken);
}

/// <summary>
/// The two orders of a pair that protects a position of the Spot market, as they are stored: the
/// exchange holds them as one list, and ends one when the other is triggered (FR-BIN-S2).
/// </summary>
/// <param name="StopLoss">The stop-loss.</param>
/// <param name="TakeProfit">The take-profit.</param>
internal sealed record OrderPair(Order StopLoss, Order TakeProfit)
{
    /// <summary>The id of the pair, which both carry.</summary>
    public Guid Id => StopLoss.PairId ?? throw new InvalidOperationException("The orders of a pair carry the id of the pair.");

    /// <summary>Both, the stop-loss first.</summary>
    public IReadOnlyList<Order> Both => [StopLoss, TakeProfit];
}

/// <summary>The account of a user on a market in an environment, in which an agent of theirs holds something.</summary>
/// <param name="UserId">Whose account.</param>
/// <param name="Mode">The environment.</param>
/// <param name="Market">The market.</param>
internal sealed record HeldAccount(Guid UserId, TradingMode Mode, Market Market);

/// <summary>Why no entry was begun for an agent.</summary>
public enum EntryRefusal
{
    /// <summary>The agent does not open positions now: its user paused or stopped it, or it waits for its user.</summary>
    NotRunning,

    /// <summary>The agent holds a position already, or has an order that is not over.</summary>
    Holding,
}
