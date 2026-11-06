using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using ExchangeSide = Ecnanib.Modules.ExchangeAdapter.Contracts.OrderSide;
using SizedSide = Ecnanib.Modules.RiskEngine.Contracts.OrderSide;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Sends a <see cref="SizedOrder"/> once, and sees what it leads to settled before it returns (FR-EXE-01, FR-EXE-02,
/// FR-EXE-05): an entry or an add with what fills protected, an exit sold at market, a stop or target moved, or an
/// unfilled entry cancelled. Each reconciles the agent's orders with Binance first (FR-EXE-06). It holds the agent's
/// lock throughout, so the agent's watch and reconciliation can't act at the same time. Whatever is left, such as a
/// resting limit order, goes to the watch. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderExecutor(
    AgentLocks locks,
    AgentSessions sessions,
    OrderStore store,
    ProtectionKeeper keeper,
    Reconciler reconciler,
    OrderWatch watch,
    IExchangeTrading trading,
    IExchangeSymbols symbols,
    Contracts.IFuturesSetup futures,
    ExecutionSettings settings,
    TimeProvider time,
    ILogger<OrderExecutor> logger) : IOrderExecution
{
    /// <summary>Between passes while a fill waits for its stop, or an order's outcome is being settled.</summary>
    public static readonly TimeSpan UrgentPassDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The longest the cycle waits for an entry to settle and be protected. An entry with an unknown outcome settles
    /// within the adapter's trade timeout and its receive window, and a fill's stop rests or it's closed within
    /// <see cref="ExecutionSettings.ProtectiveStop"/>. Anything still under way after this is left to the watch.
    /// </summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(45);

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Order.Market != request.Market)
        {
            throw new ArgumentException("The order was sized for another market.", nameof(request));
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.AgentId] = request.AgentId,
            [LogProperties.CycleId] = request.CycleId,
        });

        return request.Order.Kind switch
        {
            SizedOrderKind.Entry => await EntryAsync(request, cancellationToken),
            SizedOrderKind.CancelEntry => await CancelAsync(request, cancellationToken),
            SizedOrderKind.Exit => await ExitAsync(request, cancellationToken),
            SizedOrderKind.StopAdjustment or SizedOrderKind.TargetAdjustment => await AdjustAsync(request, cancellationToken),
            _ => throw new ArgumentException("The order is of a kind this version doesn't execute.", nameof(request)),
        };
    }

    private async Task<ExecutionResult> EntryAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var order = request.Order;

        if (order is not { Quantity: > 0m, Side: { } side, StopPrice: { } stop, EntryType: { } entryType })
        {
            throw new ArgumentException("An entry needs a quantity, a side, an entry type and a protective stop.", nameof(request));
        }

        if (!AllowsEntry(request))
        {
            return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.PastDeadline);
        }

        using var held = await locks.HoldAsync(request.AgentId, cancellationToken);

        AgentSession session;

        try
        {
            session = await sessions.OpenAsync(
                request.UserId, request.AgentId, request.CredentialId, request.Market, request.Environment, request.Symbol, cancellationToken);
        }
        catch (AgentSessionException exception)
        {
            logger.LogWarning("The entry wasn't sent: {Failure}", exception.Failure);

            return new ExecutionResult(
                ExecutionStatus.NotSent, exception.Failure == AgentSessionFailure.KeyUnusable ? ExecutionReason.KeyUnusable : ExecutionReason.ExchangeUnavailable);
        }

        using (session)
        {
            if (!SupportsProtection(session.Symbol, request.Market, order.TargetPrice is not null))
            {
                logger.LogWarning("Binance doesn't take the order types {Symbol} needs for a stop, so the entry isn't sent", request.Symbol);
                return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.OrderTypeUnsupported);
            }

            // The Risk Engine sized it against the position the cycle saw. Since then a stop may have executed, which
            // only makes it smaller than it could be, or the user may have opened a position of their own in the pair.
            var reconciled = await reconciler.ReconcileAsync(session, readPosition: true, cancellationToken);

            if (!reconciled.Verified)
            {
                logger.LogWarning("Binance couldn't confirm the agent's orders and position, so no entry is sent");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable));
            }

            if (request.Market == Market.Futures && reconciled.Amount != SignedHeld(reconciled.Orders))
            {
                logger.LogWarning(
                    "Binance holds {Amount} of {Symbol}, which isn't only the agent's, so no entry is sent", reconciled.Amount, request.Symbol);
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ForeignPosition));
            }

            if (request.Market == Market.Futures)
            {
                var setup = await futures.PrepareEntryAsync(request.UserId, request.CredentialId, request.Symbol, order, cancellationToken);

                if (setup != Contracts.FuturesSetupOutcome.Ready)
                {
                    return Finish(
                        session,
                        new ExecutionResult(
                            ExecutionStatus.NotSent,
                            setup == Contracts.FuturesSetupOutcome.KeyUnusable ? ExecutionReason.KeyUnusable : ExecutionReason.FuturesSetup)
                        {
                            FuturesSetup = setup,
                        });
                }
            }

            // FR-CYC-06, right before sending: the checks above take time.
            if (!AllowsEntry(request))
            {
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.PastDeadline));
            }

            var entry = NewEntry(request, side, entryType, stop);
            await store.InsertAsync([entry], cancellationToken);

            await SendAsync(session, entry, cancellationToken);

            if (entry.Status == OrderStatus.NotPlaced)
            {
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable) { ExchangeCode = entry.ErrorCode });
            }

            if (entry.Status == OrderStatus.Rejected)
            {
                return Finish(session, new ExecutionResult(ExecutionStatus.Rejected) { ExchangeCode = entry.ErrorCode, ClientOrderIds = [entry.ClientOrderId] });
            }

            var pass = await KeepAsync(session, cancellationToken);

            return Finish(session, Result(pass.Orders, entry.Id), pass);
        }
    }

    /// <summary>
    /// CANCEL_PENDING: cancels the agent's unfilled entry, never a stop or target, and protects whatever had filled.
    /// </summary>
    private async Task<ExecutionResult> CancelAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        using var held = await locks.HoldAsync(request.AgentId, cancellationToken);

        AgentSession session;

        try
        {
            session = await sessions.OpenAsync(
                request.UserId, request.AgentId, request.CredentialId, request.Market, request.Environment, request.Symbol, cancellationToken);
        }
        catch (AgentSessionException exception)
        {
            return new ExecutionResult(
                ExecutionStatus.NotSent, exception.Failure == AgentSessionFailure.KeyUnusable ? ExecutionReason.KeyUnusable : ExecutionReason.ExchangeUnavailable);
        }

        using (session)
        {
            var orders = await store.LoadAsync(request.UserId, request.AgentId, cancellationToken);
            var entry = orders.Entries.LastOrDefault(row => OrderStore.PendingEntryStatuses.Contains(row.Status));

            if (entry is null)
            {
                return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.NothingToCancel);
            }

            await keeper.CancelEntryAsync(session, entry, cancellationToken);
            var pass = await KeepAsync(session, cancellationToken);
            var cancelled = pass.Orders.Entries.First(row => row.Id == entry.Id);

            var result = Result(pass.Orders, entry.Id);

            return Finish(session, cancelled switch
            {
                // Binance couldn't be reached, or the entry's outcome is still unknown. The watch tries again.
                { IsLive: true } => new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable),

                // Shown never to have been placed, so there was nothing to cancel.
                { Status: OrderStatus.NotPlaced } => new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.NothingToCancel),

                // It filled in full before the cancel arrived: its protection is what happened.
                { Status: OrderStatus.Filled } => result,

                // What had filled is protected, unless its stop couldn't be placed.
                _ => result.Status is ExecutionStatus.Unprotected or ExecutionStatus.Closed ? result : result with { Status = ExecutionStatus.Cancelled },
            }, pass);
        }
    }

    /// <summary>
    /// SELL or CLOSE: sells what the exit was sized for, or what's still held if a stop has taken some meanwhile, at
    /// market, under the exit's evaluation (AC-01). It takes the position's parts oldest first. A part it takes only
    /// some of is split, and the rest gets a stop of its own, as a fill does (FR-EXE-02). On Spot the split part's stop
    /// is cancelled first, which frees the base it locks. A part's close goes as an emergency close does: Spot cancels
    /// its stop first, and Futures sends the reduce-only close and then cancels the stop.
    /// </summary>
    private async Task<ExecutionResult> ExitAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var order = request.Order;

        if (order is not { Quantity: > 0m, Side: { } side })
        {
            throw new ArgumentException("An exit needs a quantity and a side.", nameof(request));
        }

        using var held = await locks.HoldAsync(request.AgentId, cancellationToken);

        if (await OpenAsync(request, cancellationToken) is not { } session)
        {
            return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.KeyUnusable);
        }

        using (session)
        {
            var reconciled = await reconciler.ReconcileAsync(session, readPosition: false, cancellationToken);

            if (!reconciled.Verified)
            {
                logger.LogWarning("Binance couldn't confirm the agent's orders, so the exit isn't sent");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable));
            }

            var orders = reconciled.Orders;
            var parts = Parts(orders);
            var closing = side == SizedSide.Buy ? ExchangeSide.Buy : ExchangeSide.Sell;

            if (parts.Count == 0 || ProtectionKeeper.Closing(orders.EntryOf(parts[0]).Side) != closing)
            {
                logger.LogInformation("The agent no longer holds the position, so there's nothing to exit");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.NoPosition));
            }

            var now = time.GetUtcNow();
            var lot = Lots.Of(session.Symbol);
            var remaining = Math.Min(order.Quantity.Value, parts.Sum(part => part.Quantity));
            var index = 0;
            var changed = new List<ProtectionRow>();
            var added = new List<ProtectionRow>();
            var taken = new List<Guid>();

            foreach (var part in parts)
            {
                if (remaining <= 0m || index > ClientOrderIds.MaximumIndex)
                {
                    break;
                }

                if (part.Quantity <= remaining)
                {
                    Rebase(part, request, index++, now);
                    part.Status = ProtectionStatus.Closing;
                    part.ClosingFor = ProtectionClosing.Exit;
                    changed.Add(part);
                    taken.Add(part.Id);
                    remaining -= part.Quantity;
                    continue;
                }

                var sell = Lots.Down(remaining, lot);
                var keep = part.Quantity - sell;

                if (sell <= 0m || index + 1 > ClientOrderIds.MaximumIndex)
                {
                    break;
                }

                if (!session.Futures)
                {
                    await keeper.CancelLegsAsync(session, orders, part, includeUnsettled: true, cancellationToken);

                    if (orders.LegsOf(part).Any(leg => leg.Leg is OrderLeg.Stop or OrderLeg.Target
                        && (leg.IsUnsettled || leg.Status is OrderStatus.Open or OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Finished)))
                    {
                        // Its stop couldn't be cancelled, or had just executed, so the part can't be split now.
                        logger.LogWarning("The stop of the part the exit splits couldn't be cancelled, so the exit isn't sent");
                        await StoreAsync(changed, added);
                        return Finish(session, ExitResult(orders, taken, request), null);
                    }
                }

                var sequence = orders.ProtectionsOf(orders.EntryOf(part)).Count();

                part.Status = ProtectionStatus.Ended;
                part.EndReason = ProtectionEnd.Split;
                part.EndedAt = now;
                part.UpdatedAt = now;
                changed.Add(part);

                var sold = ProtectionParts.Of(part, sell, sequence, request.CycleId, request.CandleClose, index++, now);
                sold.Status = ProtectionStatus.Closing;
                sold.ClosingFor = ProtectionClosing.Exit;
                added.Add(sold);
                taken.Add(sold.Id);

                var rest = ProtectionParts.Of(part, keep, sequence + 1, request.CycleId, request.CandleClose, index++, now);

                if (Lots.Placeable(keep, rest.StopPrice, session) && Lots.Down(keep, lot) == keep)
                {
                    rest.Status = ProtectionStatus.Pending;
                    rest.ProtectBy = now + settings.ProtectiveStop;
                }
                else
                {
                    rest.Status = ProtectionStatus.Dust;
                    rest.EndedAt = now;
                    logger.LogError(
                        "The exit leaves {Quantity} of a part, which is too small for Binance to take a stop or a close for", keep);
                }

                added.Add(rest);
                remaining = 0m;
            }

            await StoreAsync(changed, added);

            logger.LogInformation("The exit closes {Count} parts of the position", taken.Count);

            var pass = await KeepAsync(session, cancellationToken);

            return Finish(session, ExitResult(pass.Orders, taken, request), pass);
        }
    }

    /// <summary>
    /// ADJUST_STOP or ADJUST_TARGET: moves every held part's stop, only where it tightens it (FR-RISK-06), or its
    /// target, or removes the target. The parts are rebased onto this cycle, so their new orders cite its evaluation
    /// (AC-01) and take client order IDs no earlier order had (FR-EXE-04). The keeper brings their orders in line: on
    /// Futures the new stop rests before the old one is cancelled, and on Spot the stop or OCO list is replaced.
    /// </summary>
    private async Task<ExecutionResult> AdjustAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var order = request.Order;

        if (order.Kind == SizedOrderKind.StopAdjustment && order.StopPrice is null)
        {
            throw new ArgumentException("A stop adjustment needs a stop.", nameof(request));
        }

        using var held = await locks.HoldAsync(request.AgentId, cancellationToken);

        if (await OpenAsync(request, cancellationToken) is not { } session)
        {
            return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.KeyUnusable);
        }

        using (session)
        {
            var reconciled = await reconciler.ReconcileAsync(session, readPosition: false, cancellationToken);

            if (!reconciled.Verified)
            {
                logger.LogWarning("Binance couldn't confirm the agent's orders, so the stop or target isn't moved");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable));
            }

            var orders = reconciled.Orders;
            var parts = Parts(orders);

            if (parts.Count == 0)
            {
                logger.LogInformation("The agent no longer holds the position, so there's nothing to adjust");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.NoPosition));
            }

            var now = time.GetUtcNow();
            var side = ProtectionKeeper.SideOf(orders.EntryOf(parts[0]));
            var adjusted = new List<ProtectionRow>();

            foreach (var part in parts.Take(ClientOrderIds.MaximumIndex + 1))
            {
                Rebase(part, request, adjusted.Count, now);

                if (order.Kind == SizedOrderKind.StopAdjustment)
                {
                    if (ProtectionKeeper.Tightens(side, part.StopPrice, order.StopPrice!.Value))
                    {
                        part.StopPrice = order.StopPrice.Value;
                    }
                }
                else
                {
                    part.TargetPrice = order.TargetPrice;
                    part.TargetDropped = false;
                }

                adjusted.Add(part);
            }

            await StoreAsync(adjusted, []);

            logger.LogInformation(
                "The position's stop is now {Stop} and its target {Target}", adjusted.Max(part => part.StopPrice), order.TargetPrice);

            var pass = await KeepAsync(session, cancellationToken);
            var after = pass.Orders.Protections.Where(part => adjusted.Any(changed => changed.Id == part.Id)).ToList();

            var status = after switch
            {
                _ when after.All(part => part.Status == ProtectionStatus.Closed) => ExecutionStatus.Closed,
                _ when after.Any(part => part.Status is ProtectionStatus.Pending or ProtectionStatus.Closing or ProtectionStatus.Abandoned or ProtectionStatus.Dust)
                    || after.Any(part => part.Status == ProtectionStatus.Closed) => ExecutionStatus.Unprotected,
                _ => ExecutionStatus.Adjusted,
            };

            return Finish(
                session,
                new ExecutionResult(status)
                {
                    TargetNotPlaced = order.Kind == SizedOrderKind.TargetAdjustment && order.TargetPrice is not null && after.Any(part => part.TargetDropped),
                    ClientOrderIds = Sent(pass.Orders, request),
                },
                pass);
        }
    }

    /// <summary>
    /// Sends the entry once (FR-EXE-04). The row is already written, so a send that's cut off is settled by its client
    /// order ID, and an entry is never sent again (FR-EXE-05).
    /// </summary>
    private async Task SendAsync(AgentSession session, OrderRow entry, CancellationToken cancellationToken)
    {
        var request = new OrderRequest(
            entry.Symbol,
            entry.Side,
            entry.Type == OrderKind.Limit ? OrderType.Limit : OrderType.Market,
            entry.Quantity,
            entry.ClientOrderId,
            RequestPriority.Normal,
            price: entry.Price);

        try
        {
            var placed = await trading.PlaceOrderAsync(session.Market, session.Key, request, cancellationToken);
            var now = time.GetUtcNow();

            entry.SentAt = now;
            ProtectionKeeper.Apply(entry, placed, now);

            logger.LogInformation(
                "Sent entry {ClientOrderId}: {Status}, {Executed} of {Quantity} filled", entry.ClientOrderId, entry.Status, entry.ExecutedQuantity, entry.Quantity);
        }
        catch (ExchangeException exception)
        {
            ProtectionKeeper.Failed([entry], exception, time.GetUtcNow());

            if (exception.Failure == ExchangeFailure.Unknown)
            {
                logger.LogWarning("Entry {ClientOrderId}'s outcome is unknown, so it's settled by its client order ID", entry.ClientOrderId);
            }
            else
            {
                logger.LogWarning(
                    "Entry {ClientOrderId} wasn't placed: {Failure}, code {ErrorCode}", entry.ClientOrderId, exception.Failure, exception.Code);
            }

            if (exception.IsFilterRejection)
            {
                await RefreshAsync(session, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Only before the send began, so it never reached Binance.
            ProtectionKeeper.NotSent([entry], time.GetUtcNow());
            await store.SaveAsync(entry);
            throw;
        }

        // An entry that hasn't ended counts as resolved only once the keeper has protected what filled.
        if (entry.Status is OrderStatus.NotPlaced or OrderStatus.Rejected)
        {
            entry.Resolved = true;
        }

        await store.SaveAsync(entry);
    }

    /// <summary>
    /// Passes over the agent's orders until nothing is urgent: the entry's outcome is known, and every fill has a
    /// resting stop or has been closed. Whatever remains goes to the watch.
    /// </summary>
    private async Task<KeeperPass> KeepAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + MaximumWait;
        var pass = await keeper.AdvanceAsync(session, cancellationToken);

        while (pass.Urgent && time.GetUtcNow() < until)
        {
            await Task.Delay(UrgentPassDelay, time, cancellationToken);
            pass = await keeper.AdvanceAsync(session, cancellationToken);
        }

        return pass;
    }

    /// <summary>Opens the agent's session, or null when its key can't be used. Binance no longer listing the pair is logged and null too.</summary>
    private async Task<AgentSession?> OpenAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await sessions.OpenAsync(
                request.UserId, request.AgentId, request.CredentialId, request.Market, request.Environment, request.Symbol, cancellationToken);
        }
        catch (AgentSessionException exception)
        {
            logger.LogWarning("Nothing was sent: {Failure}", exception.Failure);
            return null;
        }
    }

    /// <summary>The parts of the position an exit or an adjustment may act on, oldest first. One being closed already is left alone.</summary>
    private static List<ProtectionRow> Parts(AgentOrders orders) =>
        [
            .. orders.Protections
                .Where(protection => protection.Status is ProtectionStatus.Pending or ProtectionStatus.Resting)
                .OrderBy(protection => protection.OpenedAt)
                .ThenBy(protection => protection.CreatedAt),
        ];

    /// <summary>
    /// Points a part's orders at this cycle: they cite its evaluation (AC-01), and their client order IDs derive from its
    /// candle and the part's place among this cycle's (FR-EXE-04). The cycle has no entry, so no other order shares them.
    /// </summary>
    private static void Rebase(ProtectionRow part, ExecutionRequest request, int index, DateTimeOffset now)
    {
        part.CycleId = request.CycleId;
        part.IdCandleClose = request.CandleClose;
        part.IdIndex = index;
        part.Attempts = 0;
        part.UpdatedAt = now;
    }

    /// <summary>The agent's position on Futures, as its records hold it: positive for a long.</summary>
    private static decimal SignedHeld(AgentOrders orders) =>
        orders.Protections
            .Where(protection => OrderStore.HeldProtections.Contains(protection.Status))
            .Sum(protection => orders.EntryOf(protection).Side == ExchangeSide.Buy ? protection.Quantity : -protection.Quantity);

    private Task StoreAsync(List<ProtectionRow> changed, List<ProtectionRow> added) =>
        changed.Count == 0 && added.Count == 0 ? Task.CompletedTask : store.ReplaceAsync(changed, added);

    /// <summary>How an exit went, from the parts it took.</summary>
    private static ExecutionResult ExitResult(AgentOrders orders, List<Guid> taken, ExecutionRequest request)
    {
        if (taken.Count == 0)
        {
            return new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable);
        }

        var parts = orders.Protections.Where(protection => taken.Contains(protection.Id)).ToList();
        var rests = orders.Protections.Where(protection => protection.CycleId == request.CycleId && !taken.Contains(protection.Id)
            && protection.EndReason != ProtectionEnd.Split).ToList();

        var status = parts switch
        {
            _ when parts.Any(part => part.Status is ProtectionStatus.Abandoned or ProtectionStatus.Dust)
                || rests.Any(rest => rest.Status is ProtectionStatus.Closing or ProtectionStatus.Dust or ProtectionStatus.Abandoned
                    || rest.EndReason == ProtectionEnd.Unprotected) => ExecutionStatus.Unprotected,
            _ when parts.All(part => part.Status == ProtectionStatus.Closed) => ExecutionStatus.Exited,
            _ => ExecutionStatus.Unsettled,
        };

        return new ExecutionResult(status) { ClientOrderIds = Sent(orders, request) };
    }

    /// <summary>The client order IDs of the orders this cycle sent, in the order they were sent.</summary>
    private static IReadOnlyList<string> Sent(AgentOrders orders, ExecutionRequest request) =>
        [
            .. orders.Legs
                .Where(leg => leg.CycleId == request.CycleId && leg.SentAt is not null)
                .OrderBy(leg => leg.CreatedAt)
                .Select(leg => leg.ListClientOrderId ?? leg.ClientOrderId)
                .Distinct(),
        ];

    /// <summary>Hands what's left to the agent's watch, while the lock is still held, so the watch can't miss it.</summary>
    private ExecutionResult Finish(AgentSession session, ExecutionResult result, KeeperPass? pass = null)
    {
        if (pass is null or { HasWork: true })
        {
            watch.Ensure(session.UserId, session.AgentId);
        }

        return result;
    }

    /// <summary>How the entry went, from its rows.</summary>
    private static ExecutionResult Result(AgentOrders orders, Guid entryId)
    {
        var entry = orders.Entries.First(row => row.Id == entryId);
        var protections = orders.ProtectionsOf(entry).ToList();
        IReadOnlyList<string> ids =
        [
            entry.ClientOrderId,
            .. orders.Legs.Where(leg => leg.EntryId == entry.Id).OrderBy(leg => leg.CreatedAt).Select(leg => leg.ListClientOrderId ?? leg.ClientOrderId).Distinct(),
        ];

        var status = entry switch
        {
            { IsUnsettled: true } => ExecutionStatus.Unsettled,
            { Status: OrderStatus.NotPlaced } => ExecutionStatus.NotPlaced,
            { Status: OrderStatus.Rejected } => ExecutionStatus.Rejected,
            { ExecutedQuantity: 0m, IsLive: true } => ExecutionStatus.Placed,
            { ExecutedQuantity: 0m } => ExecutionStatus.NotPlaced,
            _ when protections.Any(protection => protection.Status is ProtectionStatus.Pending or ProtectionStatus.Closing
                or ProtectionStatus.Dust or ProtectionStatus.Abandoned) => ExecutionStatus.Unprotected,
            _ when protections.Count > 0 && protections.All(protection => protection.Status is ProtectionStatus.Closed) => ExecutionStatus.Closed,
            _ when protections.Any(protection => protection.Status == ProtectionStatus.Resting) => ExecutionStatus.Protected,
            _ when protections.Count == 0 && entry.IsLive => ExecutionStatus.Placed,
            _ => ExecutionStatus.Unprotected,
        };

        return new ExecutionResult(status)
        {
            ExchangeCode = entry.ErrorCode,
            TargetNotPlaced = entry.Target is not null && protections.Any(protection => protection.TargetDropped),
            ClientOrderIds = ids,
        };
    }

    private OrderRow NewEntry(ExecutionRequest request, SizedSide side, EntryType entryType, decimal stop)
    {
        var now = time.GetUtcNow();
        var order = request.Order;
        var limit = entryType == EntryType.Limit;
        var ttl = Math.Clamp(request.LimitTtlCandles ?? 1, 1, settings.MaxLimitTtlCandles);

        return new OrderRow
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            AgentId = request.AgentId,
            CycleId = request.CycleId,
            CandleClose = request.CandleClose,
            Origin = OrderOrigin.Council,
            CredentialId = request.CredentialId,
            Market = request.Market,
            Environment = request.Environment,
            Symbol = request.Symbol,
            Leg = OrderLeg.Entry,
            ClientOrderId = ClientOrderIds.Entry(request.AgentId, request.CandleClose),
            Type = limit ? OrderKind.Limit : OrderKind.Market,
            Side = side == SizedSide.Buy ? ExchangeSide.Buy : ExchangeSide.Sell,
            Quantity = order.Quantity!.Value,
            Price = limit ? order.LimitPrice : null,
            ProtectiveStop = stop,
            Target = order.TargetPrice,
            Status = OrderStatus.Sending,
            SettlesAt = now + ProtectionKeeper.SendWindow,

            // FR-EXE-01: counted in candles of the agent's timeframe, from the close the cycle decided on.
            ExpiresAt = limit ? request.CandleClose + (TimeframeLength.Of(request.Timeframe) * ttl) : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Binance must take a stop for the pair, and on Spot with a target the OCO list's LIMIT_MAKER. A pair whose order
    /// types aren't known yet isn't held back: a refused stop is closed at market anyway.
    /// </summary>
    private static bool SupportsProtection(ExchangeSymbolInfo symbol, Market market, bool target)
    {
        if (symbol.OrderTypes.Count == 0)
        {
            return true;
        }

        string[] needed = market == Market.Spot
            ? target ? ["STOP_LOSS", "LIMIT_MAKER"] : ["STOP_LOSS"]
            : target ? ["STOP_MARKET", "TAKE_PROFIT_MARKET"] : ["STOP_MARKET"];

        return needed.All(type => symbol.OrderTypes.Contains(type, StringComparer.Ordinal));
    }

    private bool AllowsEntry(ExecutionRequest request) => time.GetUtcNow() < request.EntryDeadline;

    private async Task RefreshAsync(AgentSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RefreshSymbolAsync(symbols, cancellationToken);
        }
        catch (Exception exception) when (exception is MarketDataException or ExchangeException)
        {
            logger.LogWarning("Couldn't refresh {Symbol}'s filters after Binance rejected an order on one", session.Symbol.Symbol);
        }
    }
}
