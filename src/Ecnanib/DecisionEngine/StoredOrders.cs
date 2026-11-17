using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Keeps the orders, the executions, and the positions of agents: every read of them, and every
/// write. It calls no exchange, and knows nothing of one: what places the orders tells it what
/// the exchange said. See "The record of an order" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An order is begun before it is sent.</b> Beginning one writes its row, inside a
/// transaction that first takes the lock of the agent's account, as the controls of the agent's
/// user do. Under that lock it is asked what the agent holds, so two that begin an order of the
/// same agent begin one after the other, and the second finds what the first wrote. An index
/// holds an agent to one order of each purpose that is not over, and to one open position,
/// whatever asks.
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
    /// order of which it is not known whether the exchange has it, and no open position without
    /// a stop-loss that rests on the exchange.
    /// </summary>
    public async Task<bool> IsSettledAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking();

        if (await orders.AnyAsync(order => order.AgentId == agentId && OrderStatuses.Unsettled.Contains(order.Status), cancellationToken))
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
    /// of their ids: one with an order of unknown outcome, one with an open position that has no
    /// stop-loss resting, one whose resting stop-loss the exchange was last asked about before a
    /// moment, and one with executions that are not stored yet.
    /// </summary>
    /// <param name="checkedBefore">A stop-loss that the exchange was asked about before this is asked about again.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<Guid>> ToSettleAsync(DateTimeOffset checkedBefore, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - FillsWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking();

        var ofOrders = orders
            .Where(order => OrderStatuses.Unsettled.Contains(order.Status)
                || (OrderStatuses.Resting.Contains(order.Status) && (order.CheckedAt == null || order.CheckedAt < checkedBefore))
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

        return await ofOrders.Union(ofPositions).OrderBy(id => id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Begins the entry of an agent: writes its row, before it is sent. It is written only for
    /// an agent that opens positions now, and holds nothing: no open position, and no order that
    /// is not over. Both are asked under the lock of the account, so a stop of the agent's user
    /// and an entry cannot cross.
    /// </summary>
    /// <returns>The order, or why none was begun.</returns>
    public async Task<(Order? Order, EntryRefusal? Refusal)> BeginEntryAsync(
        Agent agent,
        Guid cycleId,
        OrderSide side,
        decimal quantity,
        decimal stopLoss,
        decimal? takeProfit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The state as it is now, whatever the cycle was handed: its user may have paused or
        // stopped the agent while its team decided.
        if (await agents.StateUnderLockAsync(context, agent.UserId, agent.Id, cancellationToken) is not { } state
            || !AgentStates.TakesNewEntries(state))
        {
            return (null, EntryRefusal.NotRunning);
        }

        if (await OpenPositionAsync(context, agent.Id, cancellationToken) is not null
            || (await LiveOrdersAsync(context, agent.Id, cancellationToken)).Count > 0)
        {
            return (null, EntryRefusal.Holding);
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
            OrderType.Market,
            side,
            price: null,
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
            checkedAt: null);

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
    /// closed over a stop-loss that still rests.
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

        var owner = await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.Id == positionId)
            .Select(position => new { position.UserId, position.AgentId })
            .SingleOrDefaultAsync(cancellationToken);

        // In whatever state the agent is: protecting and closing a position reduce risk.
        if (owner is null || await agents.StateUnderLockAsync(context, owner.UserId, owner.AgentId, cancellationToken) is null)
        {
            return null;
        }

        // The position as it is under the lock.
        var position = await context.Set<Position>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == positionId, cancellationToken);

        if (position is null
            || position.ClosedAt is not null
            || position.Quantity <= 0m
            || (await LiveOrdersAsync(context, position.AgentId, cancellationToken)).Count > 0)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var id = NextId();
        var stop = purpose == OrderPurpose.StopLoss;

        var order = new Order(
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
            stop ? OrderType.Stop : OrderType.Market,
            position.ClosingSide,
            stop ? position.StopLoss : null,
            stopLoss: null,
            takeProfit: null,
            position.Quantity,
            OrderStatus.Sending,
            executedQuantity: 0m,
            averagePrice: null,
            stop ? null : reason,
            sends: 1,
            fillsRecorded: false,
            now,
            now,
            checkedAt: null);

        context.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return order;
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

        if (order.PositionId is { } positionId && order.Purpose is OrderPurpose.StopLoss or OrderPurpose.Close)
        {
            var reason = order.Purpose == OrderPurpose.StopLoss ? DecisionEngine.ExitReason.StopLoss : order.ExitReason;

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

        var position = new Position(
            NextId(),
            entry.AgentId,
            entry.UserId,
            entry.Mode,
            entry.Market,
            entry.Symbol,
            entry.Side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short,
            averagePrice,
            held,
            stopLoss,
            entry.TakeProfit,
            now,
            closedAt: null,
            exitReason: null,
            alertedAt: null);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The position first: the entry then names it.
        context.Add(position);
        context.AddRange(fills);
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

/// <summary>Why no entry was begun for an agent.</summary>
public enum EntryRefusal
{
    /// <summary>The agent does not open positions now: its user paused or stopped it, or it waits for its user.</summary>
    NotRunning,

    /// <summary>The agent holds a position already, or has an order that is not over.</summary>
    Holding,
}
