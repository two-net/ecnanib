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
/// gives each new fill a protection, places stops and targets, brings resting ones in line with a moved stop or
/// target, closes at market what can't get a stop in time or what an exit sells, and cancels orders whose position is
/// gone. Its caller holds the agent's lock, so passes never overlap. Nothing is sent again unless Binance has shown it
/// was never placed (NFR-REL-03). What happened outside Ecnanib is <see cref="Reconciler"/>'s. See Order execution in
/// docs/architecture.md.
/// </summary>
internal sealed class ProtectionKeeper(
    OrderStore store,
    IExchangeTrading trading,
    IExchangeAccount accounts,
    IExchangeSymbols symbols,
    IRiskEngine engine,
    IAgents agents,
    TradeBook book,
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
    /// One pass over the agent's orders, after which the agent's trades are booked. Exchange failures are logged and left
    /// for the next pass. Only the database failing throws.
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

        if (session.Liquidating)
        {
            // The kill switch (FR-AGT-08): no entry may fill any more. One whose outcome isn't known is cancelled too, in
            // case Binance has it. What filled before the cancel becomes a part below, and is closed.
            foreach (var entry in orders.Entries.Where(entry => !entry.Resolved && entry.IsLive && entry.Status != OrderStatus.Triggered).ToList())
            {
                logger.LogWarning("Entry {ClientOrderId} is cancelled, because the agent's kill switch was used", entry.ClientOrderId);
                await CancelEntryAsync(session, entry, cancellationToken, RequestPriority.Protective);
            }
        }

        foreach (var entry in orders.Entries.Where(entry => !entry.Resolved).ToList())
        {
            await ProtectFillsAsync(session, orders, entry, cancellationToken);
        }

        if (session.Liquidating)
        {
            // A part waiting for its stop has nothing resting that could execute meanwhile, so it's closed at once. A
            // resting part waits for reconciliation to confirm its stop hasn't executed (see Reconciler).
            foreach (var protection in orders.Protections.Where(protection => protection.Status == ProtectionStatus.Pending).ToList())
            {
                BeginKill(protection, orders.EntryOf(protection));
                await store.SaveAsync(protection);
            }
        }

        foreach (var protection in orders.Protections.Where(protection => NeedsWork(session, orders, protection)).ToList())
        {
            await AdvanceAsync(session, orders, protection, cancellationToken);
        }

        await CancelOrphansAsync(session, orders, cancellationToken);

        // Every part that ends, ends in a pass or between two of the reconciler's, so the journal hears of it here
        // (FR-EXE-07). It's the database only, and never fails the pass.
        await book.TryBookAsync(session.UserId, session.AgentId, session.Symbol, cancellationToken);

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
    /// Cancels an unfilled entry, at its time-to-live (FR-EXE-01) or for CANCEL_PENDING, then reads it again: whatever
    /// filled before the cancel needs protecting.
    /// </summary>
    /// <param name="priority">Normal, unless it's the kill switch's, which is a protective action (FR-EXE-09).</param>
    public async Task CancelEntryAsync(
        AgentSession session, OrderRow entry, CancellationToken cancellationToken, RequestPriority priority = RequestPriority.Normal)
    {
        var now = time.GetUtcNow();

        try
        {
            await trading.CancelOrderAsync(session.Market, session.Key, Reference(entry), priority, cancellationToken);
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

    /// <summary>
    /// Cancels the protection's stop and target. With <paramref name="includeUnsettled"/>, orders whose outcome isn't
    /// known are cancelled too, in case they rest. A cancel that finds nothing leaves such an order to be settled, and
    /// reads one known to rest, which may have filled meanwhile.
    /// </summary>
    public async Task CancelLegsAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, bool includeUnsettled, CancellationToken cancellationToken)
    {
        var legs = orders.LegsOf(protection)
            .Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target
                && (leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled || (includeUnsettled && leg.IsUnsettled)))
            .ToList();

        await CancelAsync(session, legs, cancellationToken);
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
    /// once it has ended and every part has one. An add's first protection moves the rest of the position's stops to
    /// its own, which the Risk Engine sized it against: one stop guards the whole position.
    /// </summary>
    private async Task ProtectFillsAsync(AgentSession session, AgentOrders orders, OrderRow entry, CancellationToken cancellationToken)
    {
        if (entry.IsUnsettled || entry.ProtectiveStop is not { } stop)
        {
            return;
        }

        var now = time.GetUtcNow();
        var existing = orders.ProtectionsOf(entry).ToList();

        // A split protection's parts hold what it did, so it isn't counted twice.
        var assigned = existing.Where(protection => protection.EndReason != ProtectionEnd.Split).Sum(protection => protection.Quantity);
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
                if (existing.Count == 0)
                {
                    await AlignStopsAsync(orders, entry, stop, now);
                }

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
                    StopPrice = stop,
                    TargetPrice = entry.Target,
                    EntryPrice = entry.AveragePrice ?? entry.Price ?? stop,
                    OpenedAt = entry.FilledAt ?? now,
                    CycleId = entry.CycleId,
                    IdCandleClose = entry.CandleClose,
                    IdIndex = existing.Count,
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
    /// An add's stop guards the whole position (FR-RISK-06), so the parts held from earlier entries take it wherever it's
    /// tighter, citing the add's cycle. Their stops are moved on Binance by <see cref="ConvergeAsync"/>.
    /// </summary>
    private async Task AlignStopsAsync(AgentOrders orders, OrderRow add, decimal stop, DateTimeOffset now)
    {
        var side = SideOf(add);
        var older = orders.Protections
            .Where(protection => protection.EntryId != add.Id && protection.Status is ProtectionStatus.Pending or ProtectionStatus.Resting)
            .Where(protection => Tightens(side, protection.StopPrice, stop))
            .ToList();

        foreach (var protection in older)
        {
            logger.LogInformation(
                "Entry {ClientOrderId} adds to the position, so protection {Sequence} of an earlier entry moves its stop from {From} to {To}",
                add.ClientOrderId,
                protection.Sequence,
                protection.StopPrice,
                stop);

            protection.StopPrice = stop;
            protection.CycleId = add.CycleId;
            protection.UpdatedAt = now;
        }

        if (older.Count > 0)
        {
            await store.SaveAsync([], older);
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

    private static bool NeedsWork(AgentSession session, AgentOrders orders, ProtectionRow protection) =>
        protection.Status is ProtectionStatus.Pending or ProtectionStatus.Closing
        || (protection.Status == ProtectionStatus.Resting && (NeedsTarget(session, orders, protection) || !Converged(session, orders, protection)));

    /// <summary>
    /// A Futures target is its own order, placed once the stop rests. Spot's is in its OCO list. One that executed, or
    /// that Binance refused, isn't placed again.
    /// </summary>
    private static bool NeedsTarget(AgentSession session, AgentOrders orders, ProtectionRow protection) =>
        session.Futures
        && !protection.TargetDropped
        && protection.TargetPrice is { } target
        && !orders.LegsOf(protection).Any(leg => leg.Leg == OrderLeg.Target && leg.StopPrice == target
            && (leg.IsLive || leg.Status is OrderStatus.Filled or OrderStatus.Finished));

    /// <summary>
    /// Whether a resting protection's orders match its stop and target. One whose stop no longer rests is left to
    /// reconciliation, which finds out whether it executed or was cancelled outside Ecnanib.
    /// </summary>
    private static bool Converged(AgentSession session, AgentOrders orders, ProtectionRow protection)
    {
        var legs = orders.LegsOf(protection).Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target).ToList();
        var stops = legs.Where(leg => leg.Leg == OrderLeg.Stop && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled).ToList();

        if (stops.Count == 0 || legs.Any(leg => leg.IsUnsettled))
        {
            return true;
        }

        var target = protection.TargetDropped ? null : protection.TargetPrice;
        var targets = legs.Where(leg => leg.Leg == OrderLeg.Target && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled).ToList();

        return session.Futures
            ? stops.Count == 1 && stops[0].StopPrice == protection.StopPrice && targets.All(leg => leg.StopPrice == target)
            : stops[^1].StopPrice == protection.StopPrice && (target is null ? targets.Count == 0 : targets.Any(leg => leg.Price == target));
    }

    private async Task AdvanceAsync(AgentSession session, AgentOrders orders, ProtectionRow protection, CancellationToken cancellationToken)
    {
        var entry = orders.EntryOf(protection);
        var now = time.GetUtcNow();

        if (protection.Status == ProtectionStatus.Resting && !Converged(session, orders, protection))
        {
            await ConvergeAsync(session, orders, protection, entry, cancellationToken);
        }

        if (protection.Status == ProtectionStatus.Pending)
        {
            var stop = Latest(orders, protection, OrderLeg.Stop);

            if (stop is { Status: OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Triggered or OrderStatus.Filled or OrderStatus.Finished }
                && stop.StopPrice == protection.StopPrice)
            {
                await RestAsync(session, orders, protection, cancellationToken);
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
                        await RestAsync(session, orders, protection, cancellationToken);
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
    /// Brings a resting protection's orders in line with its stop and target, after an add or an adjustment moved them
    /// (FR-CYC-03). On Futures the new stop is placed first and the old one cancelled once it rests, so the position is
    /// never without one, and a target that's moved or removed is cancelled. On Spot the stop and target lock the same
    /// base, so the stop or OCO list is cancelled, and the protection waits for a new one, with a fresh window, as a
    /// fill does (FR-EXE-02).
    /// </summary>
    private async Task ConvergeAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        if (!session.Futures)
        {
            await CancelLegsAsync(session, orders, protection, includeUnsettled: false, cancellationToken);

            var legs = orders.LegsOf(protection).Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target).ToList();

            // A cancel that didn't go through is tried again, and one that found an order already executed is
            // reconciliation's to record.
            if (legs.Any(leg => leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Finished)
                || legs.Any(leg => leg.IsUnsettled))
            {
                return;
            }

            protection.Status = ProtectionStatus.Pending;
            protection.ProtectBy = now + settings.ProtectiveStop;
            protection.UpdatedAt = now;
            logger.LogInformation(
                "Protection {Sequence} of entry {ClientOrderId} takes a stop at {Stop} and a target at {Target}, so its orders are replaced",
                protection.Sequence,
                entry.ClientOrderId,
                protection.StopPrice,
                protection.TargetPrice);
            await store.SaveAsync(protection);
            return;
        }

        var stops = orders.LegsOf(protection)
            .Where(leg => leg.Leg == OrderLeg.Stop && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled)
            .ToList();
        var stale = stops.Where(leg => leg.StopPrice != protection.StopPrice).ToList();

        if (stops.Count == stale.Count)
        {
            var previous = stops[^1].StopPrice;

            switch (await PlaceStopAsync(session, orders, protection, entry, cancellationToken))
            {
                case StopOutcome.Rested:
                    logger.LogInformation(
                        "Protection {Sequence} of entry {ClientOrderId} moved its stop from {From} to {To}",
                        protection.Sequence,
                        entry.ClientOrderId,
                        previous,
                        protection.StopPrice);
                    await CancelAsync(session, stale, cancellationToken);
                    await NotifyAsync(session, protection, NotificationType.StopMoved, cancellationToken);
                    break;
                case StopOutcome.WouldTrigger:
                    await BeginCloseAsync(protection, entry, "the price has already passed its new stop", cancellationToken);
                    return;
                case StopOutcome.Exhausted:
                    logger.LogError(
                        "Binance rejected every attempt to move the stop of protection {Sequence} of entry {ClientOrderId} to {To}, so it stays at {From}",
                        protection.Sequence,
                        entry.ClientOrderId,
                        protection.StopPrice,
                        previous);
                    protection.StopPrice = previous!.Value;
                    protection.UpdatedAt = now;
                    await store.SaveAsync(protection);
                    break;
            }
        }
        else if (stale.Count > 0)
        {
            await CancelAsync(session, stale, cancellationToken);
        }

        var target = protection.TargetDropped ? null : protection.TargetPrice;
        var staleTargets = orders.LegsOf(protection)
            .Where(leg => leg.Leg == OrderLeg.Target && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled && leg.StopPrice != target)
            .ToList();

        await CancelAsync(session, staleTargets, cancellationToken);
    }

    /// <summary>
    /// Places a stop for the protection, or on Spot with a target an OCO list (FR-EXE-02, FR-EXE-03), at the
    /// protection's prices. An attempt that Binance never received keeps its client order ID. Any other gets a new one,
    /// so no ID is sent twice.
    /// </summary>
    private async Task<StopOutcome> PlaceStopAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var latest = Latest(orders, protection, OrderLeg.Stop);

        if (latest is { Status: OrderStatus.Rejected } && latest.StopPrice == protection.StopPrice && now - latest.UpdatedAt < RetryPause)
        {
            return StopOutcome.Waiting;
        }

        var target = protection.TargetDropped ? null : protection.TargetPrice;
        var oco = !session.Futures && target is not null;
        var side = Closing(entry.Side);
        List<OrderRow> rows;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null } && (latest.ListClientOrderId is not null) == oco
            && SamePrices(orders, protection, latest, target))
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
            var stopId = ClientOrderIds.Stop(entry.AgentId, protection.IdCandleClose, protection.IdIndex, attempt);
            var listId = oco ? ClientOrderIds.List(entry.AgentId, protection.IdCandleClose, protection.IdIndex, attempt) : null;

            rows = [Leg(entry, protection, OrderLeg.Stop, OrderKind.StopMarket, stopId, side, stopPrice: protection.StopPrice, listId: listId, now: now)];

            if (oco)
            {
                var targetId = ClientOrderIds.Target(entry.AgentId, protection.IdCandleClose, protection.IdIndex, attempt);
                rows.Add(Leg(entry, protection, OrderLeg.Target, OrderKind.LimitMaker, targetId, side, price: target, listId: listId, now: now));
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
                        targetRow.Price!.Value,
                        stopRow.StopPrice!.Value,
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
                        stopPrice: stopRow.StopPrice,
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

    /// <summary>Whether an attempt Binance never received still has the protection's prices, so it can be sent again as it is.</summary>
    private static bool SamePrices(AgentOrders orders, ProtectionRow protection, OrderRow stop, decimal? target) =>
        stop.StopPrice == protection.StopPrice
        && (stop.ListClientOrderId is not { } listId
            || orders.LegsOf(protection).Any(leg => leg.ListClientOrderId == listId && leg.Leg == OrderLeg.Target && leg.Price == target));

    /// <summary>A Futures target, reduce-only on the mark price like the stop (FR-EXE-03). One Binance refuses is dropped.</summary>
    private async Task PlaceTargetAsync(
        AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var price = protection.TargetPrice!.Value;
        var latest = orders.LegsOf(protection).Where(leg => leg.Leg == OrderLeg.Target && leg.StopPrice == price).OrderBy(leg => leg.CreatedAt).LastOrDefault();
        OrderRow row;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null })
        {
            row = latest;
            Resend(row, now);
            await store.SaveAsync(row);
        }
        else if (latest is not null)
        {
            // Rejected, shown never to have been placed, or cancelled outside Ecnanib: it isn't tried again.
            protection.TargetDropped = true;
            protection.UpdatedAt = now;
            logger.LogInformation(
                "The target of protection {Sequence} of entry {ClientOrderId} is {Status} on Binance, so only its stop rests",
                protection.Sequence,
                entry.ClientOrderId,
                latest.Status);
            await store.SaveAsync(protection);
            return;
        }
        else
        {
            var attempt = orders.LegsOf(protection).Count(leg => leg.Leg == OrderLeg.Target && leg.CandleClose == protection.IdCandleClose);

            if (attempt > ClientOrderIds.MaximumIndex)
            {
                protection.TargetDropped = true;
                protection.UpdatedAt = now;
                await store.SaveAsync(protection);
                return;
            }

            var id = ClientOrderIds.Target(entry.AgentId, protection.IdCandleClose, protection.IdIndex, attempt);
            row = Leg(entry, protection, OrderLeg.Target, OrderKind.TakeProfitMarket, id, Closing(entry.Side), stopPrice: price, now: now);
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
                    stopPrice: row.StopPrice,
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

    /// <summary>
    /// The protection's stop rests. The first part of an entry to rest opens its trade. A part whose stop rested before
    /// has had it replaced, which the user hears of when it was cancelled outside Ecnanib or now rests at another price.
    /// </summary>
    private async Task RestAsync(AgentSession session, AgentOrders orders, ProtectionRow protection, CancellationToken cancellationToken)
    {
        var entry = orders.EntryOf(protection);
        var stops = orders.LegsOf(protection)
            .Where(leg => leg.Leg == OrderLeg.Stop && leg.SentAt is not null && leg.Status is not (OrderStatus.NotPlaced or OrderStatus.Rejected))
            .OrderBy(leg => leg.CreatedAt)
            .ToList();
        var moved = protection.StopLostAt is not null || (stops.Count > 1 && stops[^2].StopPrice != stops[^1].StopPrice);

        Notice? notice = null;

        if (!protection.Rested && !orders.ProtectionsOf(entry).Any(other => other.Rested))
        {
            notice = await NoticeAsync(session, NotificationType.TradeOpened, cancellationToken);
        }
        else if (protection.Rested && moved && session.FirstNotice(NotificationType.StopMoved))
        {
            notice = await NoticeAsync(session, NotificationType.StopMoved, cancellationToken);
        }

        protection.Status = ProtectionStatus.Resting;
        protection.Rested = true;
        protection.StopLostAt = null;
        protection.UpdatedAt = time.GetUtcNow();

        logger.LogInformation("Protection {Sequence} rests on Binance", protection.Sequence);

        await store.SaveAsync(protection, notice);
    }

    /// <summary>
    /// Marks a held part to be closed at market for the kill switch (FR-AGT-08), which the next pass does. The caller
    /// saves it.
    /// </summary>
    public void BeginKill(ProtectionRow protection, OrderRow entry)
    {
        protection.Status = ProtectionStatus.Closing;
        protection.ClosingFor = ProtectionClosing.KillSwitch;
        protection.UpdatedAt = time.GetUtcNow();

        logger.LogWarning(
            "Protection {Sequence} of entry {ClientOrderId} is closed at market, because the agent's kill switch was used. It holds {Quantity}",
            protection.Sequence,
            entry.ClientOrderId,
            protection.Quantity);
    }

    private async Task BeginCloseAsync(ProtectionRow protection, OrderRow entry, string reason, CancellationToken cancellationToken)
    {
        protection.Status = ProtectionStatus.Closing;
        protection.ClosingFor = ProtectionClosing.Unprotected;
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
    /// Closes the protection's quantity at market. An exit cites its own cycle's passing evaluation. A close because the
    /// stop couldn't be placed has a passing evaluation of its own (FR-EXE-02, AC-01). On Spot the stop is cancelled
    /// first, which frees the base a resting stop locks. On Futures the reduce-only close goes at once, and the stop is
    /// cancelled after.
    /// </summary>
    private async Task CloseAsync(AgentSession session, AgentOrders orders, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var exit = protection.ClosingFor == ProtectionClosing.Exit;
        var leg = exit ? OrderLeg.Exit : OrderLeg.EmergencyClose;
        var latest = orders.LegsOf(protection).Where(IsClose).OrderBy(row => row.CreatedAt).LastOrDefault();

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

            // A stop that still rests locks the base, so a sell now would be refused. The next pass tries again.
            if (orders.LegsOf(protection).Any(row => row.Leg is OrderLeg.Stop or OrderLeg.Target && row.Status is OrderStatus.Open or OrderStatus.PartiallyFilled))
            {
                return;
            }
        }

        decimal quantity;
        ExchangeSide side;
        bool reduceOnly;
        string? evaluationJson = null;

        if (exit)
        {
            // The exit's own evaluation sized it, and the protection holds its share.
            quantity = protection.Quantity;
            side = Closing(entry.Side);
            reduceOnly = session.Futures;
        }
        else
        {
            var evaluation = await EvaluateCloseAsync(session, protection, entry, cancellationToken);

            if (evaluation.Order is not { Quantity: > 0m } sized)
            {
                protection.Status = ProtectionStatus.Dust;
                protection.EndedAt = now;
                protection.UpdatedAt = now;
                logger.LogError(
                    "Protection {Sequence} of entry {ClientOrderId} can't be closed: the Risk Engine failed the close: {Failures}",
                    protection.Sequence,
                    entry.ClientOrderId,
                    string.Join(", ", evaluation.Failures.Select(rule => $"{rule.Rule} {rule.Reason}")));
                await store.SaveAsync(protection);
                return;
            }

            quantity = sized.Quantity.Value;
            side = sized.Side == SizedSide.Buy ? ExchangeSide.Buy : ExchangeSide.Sell;

            // The engine marks every exit reduce-only. Spot has no such thing, and the adapter refuses it.
            reduceOnly = session.Futures && sized.ReduceOnly;
            evaluationJson = ExecutionJson.Write(evaluation);
        }

        OrderRow row;

        if (latest is { Status: OrderStatus.NotPlaced, SentAt: null } && latest.Quantity == quantity && latest.Leg == leg)
        {
            row = latest;
            Resend(row, now);
            await store.SaveAsync(row);
        }
        else
        {
            var attempt = orders.LegsOf(protection).Count(row => IsClose(row) && row.CandleClose == protection.IdCandleClose);

            if (attempt > ClientOrderIds.MaximumIndex)
            {
                protection.Status = ProtectionStatus.Abandoned;
                protection.EndedAt = now;
                protection.UpdatedAt = now;
                logger.LogCritical(
                    "Binance rejected every close of protection {Sequence} of entry {ClientOrderId}. {Quantity} is still held, and the user must close it",
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
                CycleId = protection.CycleId,
                CandleClose = protection.IdCandleClose,
                Origin = exit ? OrderOrigin.Council : OrderOrigin.System,
                SystemEvaluation = evaluationJson,
                CredentialId = entry.CredentialId,
                Market = entry.Market,
                Environment = entry.Environment,
                Symbol = entry.Symbol,
                Leg = leg,
                EntryId = entry.Id,
                ProtectionId = protection.Id,
                ClientOrderId = ClientOrderIds.Close(entry.AgentId, protection.IdCandleClose, protection.IdIndex, attempt),
                Type = OrderKind.Market,
                Side = side,
                Quantity = quantity,
                ReduceOnly = reduceOnly,
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
        var now = time.GetUtcNow();
        var exit = protection.ClosingFor == ProtectionClosing.Exit;

        protection.Status = ProtectionStatus.Closed;
        protection.EndReason = protection.ClosingFor switch
        {
            ProtectionClosing.Exit => ProtectionEnd.Exited,
            ProtectionClosing.KillSwitch => ProtectionEnd.KillSwitch,
            _ => ProtectionEnd.Unprotected,
        };
        protection.EndedAt = now;
        protection.UpdatedAt = now;

        if (exit)
        {
            logger.LogInformation(
                "Protection {Sequence} of entry {ClientOrderId} was sold at market at {AveragePrice} for the exit",
                protection.Sequence,
                entry.ClientOrderId,
                close.AveragePrice);
        }
        else if (protection.ClosingFor == ProtectionClosing.KillSwitch)
        {
            logger.LogWarning(
                "Protection {Sequence} of entry {ClientOrderId} was closed at market at {AveragePrice} for the kill switch",
                protection.Sequence,
                entry.ClientOrderId,
                close.AveragePrice);
        }
        else
        {
            logger.LogWarning(
                "Protection {Sequence} of entry {ClientOrderId} was closed at market at {AveragePrice}, because its stop couldn't be placed",
                protection.Sequence,
                entry.ClientOrderId,
                close.AveragePrice);
        }

        var notice = session.FirstNotice(NotificationType.TradeClosed) ? await NoticeAsync(session, NotificationType.TradeClosed, cancellationToken) : null;
        await store.SaveAsync(protection, notice);
    }

    /// <summary>
    /// The close's own evaluation (AC-01), as an exit of <see cref="IntentOrigin.System"/> that only the structural rules
    /// can stop. On Spot the position is what the agent's fills gave it (FR-AGT-09). The price is the part's, which is
    /// all the exit rules use, for Spot's dust check, and Binance measures again anyway.
    /// </summary>
    private async Task<RiskEvaluation> EvaluateCloseAsync(
        AgentSession session, ProtectionRow protection, OrderRow entry, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(session.UserId, session.AgentId, cancellationToken);
        var price = protection.EntryPrice;
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
                SideOf(entry),
                protection.Quantity,
                price,
                Stop: protection.StopPrice,
                Target: protection.TargetPrice),
        };

        return engine.Evaluate(
            new OrderIntent { Action = session.Futures ? IntentAction.Close : IntentAction.Sell, Origin = IntentOrigin.System },
            inputs);
    }

    /// <summary>
    /// Cancels the given stops and targets, a Spot list whole. A cancel that finds nothing reads an order known to rest,
    /// which has ended meanwhile, perhaps by executing.
    /// </summary>
    private async Task CancelAsync(AgentSession session, IReadOnlyList<OrderRow> legs, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

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

    /// <summary>
    /// Stops and targets still resting for a part of a position that's gone: closed, ended or split. On Futures that's the
    /// take-profit left when the stop executed, which could otherwise close a new position. What a stopped agent left to
    /// the user stays as it is (FR-AGT-07).
    /// </summary>
    private async Task CancelOrphansAsync(AgentSession session, AgentOrders orders, CancellationToken cancellationToken)
    {
        foreach (var protection in orders.Protections
            .Where(protection => protection.Status is ProtectionStatus.Closed or ProtectionStatus.Ended or ProtectionStatus.Dust)
            .Where(protection => protection.EndReason != ProtectionEnd.LeftToUser)
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

    /// <summary>Saves the protection with a notice of this type, unless the session has raised one already.</summary>
    private async Task NotifyAsync(AgentSession session, ProtectionRow protection, NotificationType type, CancellationToken cancellationToken)
    {
        var notice = session.FirstNotice(type) ? await NoticeAsync(session, type, cancellationToken) : null;
        await store.SaveAsync(protection, notice);
    }

    public async Task<Notice?> NoticeAsync(AgentSession session, NotificationType type, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(session.UserId, session.AgentId, cancellationToken);

        return agent is null ? null : new Notice(type, session.UserId, $"/agents/{session.AgentId}/settings", agent.Settings.Name, agent.IsDemo);
    }

    /// <summary>
    /// Passes over the agent's orders until nothing is urgent or <paramref name="maximumWait"/> is up: every order's
    /// outcome is known, and every fill has a resting stop or has been closed. Whatever remains is the watch's.
    /// </summary>
    public async Task<KeeperPass> KeepAsync(AgentSession session, TimeSpan maximumWait, TimeSpan between, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + maximumWait;
        var pass = await AdvanceAsync(session, cancellationToken);

        while (pass.Urgent && time.GetUtcNow() < until)
        {
            await Task.Delay(between, time, cancellationToken);
            pass = await AdvanceAsync(session, cancellationToken);
        }

        return pass;
    }

    private static KeeperPass Summarize(AgentSession session, AgentOrders orders)
    {
        var urgent = orders.Protections.Any(protection => protection.Status is ProtectionStatus.Pending or ProtectionStatus.Closing)
            || orders.Entries.Concat(orders.Legs).Any(row => row.IsUnsettled)
            || orders.Entries.Any(entry => entry.Type == OrderKind.Market && entry.IsLive)
            || orders.Protections.Any(protection => protection.Status == ProtectionStatus.Resting && !Converged(session, orders, protection));

        var hasWork = urgent
            || orders.Entries.Any(entry => !entry.Resolved)
            || orders.Protections.Any(protection => protection.Status == ProtectionStatus.Resting && NeedsTarget(session, orders, protection))
            || orders.Protections.Any(protection => protection.Status is ProtectionStatus.Closed or ProtectionStatus.Ended or ProtectionStatus.Dust
                && protection.EndReason != ProtectionEnd.LeftToUser
                && orders.LegsOf(protection).Any(leg => leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled));

        return new KeeperPass(orders, hasWork, urgent);
    }

    private static bool IsClose(OrderRow row) => row.Leg is OrderLeg.EmergencyClose or OrderLeg.Exit;

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
        CycleId = protection.CycleId,
        CandleClose = protection.IdCandleClose,
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

        // A Futures conditional order's: the order it became, whose fills the trade journal reads. An open order list
        // reports none, so a known one is kept.
        if (order.TriggeredOrderId is > 0)
        {
            row.TriggeredOrderId ??= order.TriggeredOrderId;
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

    /// <summary>The side of the position an entry opens or adds to.</summary>
    public static PositionSide SideOf(OrderRow entry) => entry.Side == ExchangeSide.Buy ? PositionSide.Long : PositionSide.Short;

    /// <summary>Whether <paramref name="proposed"/> is strictly closer to the price than <paramref name="current"/>, for a position on this side.</summary>
    public static bool Tightens(PositionSide side, decimal current, decimal proposed) =>
        side == PositionSide.Long ? proposed > current : proposed < current;

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

    public static ExchangeSide Closing(ExchangeSide entrySide) => entrySide == ExchangeSide.Buy ? ExchangeSide.Sell : ExchangeSide.Buy;

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
/// <param name="Urgent">A fill waits for its stop, a close is under way, a stop is being moved, or an order's outcome isn't known.</param>
internal sealed record KeeperPass(AgentOrders Orders, bool HasWork, bool Urgent);
