using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Brings an agent's records in line with Binance (FR-EXE-06): order statuses and fills, stops and targets that
/// executed, and changes made outside Ecnanib, such as the user closing the position or cancelling a stop on Binance.
/// One pass reads the symbol's open orders once, queries only the agent's orders that have left them, and on Futures
/// reads the position. Its caller holds the agent's lock, so it never acts at the same time as the executor or the
/// watch. Exchange failures leave the records as they were, and say so. See Reconciliation in docs/architecture.md.
/// </summary>
internal sealed class Reconciler(
    OrderStore store,
    ProtectionKeeper keeper,
    IExchangeAccount accounts,
    ExecutionSettings settings,
    TimeProvider time,
    ILogger<Reconciler> logger)
{
    /// <summary>
    /// One pass, after the keeper's own. For an agent the kill switch stopped, each part still held is then closed at
    /// market, once Binance has confirmed that its stop hasn't executed (FR-AGT-08). That's what finishes a kill switch
    /// that was cut off, at the next sweep or at startup.
    /// </summary>
    /// <param name="readPosition">Futures: read Binance's position even when the agent holds nothing, as an entry needs.</param>
    public async Task<Reconciliation> ReconcileAsync(AgentSession session, bool readPosition, CancellationToken cancellationToken)
    {
        // The kill switch's reads lead to closes, which are protective actions (FR-EXE-09).
        var priority = session.Liquidating ? RequestPriority.Protective : RequestPriority.Normal;
        var pass = await keeper.AdvanceAsync(session, cancellationToken);
        var orders = pass.Orders;
        var verified = await ReadLegsAsync(session, orders, priority, cancellationToken);

        var changed = new List<ProtectionRow>();
        var added = new List<ProtectionRow>();
        var lost = new List<ProtectionRow>();
        var executing = false;

        if (verified)
        {
            executing = Classify(orders, changed, lost);
        }

        var held = orders.Protections.Where(protection => OrderStore.HeldProtections.Contains(protection.Status)).ToList();
        IReadOnlyList<FuturesPosition>? positions = null;
        decimal? onBinance = null;

        if (verified && session.Futures && (held.Count > 0 || readPosition))
        {
            try
            {
                positions = await accounts.GetPositionAsync(session.Key, session.Symbol.Symbol, priority, cancellationToken);
            }
            catch (ExchangeException exception)
            {
                logger.LogWarning("Binance couldn't give the {Symbol} position, so it isn't reconciled: {Failure}", session.Symbol.Symbol, exception.Failure);
                verified = false;
            }

            if (positions is not null && held.Count > 0)
            {
                var side = ProtectionKeeper.SideOf(orders.EntryOf(held[0]));
                onBinance = positions.Where(position => position.Amount > 0m == (side == PositionSide.Long)).Sum(position => Math.Abs(position.Amount));
            }
        }
        else if (verified && !session.Futures && lost.Count > 0)
        {
            try
            {
                var account = await accounts.GetAccountAsync(Market.Spot, session.Key, priority, cancellationToken);
                onBinance = account.Balances.FirstOrDefault(balance => balance.Asset == session.Symbol.BaseAsset)?.Total ?? 0m;
            }
            catch (ExchangeException exception)
            {
                // Without the balance there's no telling whether the user sold it, so the stop waits for the next pass.
                logger.LogWarning("Couldn't read the Spot account, so a stop cancelled outside Ecnanib waits for the next pass: {Failure}", exception.Failure);
                lost.Clear();
            }
        }

        // A stop that's executing, or a close under way, moves what Binance holds before the records see it.
        var settling = executing
            || held.Any(protection => protection.Status == ProtectionStatus.Closing)
            || orders.Legs.Any(leg => leg.Leg is OrderLeg.EmergencyClose or OrderLeg.Exit && (leg.IsLive || leg.IsUnsettled));

        if (onBinance is { } amount && !settling)
        {
            Shortfall(session, orders, held, lost, amount, changed, added);
        }

        if (lost.Count > 0)
        {
            Restore(session, orders, lost, changed);
        }

        if (verified && session.Liquidating)
        {
            Liquidate(orders, changed);
        }

        if (changed.Count == 0 && added.Count == 0)
        {
            return Result(orders, verified, pass.HasWork, positions);
        }

        var notice = changed.Concat(added).Any(protection => protection.Status == ProtectionStatus.Ended && protection.EndReason != ProtectionEnd.Split)
            && session.FirstNotice(NotificationType.TradeClosed)
                ? await keeper.NoticeAsync(session, NotificationType.TradeClosed, cancellationToken)
                : null;

        await store.ReplaceAsync(changed.Distinct(), added, notice);

        // Places the stops that were lost, and cancels what's left of the parts that ended, such as a take-profit.
        pass = await keeper.AdvanceAsync(session, cancellationToken);

        return Result(pass.Orders, verified, pass.HasWork, positions);
    }

    /// <summary>
    /// Reads the symbol's open orders once, and each of the agent's stops and targets that has left them, for how it
    /// ended. False when Binance couldn't say.
    /// </summary>
    private async Task<bool> ReadLegsAsync(AgentSession session, AgentOrders orders, RequestPriority priority, CancellationToken cancellationToken)
    {
        var resting = orders.Legs
            .Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target && leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Triggered)
            .ToList();

        if (resting.Count == 0)
        {
            return true;
        }

        var now = time.GetUtcNow();
        IReadOnlyList<ExchangeOrder> open;

        try
        {
            open = await accounts.GetOpenOrdersAsync(session.Market, session.Key, session.Symbol.Symbol, priority, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Binance couldn't list the open {Symbol} orders, so they aren't reconciled: {Failure}", session.Symbol.Symbol, exception.Failure);
            return false;
        }

        var byId = open.GroupBy(order => order.ClientOrderId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var verified = true;

        foreach (var leg in resting)
        {
            if (byId.TryGetValue(leg.ClientOrderId, out var order))
            {
                ProtectionKeeper.Apply(leg, order, now);
                continue;
            }

            try
            {
                if (await accounts.GetOrderAsync(session.Market, session.Key, ProtectionKeeper.Reference(leg), priority, cancellationToken) is { } read)
                {
                    ProtectionKeeper.Apply(leg, read, now);
                }
                else
                {
                    // Binance no longer has it, which it does with orders that ended unfilled long ago.
                    leg.Status = OrderStatus.Canceled;
                    leg.UpdatedAt = now;
                    leg.FinishedAt ??= now;
                }
            }
            catch (ExchangeException exception)
            {
                logger.LogInformation("Couldn't read order {ClientOrderId}, so it's read again next pass: {Failure}", leg.ClientOrderId, exception.Failure);
                verified = false;
            }
        }

        await store.SaveAsync(resting, []);

        return verified;
    }

    /// <summary>
    /// Ends each resting protection whose stop or target executed, and collects those whose stop is gone without
    /// executing: cancelled or expired outside Ecnanib. True when a stop or target is executing, so what Binance holds
    /// is about to change.
    /// </summary>
    private bool Classify(AgentOrders orders, List<ProtectionRow> changed, List<ProtectionRow> lost)
    {
        var executing = false;

        foreach (var protection in orders.Protections.Where(protection => protection.Status == ProtectionStatus.Resting))
        {
            var legs = orders.LegsOf(protection)
                .Where(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target && leg.SentAt is not null && leg.Status is not (OrderStatus.NotPlaced or OrderStatus.Rejected))
                .ToList();

            if (legs.Any(leg => leg.IsUnsettled))
            {
                continue;
            }

            if (legs.Any(leg => leg.Leg == OrderLeg.Target && leg.Status is OrderStatus.Filled or OrderStatus.Finished))
            {
                End(orders, protection, ProtectionEnd.TargetFilled, changed);
            }
            else if (legs.Any(leg => leg.Leg == OrderLeg.Stop && leg.Status is OrderStatus.Filled or OrderStatus.Finished))
            {
                End(orders, protection, ProtectionEnd.StopFilled, changed);
            }
            else if (legs.Any(leg => leg.Status is OrderStatus.Triggered or OrderStatus.PartiallyFilled))
            {
                executing = true;
            }
            else if (!legs.Any(leg => leg.Leg == OrderLeg.Stop && leg.Status == OrderStatus.Open))
            {
                lost.Add(protection);
            }
        }

        return executing;
    }

    /// <summary>
    /// Binance holds less than the agent's records say, so the difference was closed outside Ecnanib (FR-EXE-06). Parts
    /// whose stop was lost go first, because their base or position was the one left unguarded, then the newest. A part
    /// only partly gone is split: what's gone ends, and the rest gets a stop of its own.
    /// </summary>
    private void Shortfall(
        AgentSession session,
        AgentOrders orders,
        List<ProtectionRow> held,
        List<ProtectionRow> lost,
        decimal onBinance,
        List<ProtectionRow> changed,
        List<ProtectionRow> added)
    {
        var excess = held.Sum(protection => protection.Quantity) - onBinance;

        if (excess <= 0m)
        {
            return;
        }

        logger.LogWarning(
            "Binance holds {OnBinance} of {Symbol}, {Excess} less than the agent's records, so that much was closed outside Ecnanib",
            onBinance,
            session.Symbol.Symbol,
            excess);

        var now = time.GetUtcNow();
        var lot = Lots.Of(session.Symbol);
        var order = held
            .OrderByDescending(protection => lost.Contains(protection))
            .ThenByDescending(protection => protection.OpenedAt)
            .ThenByDescending(protection => protection.CreatedAt)
            .ToList();

        foreach (var protection in order)
        {
            if (excess <= 0m)
            {
                break;
            }

            lost.Remove(protection);

            if (protection.Quantity <= excess)
            {
                End(orders, protection, ProtectionEnd.ClosedOutside, changed);
                excess -= protection.Quantity;
                continue;
            }

            var keep = Lots.Down(protection.Quantity - excess, lot);
            var entry = orders.EntryOf(protection);
            var sequence = orders.ProtectionsOf(entry).Count();

            if (Lots.Placeable(keep, protection.StopPrice, session) && sequence + 1 <= ClientOrderIds.MaximumIndex)
            {
                End(orders, protection, ProtectionEnd.Split, changed);

                var gone = ProtectionParts.Of(protection, protection.Quantity - keep, sequence, protection.CycleId, entry.CandleClose, sequence, now);
                gone.Status = ProtectionStatus.Ended;
                gone.EndReason = ProtectionEnd.ClosedOutside;
                gone.EndedAt = now;

                var rest = ProtectionParts.Of(protection, keep, sequence + 1, protection.CycleId, entry.CandleClose, sequence + 1, now);
                rest.Status = ProtectionStatus.Pending;
                rest.ProtectBy = now + settings.ProtectiveStop;

                added.Add(gone);
                added.Add(rest);
                orders.Protections.Add(gone);
                orders.Protections.Add(rest);
            }
            else
            {
                // What's left is too small for a stop of its own, and so the user's to deal with.
                End(orders, protection, ProtectionEnd.ClosedOutside, changed);
            }

            excess = 0m;
        }
    }

    /// <summary>
    /// A stop cancelled outside Ecnanib, whose part is still held, is placed again within the protection window, or the
    /// part is closed at market, as for a fresh fill (FR-EXE-02, NFR-REL-02). An agent the user stopped leaves its
    /// orders as they are (FR-AGT-07), so its part becomes the user's. One the kill switch stopped closes it instead,
    /// which <see cref="Liquidate"/> does.
    /// </summary>
    private void Restore(AgentSession session, AgentOrders orders, List<ProtectionRow> lost, List<ProtectionRow> changed)
    {
        var now = time.GetUtcNow();

        foreach (var protection in lost.Where(protection => protection.Status == ProtectionStatus.Resting))
        {
            if (session.Liquidating)
            {
                continue;
            }

            if (session.LeftToUser)
            {
                logger.LogWarning(
                    "The stop of protection {Sequence} of entry {ClientOrderId} was cancelled outside Ecnanib. The agent is stopped, so {Quantity} is left to the user",
                    protection.Sequence,
                    orders.EntryOf(protection).ClientOrderId,
                    protection.Quantity);
                End(orders, protection, ProtectionEnd.LeftToUser, changed);
                continue;
            }

            logger.LogWarning(
                "The stop of protection {Sequence} of entry {ClientOrderId} was cancelled outside Ecnanib, so it's placed again by {ProtectBy:O}",
                protection.Sequence,
                orders.EntryOf(protection).ClientOrderId,
                now + settings.ProtectiveStop);

            protection.Status = ProtectionStatus.Pending;
            protection.ProtectBy = now + settings.ProtectiveStop;
            protection.StopLostAt = now;
            protection.UpdatedAt = now;
            changed.Add(protection);
        }
    }

    /// <summary>
    /// The kill switch (FR-AGT-08): every part that's still resting is closed at market, now that Binance has confirmed
    /// its stop and target are where the records say. One whose stop or target is executing is left to finish, and
    /// what's left of it is closed at the next pass. Parts waiting for a stop are the keeper's to close.
    /// </summary>
    private void Liquidate(AgentOrders orders, List<ProtectionRow> changed)
    {
        foreach (var protection in orders.Protections.Where(protection => protection.Status == ProtectionStatus.Resting).ToList())
        {
            var executing = orders.LegsOf(protection)
                .Any(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target && (leg.IsUnsettled || leg.Status is OrderStatus.Triggered or OrderStatus.PartiallyFilled));

            if (executing)
            {
                continue;
            }

            keeper.BeginKill(protection, orders.EntryOf(protection));
            changed.Add(protection);
        }
    }

    private void End(AgentOrders orders, ProtectionRow protection, ProtectionEnd reason, List<ProtectionRow> changed)
    {
        var now = time.GetUtcNow();

        protection.Status = ProtectionStatus.Ended;
        protection.EndReason = reason;
        protection.EndedAt = now;
        protection.UpdatedAt = now;
        changed.Add(protection);

        logger.LogInformation(
            "Protection {Sequence} of entry {ClientOrderId} has ended: {EndReason}", protection.Sequence, orders.EntryOf(protection).ClientOrderId, reason);
    }

    private static Reconciliation Result(AgentOrders orders, bool verified, bool hasWork, IReadOnlyList<FuturesPosition>? positions) =>
        new(orders, verified, hasWork)
        {
            Positions = positions,
        };
}

/// <summary>What a pass found.</summary>
/// <param name="Verified">Binance answered every read, so the records match it.</param>
/// <param name="HasWork">Something still needs the watch.</param>
internal sealed record Reconciliation(AgentOrders Orders, bool Verified, bool HasWork)
{
    /// <summary>Futures: Binance's positions in the symbol, when they were read.</summary>
    public IReadOnlyList<FuturesPosition>? Positions { get; init; }

    /// <summary>Futures: the signed amount Binance holds in the symbol, positive for a long, or null when it wasn't read.</summary>
    public decimal? Amount => Positions?.Sum(position => position.Amount);
}

/// <summary>
/// Parts of a protection, when an exit or a change outside Ecnanib takes only some of it: each holds the parent's prices,
/// cost and opening time, under the parent's entry with a sequence of its own.
/// </summary>
internal static class ProtectionParts
{
    /// <param name="cycleId">The cycle its orders cite: the parent's, or the exit's that split it.</param>
    /// <param name="idCandleClose">
    /// What its client order IDs derive from, with <paramref name="idIndex"/>: the exit's candle and the part's place
    /// among the exit's, or outside a cycle, its entry's candle and its own sequence, which no other part has.
    /// </param>
    public static ProtectionRow Of(
        ProtectionRow parent, decimal quantity, int sequence, Guid cycleId, DateTimeOffset idCandleClose, int idIndex, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = parent.UserId,
        AgentId = parent.AgentId,
        EntryId = parent.EntryId,
        Sequence = sequence,
        Quantity = quantity,
        ProtectBy = now,
        TargetDropped = parent.TargetDropped,
        StopPrice = parent.StopPrice,
        TargetPrice = parent.TargetPrice,
        EntryPrice = parent.EntryPrice,
        OpenedAt = parent.OpenedAt,
        CycleId = cycleId,
        IdCandleClose = idCandleClose,
        IdIndex = idIndex,
        Rested = parent.Rested,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
