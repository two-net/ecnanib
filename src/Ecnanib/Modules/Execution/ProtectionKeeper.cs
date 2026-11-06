using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using ExchangeSide = Ecnanib.Modules.ExchangeAdapter.Contracts.OrderSide;
using SizedSide = Ecnanib.Modules.RiskEngine.Contracts.OrderSide;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Keeps an agent's fills protected (FR-EXE-02, AC-02, NFR-REL-02). One pass settles every order whose outcome isn't
/// known by its client order ID (FR-EXE-05), checks unfilled entries and cancels them at their time-to-live (FR-EXE-01),
/// gives each new fill a protection, places stops and targets, closes at market what can't get a stop in time, and
/// cancels orders whose position is gone. Its caller holds the agent's lock, so passes never overlap. Nothing is sent
/// again unless Binance has shown it was never placed (NFR-REL-03). See Order execution in docs/architecture.md.
/// </summary>
internal sealed class ProtectionKeeper(
    OrderStore store,
    IExchangeTrading trading,
    IExchangeAccount accounts,
    IExchangeSymbols symbols,
    IRiskEngine engine,
    IAgents agents,
    ExecutionSettings settings,
    TimeProvider time,
    ILogger<ProtectionKeeper> logger)
{
    /// <summary>
    /// How long a row written before a send may take to settle. Above any send's acceptance window: a clock sync, the
    /// adapter's 10-second trade timeout, a receive window of up to 10 seconds and its margin.
    /// </summary>
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(2);

    /// <summary>A fill found late, such as after a restart, still gets this long to have its stop placed.</summary>
    public static readonly TimeSpan MinimumProtecting = TimeSpan.FromSeconds(ExecutionSettings.MinimumProtectingSeconds);

    /// <summary>Between an order Binance rejected and the next attempt, so a run of rejections doesn't spend the order limit.</summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Binance's highest standard Spot fee, assumed when an order's trades can't be read. A stop then sells a little less
    /// than was bought, never more (FR-AGT-09).
    /// </summary>
    private const decimal AssumedSpotFee = 0.001m;

    /// <summary>Binance's code for cancelling an order it has no open record of.</summary>
    private const int UnknownOrder = -2011;

    /// <summary>
    /// One pass over the agent's orders. Exchange failures are logged and left for the next pass. Only the database
    /// failing throws.
    /// </summary>
    public async Task<KeeperPass> AdvanceAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var orders = await store.LoadAsync(session.UserId, session.AgentId, cancellationToken);

        foreach (var rows in orders.Entries.Concat(orders.Legs).Where(row => row.IsUnsettled).GroupBy(row => row.ListClientOrderId ?? row.ClientOrderId).ToList())
        {
            await SettleAsync(session, orders, [.. rows], cancellationToken);
        }

        foreach (var entry in orders.Entries.Where(entry => entry.Status is OrderStatus.Open or OrderStatus.PartiallyFilled).ToList())
        {
            await CheckEntryAsync(session, entry, cancellationToken);
        }

        foreach (var entry in orders.Entries.Where(entry => !entry.Resolved).ToList())
        {
            await ProtectFillsAsync(session, orders, entry, cancellationToken);
        }

        foreach (var protection in orders.Protections.Where(protection => NeedsWork(session, orders, protection)).ToList())
        {
            await AdvanceAsync(session, orders, protection, cancellationToken);
        }

        await CancelOrphansAsync(session, orders, cancellationToken);

        return Summarize(session, orders);
    }

    /// <summary>
    /// Gives every protection that's waiting for its stop a full window again from now. After a restart, its time ran
    /// out while nobody could act, and a stop should still be tried before closing (NFR-REL-02).
    /// </summary>
    public async Task ResumeAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var orders = await store.LoadAsync(session.UserId, session.AgentId, cancellationToken);
        var now = time.GetUtcNow();
        var waiting = orders.Protections.Where(protection => protection.Status == ProtectionStatus.Pending).ToList();

        foreach (var protection in waiting)
        {
            protection.ProtectBy = Max(protection.ProtectBy, now + settings.ProtectiveStop);
            protection.UpdatedAt = now;
        }

        if (waiting.Count > 0)
        {
            await store.SaveAsync([], waiting);
        }
    }

    /// <summary>
    /// Whether the agent still holds a position from an earlier entry, which no new entry may be sized against until
    /// reconciliation (#35) gives the Risk Engine the position. Its stops and targets are read again first, so a
    /// position they closed doesn't count. On Futures, Binance's position for the pair decides, and orders left from
    /// a position that's gone are cancelled, so they can't act on a new one.
    /// </summary>
    /// <returns>True when a position is held, or when that can't be ruled out.</returns>
    public async Task<bool> HoldsPositionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var pass = await AdvanceAsync(session, cancellationToken);
        var orders = pass.Orders;
        var now = time.GetUtcNow();
        var held = orders.Protections.Where(protection => OrderStore.HeldProtections.Contains(protection.Status)).ToList();

        if (orders.Entries.Any(entry => entry.IsLive))
        {
            return true;
        }

        foreach (var protection in held.Where(protection => protection.Status == ProtectionStatus.Resting))
        {
            var legs = orders.LegsOf(protection).Where(leg => leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Triggered).ToList();

            foreach (var leg in legs)
            {
                await ReadAsync(session, leg, RequestPriority.Normal, now, cancellationToken);
            }

            if (Ended(orders, protection))
            {
                protection.Status = ProtectionStatus.Ended;
                protection.UpdatedAt = now;
                logger.LogInformation(
                    "Protection {Sequence} of entry {ClientOrderId} has ended: its stop or target is no longer open",
                    protection.Sequence,
                    orders.EntryOf(protection).ClientOrderId);
            }

            await store.SaveAsync(legs, [protection]);
        }

        if (!session.Futures)
        {
            return held.Any(protection => OrderStore.HeldProtections.Contains(protection.Status));
        }

        IReadOnlyList<FuturesPosition> positions;

        try
        {
            positions = await accounts.GetPositionAsync(session.Key, session.Symbol.Symbol, RequestPriority.Protective, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Binance couldn't give the {Symbol} position, so no entry is sent: {Failure}", session.Symbol.Symbol, exception.Failure);
            return true;
        }

        if (positions.Any(position => position.Amount != 0m))
        {
            return true;
        }

        // Nothing is held, so whatever still rests from an earlier entry is an orphan that could act on a new one, such as a
        // take-profit whose stop has filled.
        foreach (var protection in orders.Protections.Where(protection => protection.Status is ProtectionStatus.Resting or ProtectionStatus.Ended))
        {
            if (orders.LegsOf(protection).Any(leg => leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled))
            {
                await CancelLegsAsync(session, orders, protection, includeUnsettled: false, cancellationToken);
            }

            if (protection.Status == ProtectionStatus.Resting && !orders.LegsOf(protection).Any(leg => leg.IsLive))
            {
                protection.Status = ProtectionStatus.Ended;
                protection.UpdatedAt = time.GetUtcNow();
                await store.SaveAsync(protection);
            }
        }

        return orders.Protections.Any(protection => OrderStore.HeldProtections.Contains(protection.Status))
            || orders.Legs.Any(leg => leg.IsLive);
    }

    /// <summary>
    /// Cancels an unfilled entry, at its time-to-live (FR-EXE-01) or for CANCEL_PENDING, then reads it again: whatever
    /// filled before the cancel needs protecting.
    /// </summary>
    public async Task CancelEntryAsync(AgentSession session, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        try
        {
            await trading.CancelOrderAsync(session.Market, session.Key, Reference(entry), RequestPriority.Normal, cancellationToken);
            logger.LogInformation("Cancelled entry {ClientOrderId}", entry.ClientOrderId);
        }
        catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: UnknownOrder })
        {
            // It ended meanwhile, or was never placed. Reading it says which.
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Couldn't cancel entry {ClientOrderId}, so it's tried again: {Failure}", entry.ClientOrderId, exception.Failure);
            return;
        }

        await ReadAsync(session, entry, RequestPriority.Protective, now, cancellationToken);
        await store.SaveAsync(entry);
    }

    /// <summary>Settles one order, or one Spot list, whose outcome isn't known, by its client order ID.</summary>
    private async Task SettleAsync(AgentSession session, AgentOrders orders, List<OrderRow> rows, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var first = rows[0];

        try
        {
            if (first.ListClientOrderId is { } listId)
            {
                var list = await accounts.GetOrderListAsync(
                    session.Key, new OrderListReference(first.Symbol, listId), RequestPriority.Protective, cancellationToken);

                if (list is null)
                {
                    Missing(orders, rows, now);
                }
                else
                {
                    foreach (var row in rows)
                    {
                        row.ExchangeOrderId ??= list.Orders.FirstOrDefault(member => member.ClientOrderId == row.ClientOrderId)?.ExchangeOrderId;

                        // The list exists, so its orders do, whatever a read of one of them says.
                        if (!await ReadAsync(session, row, RequestPriority.Protective, now, cancellationToken) && row.IsUnsettled)
                        {
                            row.Status = OrderStatus.Open;
                            row.UpdatedAt = now;
                        }
                    }
                }
            }
            else
            {
                var order = await accounts.GetOrderAsync(session.Market, session.Key, Reference(first), RequestPriority.Protective, cancellationToken);

                if (order is null)
                {
                    Missing(orders, rows, now);
                }
                else
                {
                    Apply(first, order, now);
                }
            }
        }
        catch (ExchangeException exception)
        {
            logger.LogInformation(
                "Order {ClientOrderId} isn't settled yet: {Failure}, settles at {SettlesAt:O}", first.ClientOrderId, exception.Failure, first.SettlesAt);

            foreach (var row in rows.Where(row => row.Status == OrderStatus.Sending))
            {
                row.Status = OrderStatus.Unknown;
                row.UpdatedAt = now;
            }
        }

        await store.SaveAsync(rows, []);
    }

    /// <summary>
    /// Binance has no such order. That proves it was never placed only once it can no longer be accepted, which the
    /// row knows even after a restart (FR-EXE-05). A target proven missing isn't tried again.
    /// </summary>
    private void Missing(AgentOrders orders, List<OrderRow> rows, DateTimeOffset now)
    {
        if (now < rows.Max(row => row.SettlesAt))
        {
            foreach (var row in rows.Where(row => row.Status == OrderStatus.Sending))
            {
                row.Status = OrderStatus.Unknown;
                row.UpdatedAt = now;
            }

            return;
        }

        foreach (var row in rows)
        {
            row.Status = OrderStatus.NotPlaced;
            row.UpdatedAt = now;
            row.FinishedAt = now;
            logger.LogWarning("Order {ClientOrderId}, whose outcome was unknown, was never placed", row.ClientOrderId);
        }

        foreach (var protection in rows.Where(row => row.Leg == OrderLeg.Target && row.ProtectionId is not null)
            .Select(row => orders.Protections.FirstOrDefault(protection => protection.Id == row.ProtectionId))
            .OfType<ProtectionRow>()
            .Where(protection => orders.LegsOf(protection).All(leg => leg.Leg != OrderLeg.Target || !leg.IsLive)))
        {
            protection.TargetDropped = true;
        }
    }

    /// <summary>Reads an order and applies what Binance says. False when Binance has no such order, or the read failed.</summary>
    private async Task<bool> ReadAsync(AgentSession session, OrderRow row, RequestPriority priority, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            if (await accounts.GetOrderAsync(session.Market, session.Key, Reference(row), priority, cancellationToken) is { } order)
            {
                Apply(row, order, now);
                return true;
            }
        }
        catch (ExchangeException exception)
        {
            logger.LogInformation("Couldn't read order {ClientOrderId}: {Failure}", row.ClientOrderId, exception.Failure);
        }

        return false;
    }

    /// <summary>
    /// Checks an unfilled or partly filled entry for fills, and cancels it at its time-to-live. A market entry that
    /// Binance's answer didn't show filled is checked every pass, because it fills at once and its stop can't wait.
    /// </summary>
    private async Task CheckEntryAsync(AgentSession session, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expired = entry.ExpiresAt is { } expires && expires <= now;

        if (!expired && entry.Type != OrderKind.Market && now - entry.UpdatedAt < settings.PendingEntryCheck)
        {
            return;
        }

        // Unfilled, it's an ordinary read. Once something has filled, a stop depends on it (FR-EXE-09).
        var priority = entry.ExecutedQuantity > 0m ? RequestPriority.Protective : RequestPriority.Normal;

        await ReadAsync(session, entry, priority, now, cancellationToken);
        entry.UpdatedAt = now;

        if (expired && entry.Status is OrderStatus.Open or OrderStatus.PartiallyFilled)
        {
            logger.LogInformation("Entry {ClientOrderId}'s time-to-live has ended, so it's cancelled", entry.ClientOrderId);
            await CancelEntryAsync(session, entry, cancellationToken);
        }

        await store.SaveAsync(entry);
    }

    /// <summary>
    /// Gives the part of an entry that filled since its last protection a protection of its own, and resolves the entry
    /// once it has ended and every part has one.
    /// </summary>
    private async Task ProtectFillsAsync(AgentSession session, AgentOrders orders, OrderRow entry, CancellationToken cancellationToken)
    {
        if (entry.IsUnsettled || entry.ProtectiveStop is not { } stop)
        {
            return;
        }

        var now = time.GetUtcNow();
        var existing = orders.ProtectionsOf(entry).ToList();
        var assigned = existing.Sum(protection => protection.Quantity);
        var changed = false;
        var remainder = 0m;

        if (entry.ExecutedQuantity > 0m)
        {
            if (entry.NetBasis != entry.ExecutedQuantity)
            {
                entry.NetQuantity = session.Futures ? entry.ExecutedQuantity : await NetAsync(session, entry, cancellationToken);
                entry.NetBasis = entry.ExecutedQuantity;
                changed = true;
            }

            var increment = Lots.Down(entry.NetQuantity!.Value - assigned, Lots.Of(session.Symbol));

            if (Lots.Placeable(increment, stop, session) && existing.Count <= ClientOrderIds.MaximumIndex)
            {
                var protection = new ProtectionRow
                {
                    Id = Guid.NewGuid(),
                    UserId = entry.UserId,
                    AgentId = entry.AgentId,
                    EntryId = entry.Id,
                    Sequence = existing.Count,
                    Quantity = increment,
                    Status = ProtectionStatus.Pending,

                    // AC-02 counts from the fill. One found late still gets a few seconds for its stop.
                    ProtectBy = Max((entry.FilledAt ?? now) + settings.ProtectiveStop, now + MinimumProtecting),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await store.InsertAsync(protection, cancellationToken);
                orders.Protections.Add(protection);
                assigned += increment;

                logger.LogInformation(
                    "Entry {ClientOrderId} filled {Quantity} more, which protection {Sequence} must have a stop for by {ProtectBy:O}",
                    entry.ClientOrderId,
                    increment,
                    protection.Sequence,
                    protection.ProtectBy);
            }

            remainder = entry.NetQuantity.Value - assigned;
        }

        if (!entry.IsLive && !Lots.Placeable(Lots.Down(remainder, Lots.Of(session.Symbol)), stop, session))
        {
            if (entry.ExecutedQuantity > 0m && assigned == 0m)
            {
                logger.LogError(
                    "Entry {ClientOrderId} filled {Quantity}, which is too small for Binance to take a stop or a close for",
                    entry.ClientOrderId,
                    entry.NetQuantity);
            }
            else if (remainder > 0m)
            {
                logger.LogWarning(
                    "Entry {ClientOrderId} left {Quantity} without a stop, because it's below Binance's minimum order", entry.ClientOrderId, remainder);
            }

            entry.Resolved = true;
            entry.UpdatedAt = now;
            changed = true;
        }

        if (changed)
        {
            await store.SaveAsync(entry);
        }
    }

    /// <summary>
    /// What a Spot buy left in the account: the executed quantity, less the commission Binance took in the base asset.
    /// When the trades are behind the order, the fee seen so far is scaled up to the whole.
    /// </summary>
    private async Task<decimal> NetAsync(AgentSession session, OrderRow entry, CancellationToken cancellationToken)
    {
        var executed = entry.ExecutedQuantity;

        if (entry.ExchangeOrderId is { } orderId)
        {
            try
            {
                var trades = await accounts.GetOrderTradesAsync(
                    Market.Spot, session.Key, entry.Symbol, orderId, RequestPriority.Protective, cancellationToken);
                var filled = trades.Sum(trade => trade.Quantity);

                if (filled > 0m)
                {
                    var fee = trades.Where(trade => trade.CommissionAsset == session.Symbol.BaseAsset).Sum(trade => trade.Commission);

                    return executed - (fee * executed / filled);
                }
            }
            catch (ExchangeException exception)
            {
                logger.LogWarning(
                    "Couldn't read entry {ClientOrderId}'s trades, so the highest standard fee is assumed: {Failure}", entry.ClientOrderId, exception.Failure);
            }
        }

        return executed - (executed * AssumedSpotFee);
    }

    private bool NeedsWork(AgentSession session, AgentOrders orders, ProtectionRow protection) =>
        protection.Status is ProtectionStatus.Pending or ProtectionStatus.Closing
        || (protection.Status == ProtectionStatus.Resting && NeedsTarget(session, orders, protection));

    /// <summary>A Futures target is its own order, placed once the stop rests. Spot's is in its OCO list.</summary>
    private static bool NeedsTarget(AgentSession session, AgentOrders orders, ProtectionRow protection) =>
        session.Futures
        && !protection.TargetDropped
        && orders.EntryOf(protection).Target is not null
        && !orders.LegsOf(protection).Any(leg => leg.Leg == OrderLeg.Target && leg.Status is not (OrderStatus.NotPlaced or OrderStatus.Rejected));

    private async Task AdvanceAsync(AgentSession session, AgentOrders orders, ProtectionRow protection, CancellationToken cancellationToken)
    {
        var entry = orders.EntryOf(protection);
        var now = time.GetUtcNow();

        if (protection.Status == ProtectionStatus.Pending)
        {
            var stop = Latest(orders, protection, OrderLeg.Stop);

            if (stop is { Status: OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Triggered or OrderStatus.Filled or OrderStatus.Finished })
            {
                await RestAsync(session, protection, cancellationToken);
            }
            else if (stop is { IsUnsettled: true })
            {
                // Its outcome isn't known. Past the deadline, the position is closed without waiting: on Futures a late
                // reduce-only stop does no harm and is cancelled, and on Spot it can't lock base that's been sold.
                if (now >= protection.ProtectBy)
                {
                    await BeginCloseAsync(protection, entry, "its stop's outcome is unknown", cancellationToken);
                }
            }
            else if (now >= protection.ProtectBy)
            {
                await BeginCloseAsync(protection, entry, "its stop wasn't placed in time", cancellationToken);
            }
            else
            {
                switch (await PlaceStopAsync(session, orders, protection, entry, cancellationToken))
                {
                    case StopOutcome.Rested:
                        await RestAsync(session, protection, cancellationToken);
                        break;
                    case StopOutcome.WouldTrigger:
                        await BeginCloseAsync(protection, entry, "the price has already passed its stop", cancellationToken);
                        break;
                    case StopOutcome.Exhausted:
                        await BeginCloseAsync(protection, entry, "Binance rejected every attempt at its stop", cancellationToken);
                        break;
                }
            }
        }

        if (protection.Status == ProtectionStatus.Resting && NeedsTarget(session, orders, protection))
        {
            await PlaceTargetAsync(session, orders, protection, entry, cancellationToken);
        }

        if (protection.Status == ProtectionStatus.Closing)
        {
            await CloseAsync(session, orders, protection, entry, cancellationToken);
        }
    }

    /// <summary>
    /// Places a stop for the protection, or on Spot with a target an OCO list (FR-EXE-02, FR-EXE-03). An attempt that
    /// Binance never received keeps its client order ID. Any other gets a new one, so no ID is sent twice.
    /// </summary>
    private async Task<StopOutcome> PlaceStopAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var latest = Latest(orders, protection, OrderLeg.Stop);

        if (latest is { Status: OrderStatus.Rejected } && now - latest.UpdatedAt < RetryPause)
        {
            return StopOutcome.Waiting;
        }

        var oco = !session.Futures && entry.Target is not null && !protection.TargetDropped;
        var side = Closing(entry.Side);
        List<OrderRow> rows;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null } && (latest.ListClientOrderId is not null) == oco)
        {
            rows = latest.ListClientOrderId is { } listId ? [.. orders.LegsOf(protection).Where(leg => leg.ListClientOrderId == listId)] : [latest];

            foreach (var row in rows)
            {
                Resend(row, now);
            }

            await store.SaveAsync(rows, []);
        }
        else
        {
            if (protection.Attempts > ClientOrderIds.MaximumIndex)
            {
                return StopOutcome.Exhausted;
            }

            var attempt = protection.Attempts++;
            protection.UpdatedAt = now;
            var stopId = ClientOrderIds.Stop(entry.AgentId, Candle(entry), protection.Sequence, attempt);
            var listId = oco ? ClientOrderIds.List(entry.AgentId, Candle(entry), protection.Sequence, attempt) : null;

            rows = [Leg(entry, protection, OrderLeg.Stop, OrderKind.StopMarket, stopId, side, stopPrice: entry.ProtectiveStop, listId: listId, now: now)];

            if (oco)
            {
                var targetId = ClientOrderIds.Target(entry.AgentId, Candle(entry), protection.Sequence, attempt);
                rows.Add(Leg(entry, protection, OrderLeg.Target, OrderKind.LimitMaker, targetId, side, price: entry.Target, listId: listId, now: now));
            }

            await store.InsertAsync(rows, protection, cancellationToken);
            orders.Legs.AddRange(rows);
        }

        var stopRow = rows.Single(row => row.Leg == OrderLeg.Stop);

        try
        {
            if (stopRow.ListClientOrderId is { } list)
            {
                var targetRow = rows.Single(row => row.Leg == OrderLeg.Target);
                var placed = await trading.PlaceOrderListAsync(
                    session.Key,
                    new OrderListRequest(
                        entry.Symbol,
                        side,
                        protection.Quantity,
                        entry.Target!.Value,
                        entry.ProtectiveStop!.Value,
                        list,
                        targetRow.ClientOrderId,
                        stopRow.ClientOrderId,
                        RequestPriority.Protective),
                    cancellationToken);

                foreach (var row in rows)
                {
                    Sent(row, now);
                    row.ExchangeOrderId = placed.Orders.FirstOrDefault(member => member.ClientOrderId == row.ClientOrderId)?.ExchangeOrderId;
                    row.Status = placed.Status == OrderListStatus.Rejected ? OrderStatus.Rejected : OrderStatus.Open;
                }
            }
            else
            {
                var placed = await trading.PlaceOrderAsync(
                    session.Market,
                    session.Key,
                    new OrderRequest(
                        entry.Symbol,
                        side,
                        OrderType.StopMarket,
                        protection.Quantity,
                        stopRow.ClientOrderId,
                        RequestPriority.Protective,
                        stopPrice: entry.ProtectiveStop,
                        reduceOnly: session.Futures),
                    cancellationToken);

                Sent(stopRow, now);
                Apply(stopRow, placed, now);
            }
        }
        catch (ExchangeException exception)
        {
            Failed(rows, exception, now);

            if (stopRow.ListClientOrderId is not null && exception is { Failure: ExchangeFailure.Rejected }
                && exception.Rejection != OrderRejection.WouldTriggerImmediately)
            {
                // The stop is what matters. A target Binance won't take, such as one the price has already reached, is
                // dropped, and the next attempt is a stop alone.
                protection.TargetDropped = true;
                logger.LogWarning(
                    "Binance rejected the OCO list for protection {Sequence} of entry {ClientOrderId}, so a stop is placed without the target: code {ErrorCode}",
                    protection.Sequence,
                    entry.ClientOrderId,
                    exception.Code);
            }

            await store.SaveAsync(rows, [protection]);
            await RefreshIfFilterAsync(session, exception, cancellationToken);

            return exception.Rejection == OrderRejection.WouldTriggerImmediately ? StopOutcome.WouldTrigger : StopOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            // Only before the send began, so it never reached Binance.
            NotSent(rows, now);
            await store.SaveAsync(rows, []);
            throw;
        }

        await store.SaveAsync(rows, []);

        return stopRow.Status is OrderStatus.Rejected or OrderStatus.Canceled or OrderStatus.Expired ? StopOutcome.Failed : StopOutcome.Rested;
    }

    /// <summary>A Futures target, reduce-only on the mark price like the stop (FR-EXE-03). One Binance refuses is dropped.</summary>
    private async Task PlaceTargetAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var latest = Latest(orders, protection, OrderLeg.Target);
        OrderRow row;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null })
        {
            row = latest;
            Resend(row, now);
            await store.SaveAsync(row);
        }
        else if (latest is not null)
        {
            // Rejected, or shown never to have been placed: it isn't tried again.
            protection.TargetDropped = true;
            protection.UpdatedAt = now;
            await store.SaveAsync(protection);
            return;
        }
        else
        {
            var id = ClientOrderIds.Target(entry.AgentId, Candle(entry), protection.Sequence, 0);
            row = Leg(entry, protection, OrderLeg.Target, OrderKind.TakeProfitMarket, id, Closing(entry.Side), stopPrice: entry.Target, now: now);
            await store.InsertAsync([row], cancellationToken);
            orders.Legs.Add(row);
        }

        try
        {
            var placed = await trading.PlaceOrderAsync(
                session.Market,
                session.Key,
                new OrderRequest(
                    entry.Symbol,
                    row.Side,
                    OrderType.TakeProfitMarket,
                    protection.Quantity,
                    row.ClientOrderId,
                    RequestPriority.Normal,
                    stopPrice: entry.Target,
                    reduceOnly: true),
                cancellationToken);

            Sent(row, now);
            Apply(row, placed, now);
            await store.SaveAsync(row);
        }
        catch (ExchangeException exception)
        {
            Failed([row], exception, now);

            if (exception.Failure == ExchangeFailure.Rejected)
            {
                protection.TargetDropped = true;
                logger.LogWarning(
                    "Binance rejected the target for protection {Sequence} of entry {ClientOrderId}, so only its stop rests: code {ErrorCode}",
                    protection.Sequence,
                    entry.ClientOrderId,
                    exception.Code);
            }

            await store.SaveAsync([row], [protection]);
        }
        catch (OperationCanceledException)
        {
            NotSent([row], now);
            await store.SaveAsync(row);
            throw;
        }
    }

    private async Task RestAsync(AgentSession session, ProtectionRow protection, CancellationToken cancellationToken)
    {
        protection.Status = ProtectionStatus.Resting;
        protection.UpdatedAt = time.GetUtcNow();

        logger.LogInformation("Protection {Sequence} rests on Binance", protection.Sequence);

        // The trade opened once its first part is protected.
        var notice = protection.Sequence == 0 ? await NoticeAsync(session, NotificationType.TradeOpened, cancellationToken) : null;
        await store.SaveAsync(protection, notice);
    }

    private async Task BeginCloseAsync(ProtectionRow protection, OrderRow entry, string reason, CancellationToken cancellationToken)
    {
        protection.Status = ProtectionStatus.Closing;
        protection.UpdatedAt = time.GetUtcNow();

        logger.LogError(
            "Protection {Sequence} of entry {ClientOrderId} is closed at market, because {Reason}. {Quantity} has no stop",
            protection.Sequence,
            entry.ClientOrderId,
            reason,
            protection.Quantity);

        await store.SaveAsync(protection);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Closes the protection's quantity at market, after a passing evaluation of its own (FR-EXE-02, AC-01). On Spot its
    /// stop is cancelled first, which frees the base a resting stop locks. On Futures the reduce-only close goes at once,
    /// and the stop is cancelled after.
    /// </summary>
    private async Task CloseAsync(AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var latest = Latest(orders, protection, OrderLeg.EmergencyClose);

        if (latest is { Status: OrderStatus.Open or OrderStatus.PartiallyFilled })
        {
            await ReadAsync(session, latest, RequestPriority.Protective, now, cancellationToken);
            await store.SaveAsync(latest);
        }

        if (latest is { Status: OrderStatus.Filled })
        {
            await ClosedAsync(session, protection, entry, latest, cancellationToken);
            return;
        }

        if (latest is { IsLive: true } || (latest is { Status: OrderStatus.Rejected } && now - latest.UpdatedAt < RetryPause))
        {
            return;
        }

        if (!session.Futures)
        {
            await CancelLegsAsync(session, orders, protection, includeUnsettled: true, cancellationToken);
        }

        var evaluation = await EvaluateCloseAsync(session, protection, entry, cancellationToken);

        if (evaluation.Order is not { Quantity: > 0m } sized)
        {
            protection.Status = ProtectionStatus.Dust;
            protection.UpdatedAt = now;
            logger.LogError(
                "Protection {Sequence} of entry {ClientOrderId} can't be closed: the Risk Engine failed the close: {Failures}",
                protection.Sequence,
                entry.ClientOrderId,
                string.Join(", ", evaluation.Failures.Select(rule => $"{rule.Rule} {rule.Reason}")));
            await store.SaveAsync(protection);
            return;
        }

        OrderRow row;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null } && latest.Quantity == sized.Quantity)
        {
            row = latest;
            Resend(row, now);
            await store.SaveAsync(row);
        }
        else
        {
            var attempt = orders.LegsOf(protection).Count(leg => leg.Leg == OrderLeg.EmergencyClose);

            if (attempt > ClientOrderIds.MaximumIndex)
            {
                protection.Status = ProtectionStatus.Abandoned;
                protection.UpdatedAt = now;
                logger.LogCritical(
                    "Binance rejected every close of protection {Sequence} of entry {ClientOrderId}. {Quantity} has no stop, and the user must close it",
                    protection.Sequence,
                    entry.ClientOrderId,
                    protection.Quantity);
                await store.SaveAsync(protection);
                return;
            }

            row = new OrderRow
            {
                Id = Guid.NewGuid(),
                UserId = entry.UserId,
                AgentId = entry.AgentId,
                CycleId = entry.CycleId,
                CandleClose = entry.CandleClose,
                Origin = OrderOrigin.System,
                SystemEvaluation = ExecutionJson.Write(evaluation),
                CredentialId = entry.CredentialId,
                Market = entry.Market,
                Environment = entry.Environment,
                Symbol = entry.Symbol,
                Leg = OrderLeg.EmergencyClose,
                EntryId = entry.Id,
                ProtectionId = protection.Id,
                ClientOrderId = ClientOrderIds.Close(entry.AgentId, Candle(entry), protection.Sequence, attempt),
                Type = OrderKind.Market,
                Side = sized.Side == SizedSide.Buy ? ExchangeSide.Buy : ExchangeSide.Sell,
                Quantity = sized.Quantity.Value,

                // The engine marks every exit reduce-only. Spot has no such thing, and the adapter refuses it.
                ReduceOnly = session.Futures && sized.ReduceOnly,
                Status = OrderStatus.Sending,
                SettlesAt = now + SendWindow,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await store.InsertAsync([row], cancellationToken);
            orders.Legs.Add(row);
        }

        try
        {
            var placed = await trading.PlaceOrderAsync(
                session.Market,
                session.Key,
                new OrderRequest(row.Symbol, row.Side, OrderType.Market, row.Quantity, row.ClientOrderId, RequestPriority.Protective, reduceOnly: row.ReduceOnly),
                cancellationToken);

            Sent(row, now);
            Apply(row, placed, now);
            await store.SaveAsync(row);
        }
        catch (ExchangeException exception)
        {
            Failed([row], exception, now);
            logger.LogError(
                "Closing protection {Sequence} of entry {ClientOrderId} failed, so it's tried again: {Failure}, code {ErrorCode}",
                protection.Sequence,
                entry.ClientOrderId,
                exception.Failure,
                exception.Code);
            await store.SaveAsync(row);
            await RefreshIfFilterAsync(session, exception, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            NotSent([row], now);
            await store.SaveAsync(row);
            throw;
        }

        if (session.Futures)
        {
            await CancelLegsAsync(session, orders, protection, includeUnsettled: false, cancellationToken);
        }

        if (row.Status == OrderStatus.Filled)
        {
            await ClosedAsync(session, protection, entry, row, cancellationToken);
        }
    }

    private async Task ClosedAsync(AgentSession session, ProtectionRow protection, OrderRow entry, OrderRow close, CancellationToken cancellationToken)
    {
        protection.Status = ProtectionStatus.Closed;
        protection.UpdatedAt = time.GetUtcNow();

        logger.LogWarning(
            "Protection {Sequence} of entry {ClientOrderId} was closed at market at {AveragePrice}, because its stop couldn't be placed",
            protection.Sequence,
            entry.ClientOrderId,
            close.AveragePrice);

        await store.SaveAsync(protection, await NoticeAsync(session, NotificationType.TradeClosed, cancellationToken));
    }

    /// <summary>
    /// The close's own evaluation (AC-01), as an exit of <see cref="IntentOrigin.System"/> that only the structural rules
    /// can stop. On Spot the position is what the agent's fills gave it (FR-AGT-09). The price is the entry's, which is
    /// all the exit rules use, for Spot's dust check, and Binance measures again anyway.
    /// </summary>
    private async Task<RiskEvaluation> EvaluateCloseAsync(
        AgentSession session, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(session.UserId, session.AgentId, cancellationToken);
        var price = entry.AveragePrice ?? entry.Price ?? entry.ProtectiveStop!.Value;
        decimal? baseHeld = null;

        if (!session.Futures)
        {
            try
            {
                var account = await accounts.GetAccountAsync(Market.Spot, session.Key, RequestPriority.Protective, cancellationToken);
                baseHeld = account.Balances.FirstOrDefault(balance => balance.Asset == session.Symbol.BaseAsset)?.Total ?? 0m;
            }
            catch (ExchangeException exception)
            {
                // The engine closes the position's quantity without it, and Binance refuses what the account doesn't hold.
                logger.LogWarning("Couldn't read the Spot account before closing, so the close is sized without it: {Failure}", exception.Failure);
            }
        }

        var inputs = new RiskInputs
        {
            Market = session.Market,
            Now = time.GetUtcNow(),
            Settings = agent?.Settings.Risk ?? RiskSettings.Default,
            CapitalAllocation = agent?.Settings.CapitalAllocation ?? 0m,
            Filters = Filters(session.Symbol),
            Prices = new MarketPrices(price, price, price, session.Futures ? price : null),
            AvailableQuote = 0m,
            BaseHeld = baseHeld,
            Position = new OpenPosition(
                entry.Side == ExchangeSide.Buy ? PositionSide.Long : PositionSide.Short,
                protection.Quantity,
                price,
                Stop: entry.ProtectiveStop,
                Target: entry.Target),
        };

        return engine.Evaluate(
            new OrderIntent { Action = session.Futures ? IntentAction.Close : IntentAction.Sell, Origin = IntentOrigin.System },
            inputs);
    }

    /// <summary>
    /// Cancels the protection's stop and target. With <paramref name="includeUnsettled"/>, orders whose outcome isn't
    /// known are cancelled too, in case they rest. A cancel that finds nothing leaves such an order to be settled.
    /// </summary>
    private async Task CancelLegsAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, bool includeUnsettled, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var legs = orders.LegsOf(protection)
            .Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target
                && (leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled || (includeUnsettled && leg.IsUnsettled)))
            .ToList();

        foreach (var group in legs.GroupBy(leg => leg.ListClientOrderId ?? leg.ClientOrderId).Select(group => group.ToList()))
        {
            var first = group[0];

            try
            {
                if (first.ListClientOrderId is { } listId)
                {
                    await trading.CancelOrderListAsync(session.Key, new OrderListReference(first.Symbol, listId), RequestPriority.Protective, cancellationToken);
                }
                else
                {
                    await trading.CancelOrderAsync(session.Market, session.Key, Reference(first), RequestPriority.Protective, cancellationToken);
                }

                foreach (var leg in group)
                {
                    leg.Status = OrderStatus.Canceled;
                    leg.UpdatedAt = now;
                    leg.FinishedAt = now;
                }

                logger.LogInformation("Cancelled order {ClientOrderId}", first.ListClientOrderId ?? first.ClientOrderId);
            }
            catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: UnknownOrder })
            {
                // Nothing open has that ID. One known to rest has ended meanwhile, perhaps by filling, which a read tells.
                foreach (var leg in group.Where(leg => !leg.IsUnsettled))
                {
                    await ReadAsync(session, leg, RequestPriority.Protective, now, cancellationToken);
                }
            }
            catch (ExchangeException exception)
            {
                logger.LogWarning("Couldn't cancel order {ClientOrderId}, so it's tried again: {Failure}", first.ClientOrderId, exception.Failure);
            }

            await store.SaveAsync(group, []);
        }
    }

    /// <summary>Stops and targets still resting for a part of a position that's gone: closed, ended, or abandoned.</summary>
    private async Task CancelOrphansAsync(AgentSession session, AgentOrders orders, CancellationToken cancellationToken)
    {
        foreach (var protection in orders.Protections
            .Where(protection => protection.Status is ProtectionStatus.Closed or ProtectionStatus.Ended or ProtectionStatus.Dust)
            .Where(protection => orders.LegsOf(protection).Any(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled))
            .ToList())
        {
            await CancelLegsAsync(session, orders, protection, includeUnsettled: false, cancellationToken);
        }
    }

    private async Task RefreshIfFilterAsync(AgentSession session, ExchangeException exception, CancellationToken cancellationToken)
    {
        if (!exception.IsFilterRejection)
        {
            return;
        }

        try
        {
            await session.RefreshSymbolAsync(symbols, cancellationToken);
        }
        catch (Exception refresh) when (refresh is MarketDataException or ExchangeException)
        {
            logger.LogWarning("Couldn't refresh {Symbol}'s filters after Binance rejected an order on one", session.Symbol.Symbol);
        }
    }

    private async Task<Notice?> NoticeAsync(AgentSession session, NotificationType type, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(session.UserId, session.AgentId, cancellationToken);

        return agent is null ? null : new Notice(type, session.UserId, $"/agents/{session.AgentId}/settings", agent.Settings.Name, agent.IsDemo);
    }

    private KeeperPass Summarize(AgentSession session, AgentOrders orders)
    {
        var urgent = orders.Protections.Any(protection => protection.Status is ProtectionStatus.Pending or ProtectionStatus.Closing)
            || orders.Entries.Concat(orders.Legs).Any(row => row.IsUnsettled)
            || orders.Entries.Any(entry => entry.Type == OrderKind.Market && entry.IsLive);

        var hasWork = urgent
            || orders.Entries.Any(entry => !entry.Resolved)
            || orders.Protections.Any(protection => protection.Status == ProtectionStatus.Resting && NeedsTarget(session, orders, protection))
            || orders.Protections.Any(protection => protection.Status is ProtectionStatus.Closed or ProtectionStatus.Ended or ProtectionStatus.Dust
                && orders.LegsOf(protection).Any(leg => leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled));

        return new KeeperPass(orders, hasWork, urgent);
    }

    /// <summary>Whether a resting protection's stop or target has ended, so what it protected is no longer held.</summary>
    private static bool Ended(AgentOrders orders, ProtectionRow protection)
    {
        var legs = orders.LegsOf(protection).Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target && leg.Status is not (OrderStatus.NotPlaced or OrderStatus.Rejected)).ToList();
        var stop = legs.LastOrDefault(leg => leg.Leg == OrderLeg.Stop);

        return stop is not null && !stop.IsLive
            || legs.Any(leg => leg.Leg == OrderLeg.Target && leg.Status is OrderStatus.Filled or OrderStatus.Finished);
    }

    private static OrderRow? Latest(AgentOrders orders, ProtectionRow protection, OrderLeg leg) =>
        orders.LegsOf(protection).Where(row => row.Leg == leg).OrderBy(row => row.CreatedAt).LastOrDefault();

    private static OrderRow Leg(
        OrderRow entry,
        ProtectionRow protection,
        OrderLeg leg,
        OrderKind type,
        string clientOrderId,
        ExchangeSide side,
        DateTimeOffset now,
        decimal? price = null,
        decimal? stopPrice = null,
        string? listId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = entry.UserId,
        AgentId = entry.AgentId,
        CycleId = entry.CycleId,
        CandleClose = entry.CandleClose,
        Origin = OrderOrigin.Council,
        CredentialId = entry.CredentialId,
        Market = entry.Market,
        Environment = entry.Environment,
        Symbol = entry.Symbol,
        Leg = leg,
        EntryId = entry.Id,
        ProtectionId = protection.Id,
        ListClientOrderId = listId,
        ClientOrderId = clientOrderId,
        Type = type,
        Side = side,
        Quantity = protection.Quantity,
        Price = price,
        StopPrice = stopPrice,
        ReduceOnly = entry.Market == Market.Futures,
        Status = OrderStatus.Sending,
        SettlesAt = now + SendWindow,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>What Binance says of an order, onto its row.</summary>
    public static void Apply(OrderRow row, ExchangeOrder order, DateTimeOffset now)
    {
        if (order.ExchangeOrderId > 0)
        {
            row.ExchangeOrderId ??= order.ExchangeOrderId;
        }

        if (order.ExecutedQuantity > row.ExecutedQuantity)
        {
            row.FilledAt = order.UpdatedAt is { } at && at <= now ? at : now;
            row.ExecutedQuantity = order.ExecutedQuantity;
        }

        row.AveragePrice = order.AveragePrice ?? row.AveragePrice;
        row.Status = order.Status switch
        {
            ExchangeOrderStatus.New => OrderStatus.Open,
            ExchangeOrderStatus.PartiallyFilled => OrderStatus.PartiallyFilled,
            ExchangeOrderStatus.Filled => OrderStatus.Filled,
            ExchangeOrderStatus.Canceled => OrderStatus.Canceled,
            ExchangeOrderStatus.Rejected => OrderStatus.Rejected,
            ExchangeOrderStatus.Expired => OrderStatus.Expired,
            ExchangeOrderStatus.Triggered => OrderStatus.Triggered,
            ExchangeOrderStatus.Finished => OrderStatus.Finished,

            // Treated as open until it's read again.
            _ => row.IsLive && !row.IsUnsettled ? row.Status : OrderStatus.Open,
        };
        row.UpdatedAt = now;

        if (!row.IsLive)
        {
            row.FinishedAt ??= now;
        }
    }

    /// <summary>What a failed send means for its rows.</summary>
    public static void Failed(IEnumerable<OrderRow> rows, ExchangeException exception, DateTimeOffset now)
    {
        foreach (var row in rows)
        {
            switch (exception.Failure)
            {
                case ExchangeFailure.Unknown:
                    // The adapter knows exactly when it can no longer be accepted. The window written before the send
                    // is only for a crash that loses that.
                    Sent(row, now);
                    row.Status = OrderStatus.Unknown;
                    row.SettlesAt = exception.SettlesAt ?? row.SettlesAt;
                    break;
                case ExchangeFailure.Rejected:
                    Sent(row, now);
                    row.Status = OrderStatus.Rejected;
                    row.ErrorCode = exception.Code;
                    row.FinishedAt = now;
                    break;
                default:
                    // Throttled, banned, or the clock couldn't be synced: it never reached Binance.
                    row.Status = OrderStatus.NotPlaced;
                    row.ErrorCode = exception.Code;
                    break;
            }

            row.UpdatedAt = now;
        }
    }

    public static void NotSent(IEnumerable<OrderRow> rows, DateTimeOffset now)
    {
        foreach (var row in rows)
        {
            row.Status = OrderStatus.NotPlaced;
            row.UpdatedAt = now;
        }
    }

    public static OrderReference Reference(OrderRow row) => new(row.Symbol, row.ClientOrderId, row.Type switch
    {
        OrderKind.Market => OrderType.Market,
        OrderKind.StopMarket => OrderType.StopMarket,
        OrderKind.TakeProfitMarket => OrderType.TakeProfitMarket,
        _ => OrderType.Limit,
    });

    public static ExchangeFilters Filters(ExchangeSymbolInfo symbol) => new(
        symbol.IsTrading,
        symbol.Price is { } price ? new TickRule(price.Minimum, price.Maximum, price.TickSize) : null,
        symbol.Quantity is { } lot ? new LotRule(lot.Minimum, lot.Maximum, lot.StepSize) : null,
        symbol.MarketQuantity is { } market ? new LotRule(market.Minimum, market.Maximum, market.StepSize) : null,
        symbol.Notional is { } notional ? new NotionalRule(notional.Minimum, notional.Maximum) : null,
        symbol.PercentPrice is { } band ? new PercentPriceRule(band.BuyUp, band.BuyDown, band.SellUp, band.SellDown) : null);

    /// <summary>A row sent again under its own client order ID, which Binance never received.</summary>
    private static void Resend(OrderRow row, DateTimeOffset now)
    {
        row.Status = OrderStatus.Sending;
        row.ErrorCode = null;
        row.SettlesAt = now + SendWindow;
        row.UpdatedAt = now;
    }

    private static void Sent(OrderRow row, DateTimeOffset now)
    {
        row.SentAt ??= now;
        row.UpdatedAt = now;
    }

    private static ExchangeSide Closing(ExchangeSide entrySide) => entrySide == ExchangeSide.Buy ? ExchangeSide.Sell : ExchangeSide.Buy;

    /// <summary>The cycle an entry's IDs are derived from, which its own ID holds.</summary>
    private static DateTimeOffset Candle(OrderRow entry) => entry.CandleClose;

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private enum StopOutcome
    {
        Rested,
        Failed,
        Waiting,
        WouldTrigger,
        Exhausted,
    }
}

/// <summary>What one pass found.</summary>
/// <param name="HasWork">Something still needs a pass: an unfilled entry, a stop not yet resting, an unsettled order.</param>
/// <param name="Urgent">A fill waits for its stop, a close is under way, or an order's outcome isn't known.</param>
internal sealed record KeeperPass(AgentOrders Orders, bool HasWork, bool Urgent);
