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
/// Sends a <see cref="SizedOrder"/>'s entry once, and sees what fills protected before it returns (FR-EXE-01, FR-EXE-02,
/// FR-EXE-05). It holds the agent's lock throughout, so the agent's watch can't act at the same time. Whatever is left,
/// such as a resting limit order, goes to the watch. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderExecutor(
    AgentLocks locks,
    AgentSessions sessions,
    OrderStore store,
    ProtectionKeeper keeper,
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

            // Exits and adjustments need the position, which reconciliation (#35) gives the Risk Engine first.
            _ => new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.Unsupported),
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

            // Until reconciliation (#35) gives the Risk Engine the position, an entry can't be sized against one.
            if (await keeper.HoldsPositionAsync(session, cancellationToken))
            {
                logger.LogInformation("The agent still holds a position, so no entry is sent");
                return Finish(session, new ExecutionResult(ExecutionStatus.NotSent, ExecutionReason.PositionOpen));
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
