using System.Collections.Concurrent;
using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Notifications;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Places the orders of an agent, and settles them with the exchange (SRS 3.2, step 7; FR-BIN-6,
/// FR-RSK-7). It is the one place of the Decision Engine that uses the orders of the Exchange
/// Gateway: an entry is made here from what the Risk Guard accepted, and from nothing else
/// (NFR-SEC-6). See docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An order is never sent twice.</b> Its row is written before it is sent, with the
/// identifier that it carries. When the call that placed it ends without saying what the
/// exchange made of it, the order is asked about by that identifier, and nothing else is done
/// with it until the exchange has said. An answer that the exchange has no such order counts
/// once the order could no longer arrive (<see cref="ExchangeOrderTimes.FinalAfter"/>). An entry
/// is never sent again after that: the next cycle decides anew, and no failure leads to an entry
/// (NFR-REL-2). A stop-loss and an order that closes a position are sent once more, as the same
/// order with the same identifier.
/// </para>
/// <para>
/// <b>Every open position has a stop-loss that rests on the exchange.</b> When an entry is
/// executed, its stop-loss is placed for what the agent holds of it. When the exchange refuses
/// the stop-loss, or it is not placed at the second try, the position is closed at market at
/// once, and its user is told. When it cannot be closed either, its user is told that, an agent
/// that runs waits for its user, and what settles the orders tries again.
/// </para>
/// <para>
/// <b>No entry is placed</b> while new entries are paused for the market and the environment of
/// the agent, because the clock of the service is not near the exchange's (FR-BIN-5), for an
/// agent that no longer opens positions, in a symbol that takes no stop, or for a plan with a
/// limit entry, which comes with the orders of each market.
/// </para>
/// <para>
/// <b>What it does for one agent it does one after the other:</b> a cycle, what settles the
/// orders, and a close of the agent's user take turns. What another instance of the application
/// does at the same moment is kept apart by the rows: see <see cref="StoredOrders"/>.
/// </para>
/// <para>
/// <b>A stored key is asked for with each call,</b> handed to the Exchange Gateway with that
/// call, and kept nowhere (FR-KEY-9).
/// </para>
/// </remarks>
internal sealed class AgentOrders(
    IExchangeOrders exchange,
    IExchangeClock entries,
    IExchangeMarketData market,
    IKeyHandOver keys,
    IKeyValidation keyChecks,
    StoredOrders stored,
    IAgentTrading agents,
    INotifications notifications,
    TimeProvider clock,
    ILogger<AgentOrders> logger) : IPlanOrders
{
    /// <summary>
    /// How long an order that is being sent may be on its way: the wait for a turn at the
    /// limiter, the time limit of the call, and the time after which the exchange's answer that
    /// it has no such order is final. A row that says for longer that it is being sent was cut
    /// off, and is asked about.
    /// </summary>
    public static readonly TimeSpan OnItsWay = TimeSpan.FromSeconds(35);

    // The longest that a wait sleeps before it looks at the clock again.
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(250);

    // One gate for each agent that an order was placed or settled for.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task PlaceAsync(CycleStart cycle, GuardResult accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(accepted);

        if (!accepted.IsAccepted)
        {
            throw new ArgumentException("Orders are placed for a plan that the Risk Guard accepted, and for no other.", nameof(accepted));
        }

        var agent = cycle.Agent;
        var pace = new Pace(cancellationToken, clock);

        using var turn = await EnterAsync(agent.Id, cancellationToken);

        switch (accepted.Case.Action)
        {
            case GuardedAction.OpenLong:
                await OpenAsync(cycle, accepted, OrderSide.Buy, pace);
                break;

            case GuardedAction.OpenShort:
                await OpenAsync(cycle, accepted, OrderSide.Sell, pace);
                break;

            case GuardedAction.Close:
                await CloseForPlanAsync(cycle, pace);
                break;

            case GuardedAction.Reverse:
                // What it opens is on the other side of what it closes, and is opened only when
                // that is closed: an agent holds one position (FR-CYC-5).
                if (await stored.OpenPositionAsync(agent.Id, cancellationToken) is { } held
                    && await CloseForPlanAsync(cycle, pace) == PositionCloseOutcome.Closed
                    && !pace.Stopping)
                {
                    await OpenAsync(cycle, accepted, held.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy, pace);
                }

                break;

            case GuardedAction.AdjustProtection:
                await AdjustAsync(cycle, accepted, pace);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(accepted), accepted.Case.Action, "No orders follow from such a plan.");
        }
    }

    public async Task SettleAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var turn = await EnterAsync(agentId, cancellationToken);

        await SettleAsync(agentId, new Pace(cancellationToken, clock));
    }

    public async Task<PositionCloseOutcome> CloseAtMarketAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var turn = await EnterAsync(agentId, cancellationToken);

        try
        {
            var pace = new Pace(cancellationToken, clock);

            // First what the exchange has to say: the stop-loss may have been executed already.
            await SettleAsync(agentId, pace);

            if (await stored.OpenPositionAsync(agentId, CancellationToken.None) is not { } position)
            {
                // An entry whose outcome is not known may still have opened one.
                var live = await stored.LiveOrdersAsync(agentId, CancellationToken.None);

                return live.Any(order => order.Purpose == OrderPurpose.Entry) ? PositionCloseOutcome.Failed : PositionCloseOutcome.NothingOpen;
            }

            return await CloseAsync(position, ExitReason.UserClose, cycleId: null, pace);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Whoever asked is answered with an outcome, and not with an error. What is left
            // undone is in the rows, for what settles the orders.
            logger.LogError(error, "The position of agent {AgentId} could not be closed at market for its user", agentId);

            return PositionCloseOutcome.Failed;
        }
    }

    /// <summary>
    /// Opens a position: an entry at the market for the quantity that the Risk Guard computed,
    /// and then its stop-loss.
    /// </summary>
    private async Task OpenAsync(CycleStart cycle, GuardResult accepted, OrderSide side, Pace pace)
    {
        var agent = cycle.Agent;
        var plan = accepted.Case;

        if (plan.EntryPrice is not null)
        {
            // An entry at a price rests until it is executed, and is protected from then on.
            // That takes the orders that each market has for it, which are not built.
            NotEntered(cycle, "LimitEntry");

            return;
        }

        if (accepted.Size is not { Quantity: > 0m } size || plan.StopLoss is not { } stopLoss)
        {
            // The Risk Guard accepts no plan that opens a position without both.
            NotEntered(cycle, "NoSizeOrStopLoss");

            return;
        }

        // The watch on the clock pauses nothing by itself: what places an entry asks (FR-BIN-5).
        if (entries.EntriesPaused(agent.Market, agent.Mode))
        {
            NotEntered(cycle, "EntriesPaused");

            return;
        }

        // No position is opened where no stop-loss can rest (FR-RSK-7).
        if (await SymbolAsync(agent.Market, agent.Mode, agent.Symbol, pace.Token) is not { StopOrders: true })
        {
            NotEntered(cycle, "NoStopOrders");

            return;
        }

        var (entry, refusal) = await stored.BeginEntryAsync(
            agent, cycle.CycleId, side, size.Quantity, stopLoss, plan.TakeProfit, pace.Token);

        if (entry is null)
        {
            NotEntered(cycle, refusal.ToString()!);

            return;
        }

        // Until it is sent, the entry is the cycle's to stop. From then on it is followed.
        entry = await SendAsync(entry, pace.Token);
        entry = await FollowAsync(entry, pace);

        if (entry.PositionId is { } opened && await stored.PositionAsync(opened, CancellationToken.None) is { ClosedAt: null } position)
        {
            // Also when the cycle is being stopped: no entry is left without protection (NFR-REL-5).
            await ProtectAsync(position, cycle.CycleId, pace, previousStopLoss: null);
        }
    }

    /// <summary>Closes the open position of the agent of a cycle, for the plan of the cycle.</summary>
    private async Task<PositionCloseOutcome> CloseForPlanAsync(CycleStart cycle, Pace pace)
    {
        if (await stored.OpenPositionAsync(cycle.Agent.Id, pace.Token) is not { } position)
        {
            // Its stop-loss was executed while the team decided.
            logger.LogInformation(
                "The plan of agent {AgentId} for the close at {ClosedAt} closes a position, and the agent holds none any more",
                cycle.Agent.Id,
                cycle.ClosedAt);

            return PositionCloseOutcome.NothingOpen;
        }

        return await CloseAsync(position, ExitReason.TeamClose, cycle.CycleId, pace);
    }

    /// <summary>
    /// Moves the stop-loss of the open position to the price of the plan, and keeps the
    /// take-profit of the plan. The stop-loss that rests is taken off first, and the new one is
    /// placed. When the new one cannot be placed, the old price is tried again, and when that
    /// cannot be placed either, the position is closed at market (FR-RSK-7).
    /// </summary>
    private async Task AdjustAsync(CycleStart cycle, GuardResult accepted, Pace pace)
    {
        var plan = accepted.Case;

        if (await stored.OpenPositionAsync(cycle.Agent.Id, pace.Token) is not { } position)
        {
            logger.LogInformation(
                "The plan of agent {AgentId} for the close at {ClosedAt} adjusts protection, and the agent holds no position any more",
                cycle.Agent.Id,
                cycle.ClosedAt);

            return;
        }

        // The take-profit is a price only: no order rests for it yet.
        if (plan.TakeProfit is { } takeProfit && takeProfit != position.TakeProfit)
        {
            await stored.TakeProfitToAsync(position.Id, takeProfit, CancellationToken.None);
        }

        if (plan.StopLoss is not { } stopLoss || stopLoss == position.StopLoss)
        {
            return;
        }

        if (!await TakeOffStopLossAsync(position))
        {
            // It rests as it was: the position is protected at the old price.
            logger.LogWarning(
                "The stop-loss of the position of agent {AgentId} could not be taken off the exchange, so it stays where it is",
                position.AgentId);

            return;
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } now)
        {
            return;
        }

        await stored.StopLossToAsync(now.Id, stopLoss, CancellationToken.None);
        await ProtectAsync(now, cycle.CycleId, pace, previousStopLoss: position.StopLoss);
    }

    /// <summary>
    /// Closes a position at market: takes its stop-loss off the exchange, and places the order
    /// that closes it. When it cannot be closed, the stop-loss is placed again.
    /// </summary>
    private async Task<PositionCloseOutcome> CloseAsync(Position position, ExitReason reason, Guid? cycleId, Pace pace)
    {
        // The stop-loss first: on the Spot market it holds what the close sells, and on the
        // Futures market it would be left behind.
        if (!await TakeOffStopLossAsync(position))
        {
            // The position is open as it was, with its stop-loss.
            logger.LogWarning(
                "The position of agent {AgentId} was not closed: its stop-loss could not be taken off the exchange", position.AgentId);

            return PositionCloseOutcome.Failed;
        }

        // The stop-loss may have been executed before it could be taken off.
        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } open)
        {
            return PositionCloseOutcome.NothingOpen;
        }

        if (await CloseAtMarketAsync(open, reason, cycleId, pace))
        {
            return PositionCloseOutcome.Closed;
        }

        // Not closed, and unprotected now. The stop-loss goes back, unless what asked is being
        // stopped: then it is what settles the orders that puts it back.
        if (!pace.Stopping && await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } left)
        {
            await ProtectAsync(left, cycleId, pace, previousStopLoss: null);
        }

        return await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null }
            ? PositionCloseOutcome.Failed
            : PositionCloseOutcome.Closed;
    }

    /// <summary>
    /// Takes the stop-loss of a position off the exchange, and says whether none rests any more:
    /// it was taken off, or was executed, or there was none. It is false when an order of the
    /// agent is still with the exchange, or may be.
    /// </summary>
    private async Task<bool> TakeOffStopLossAsync(Position position)
    {
        foreach (var order in await stored.LiveOrdersAsync(position.AgentId, CancellationToken.None))
        {
            if (order.Purpose != OrderPurpose.StopLoss || !OrderStatuses.IsResting(order.Status))
            {
                // An order whose outcome is not known, or another close. Neither is this one's to end.
                return false;
            }

            if (OrderStatuses.IsLive((await CancelAsync(order)).Status))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Places the order that closes a position at market, and follows it, at most twice: a
    /// second time when the first was not placed, or ended with a part of the position.
    /// </summary>
    /// <returns>Whether the position is closed.</returns>
    private async Task<bool> CloseAtMarketAsync(Position position, ExitReason reason, Guid? cycleId, Pace pace)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (await stored.BeginForPositionAsync(position.Id, OrderPurpose.Close, reason, cycleId, CancellationToken.None) is not { } close)
            {
                break;
            }

            close = await SendAsync(close, CancellationToken.None);
            close = await FollowAsync(close, pace);

            if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null })
            {
                logger.LogInformation(
                    "The position of agent {AgentId} was closed at market: {ExitReason}, with the order {ExchangeOrderId}",
                    position.AgentId,
                    reason,
                    close.ExchangeOrderId);

                return true;
            }

            // Still with the exchange, or not known: it is followed by what settles the orders.
            // Refused: the exchange would refuse it again.
            if (OrderStatuses.IsLive(close.Status) || close.Status == OrderStatus.Rejected || pace.Stopping)
            {
                break;
            }
        }

        return await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null };
    }

    /// <summary>
    /// Has the stop-loss of an open position rest on the exchange, at the price that the
    /// position says (FR-RSK-7). It is placed at most twice: a second time when the first was
    /// not placed. When the exchange refuses it, or it is not placed at the second try, the
    /// position is closed at market at once, and its user is told.
    /// </summary>
    /// <param name="position">The position.</param>
    /// <param name="cycleId">The cycle that asks, or null when none does.</param>
    /// <param name="pace">Whether what asks is being stopped: then the stop-loss is sent once, and the rest is left to what settles the orders.</param>
    /// <param name="previousStopLoss">
    /// The price that the stop-loss was at before a plan moved it. When it cannot be placed at
    /// the new price, it is placed at this one again before the position is given up on.
    /// </param>
    /// <returns>Whether the position is protected, or closed.</returns>
    private async Task<bool> ProtectAsync(Position position, Guid? cycleId, Pace pace, decimal? previousStopLoss)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (await stored.BeginForPositionAsync(position.Id, OrderPurpose.StopLoss, reason: null, cycleId, CancellationToken.None)
                is not { } stopLoss)
            {
                // Closed meanwhile, or another is at it.
                return await stored.IsSettledAsync(position.AgentId, CancellationToken.None);
            }

            stopLoss = await SendAsync(stopLoss, CancellationToken.None);
            stopLoss = await FollowAsync(stopLoss, pace);

            if (OrderStatuses.IsResting(stopLoss.Status) || stopLoss.Status == OrderStatus.Filled)
            {
                await stored.ProtectedAgainAsync(position.Id, CancellationToken.None);

                logger.LogInformation(
                    "The stop-loss of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}",
                    position.AgentId,
                    stopLoss.ExchangeOrderId);

                return true;
            }

            // Not known whether the exchange has it: it is asked about by what settles the
            // orders, and nothing is placed beside it. And nothing more is begun for a cycle
            // that is being stopped.
            if (OrderStatuses.IsUnsettled(stopLoss.Status) || pace.Stopping)
            {
                return false;
            }

            // Refused: the exchange would refuse it again.
            if (stopLoss.Status == OrderStatus.Rejected)
            {
                break;
            }
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } open)
        {
            return true;
        }

        // A stop-loss that a plan moved goes back to where it was, and rests there as before.
        if (previousStopLoss is { } previous && previous != open.StopLoss)
        {
            logger.LogWarning(
                "The stop-loss of the position of agent {AgentId} could not be placed at its new price, so it is placed at the old one again",
                open.AgentId);

            await stored.StopLossToAsync(open.Id, previous, CancellationToken.None);

            return await ProtectAsync(open, cycleId, pace, previousStopLoss: null);
        }

        // The protective order cannot be placed: the position is closed at market at once, and
        // its user is told (FR-RSK-7).
        logger.LogWarning(
            "The stop-loss of the position of agent {AgentId} could not be placed, so the position is closed at market", open.AgentId);

        if (await CloseAtMarketAsync(open, ExitReason.ProtectionFailed, cycleId, pace))
        {
            await TellAsync(
                open,
                $"The stop-loss of the position in {open.Symbol} ({open.Mode}) could not be placed, so the position was closed at market.");

            return true;
        }

        // It is open with no stop-loss, and could not be closed. Its user is told once, an
        // agent that runs waits for its user, and what settles the orders tries again.
        if (await stored.AlertedAsync(open.Id, CancellationToken.None))
        {
            logger.LogError(
                "The position of agent {AgentId} is open with no stop-loss, and could not be closed at market", open.AgentId);

            await TellAsync(
                open,
                $"The stop-loss of the position in {open.Symbol} ({open.Mode}) could not be placed, and the position could not be " +
                "closed at market either. It is open with no stop-loss. Close it with \"Close position now\", or at Binance.");

            await agents.RequireAttentionAsync(open.UserId, open.AgentId, CancellationToken.None);
        }

        return false;
    }

    /// <summary>Settles the orders of an agent with the exchange. See <see cref="IPlanOrders.SettleAsync"/>.</summary>
    private async Task SettleAsync(Guid agentId, Pace pace)
    {
        // The orders that are not over: what became of them, as far as the exchange says.
        foreach (var order in await stored.LiveOrdersAsync(agentId, pace.Token))
        {
            if (pace.Stopping)
            {
                return;
            }

            // Being sent now, by another instance of the application: it is that one's.
            if (order.Status == OrderStatus.Sending && clock.GetUtcNow() < order.UpdatedAt + OnItsWay)
            {
                return;
            }

            await AskAsync(order);
        }

        if (pace.Stopping)
        {
            return;
        }

        // An open position that no order is under way for has its stop-loss, or gets it now.
        if (await stored.OpenPositionAsync(agentId, CancellationToken.None) is { } position
            && (await stored.LiveOrdersAsync(agentId, CancellationToken.None)).Count == 0)
        {
            await ProtectAsync(position, cycleId: null, pace, previousStopLoss: null);
        }

        // The executions of the orders that are over, where they are not stored yet.
        foreach (var order in await stored.WithoutFillsAsync(agentId, CancellationToken.None))
        {
            if (pace.Stopping)
            {
                return;
            }

            if (await FillsAsync(order, order.ExchangeOrderId!) is { } fills)
            {
                await stored.RecordFillsAsync(order, fills, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Sends an order whose row is written, once, and writes what the call said of it.
    /// </summary>
    /// <param name="order">The order, which is being sent.</param>
    /// <param name="beforeSending">Cancels the call while it is not sent yet. Nothing cancels it after that.</param>
    /// <returns>The order as it is stored after that.</returns>
    /// <exception cref="OperationCanceledException">The call was cancelled before it was sent. The order was not placed.</exception>
    private async Task<Order> SendAsync(Order order, CancellationToken beforeSending)
    {
        var key = await keys.ExchangeKeyAsync(order.UserId, order.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            logger.LogWarning(
                "The {OrderPurpose} of agent {AgentId} was not sent: the Binance key of its user for {TradingMode} is not there ({KeyHandOverOutcome})",
                order.Purpose,
                order.AgentId,
                order.Mode,
                key.Outcome);

            return await MoveAsync(order, OrderStatus.NotPlaced);
        }

        ExchangeOrderResult result;

        try
        {
            result = await exchange.PlaceAsync(
                order.UserId, order.Market, order.Mode, key.ApiKey!, key.SecretKey!, RequestOf(order), beforeSending);
        }
        catch (OperationCanceledException) when (beforeSending.IsCancellationRequested)
        {
            // Cancelled while it waited for its turn: nothing was sent.
            await MoveAsync(order, OrderStatus.NotPlaced);

            throw;
        }

        switch (result.Outcome)
        {
            case ExchangeOrderOutcome.Answered:
                return await TakeAsync(order, result.Order!);

            case ExchangeOrderOutcome.NotSent:
                return await MoveAsync(order, OrderStatus.NotPlaced);

            case ExchangeOrderOutcome.KeyRefused:
                await RecheckKeyAsync(order);

                return await MoveAsync(order, OrderStatus.Rejected);

            // The exchange refused it. An order that was sent before may be what it refused
            // this one for: it is asked about once more before the refusal is believed.
            case ExchangeOrderOutcome.Rejected or ExchangeOrderOutcome.NotFound:
                if (order.Sends > 1 && await FindAsync(order) is { Outcome: ExchangeOrderOutcome.Answered, Order: { } there })
                {
                    return await TakeAsync(order, there);
                }

                return await MoveAsync(order, OrderStatus.Rejected);

            // No word of what the exchange made of it: it is asked about before anything else.
            default:
                return await MoveAsync(order, OrderStatus.Unknown);
        }
    }

    /// <summary>
    /// Follows an order whose outcome is unknown until the exchange has said what became of it,
    /// or says nothing more for now: it waits until an answer that the exchange has no such
    /// order would be final, and asks.
    /// </summary>
    private async Task<Order> FollowAsync(Order order, Pace pace)
    {
        while (order.Status == OrderStatus.Unknown)
        {
            if (!await pace.UntilAsync(order.UpdatedAt + ExchangeOrderTimes.FinalAfter))
            {
                // What asked is being stopped: the order is asked about by what settles the orders.
                break;
            }

            var asked = await AskAsync(order);

            if (asked.Status == OrderStatus.Unknown && asked.UpdatedAt == order.UpdatedAt)
            {
                // The exchange said nothing of it.
                break;
            }

            order = asked;
        }

        return order;
    }

    /// <summary>
    /// Asks the exchange what became of an order, by its identifier, and writes what it said.
    /// An order that the exchange does not have was not placed once it could no longer arrive:
    /// an entry is then over, and a stop-loss or a close is sent once more, as the same order.
    /// </summary>
    /// <returns>The order as it is stored after that.</returns>
    private async Task<Order> AskAsync(Order order)
    {
        var result = await FindAsync(order);

        switch (result?.Outcome)
        {
            case ExchangeOrderOutcome.Answered:
                return await TakeAsync(order, result.Order!);

            case ExchangeOrderOutcome.NotFound when OrderStatuses.IsResting(order.Status):
                // It rested, and is gone.
                return await MoveAsync(order, OrderStatus.Canceled, said: true);

            case ExchangeOrderOutcome.NotFound:
                var since = order.UpdatedAt + (order.Status == OrderStatus.Sending ? OnItsWay : ExchangeOrderTimes.FinalAfter);

                if (clock.GetUtcNow() < since)
                {
                    // It may still arrive: the answer says nothing yet.
                    return order;
                }

                // No entry is sent again. What protects or closes a position is, once.
                if (order.Purpose != OrderPurpose.Entry && await stored.SendAgainAsync(order, CancellationToken.None))
                {
                    logger.LogWarning(
                        "The exchange does not have the {OrderPurpose} of agent {AgentId}, which could no longer arrive, so it is sent once more",
                        order.Purpose,
                        order.AgentId);

                    return await stored.FindAsync(order.Id, CancellationToken.None) is { Status: OrderStatus.Sending } again
                        ? await SendAsync(again, CancellationToken.None)
                        : order;
                }

                return await MoveAsync(order, OrderStatus.NotPlaced, said: true);

            case ExchangeOrderOutcome.KeyRefused:
                await RecheckKeyAsync(order);

                return order;

            // The exchange said nothing of the order.
            default:
                return order;
        }
    }

    /// <summary>Takes an order that rests off the exchange, and writes what became of it.</summary>
    /// <returns>The order as it is stored after that. It still rests when the exchange did not take it off.</returns>
    private async Task<Order> CancelAsync(Order order)
    {
        var key = await keys.ExchangeKeyAsync(order.UserId, order.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            return order;
        }

        var result = await exchange.CancelAsync(
            order.UserId, order.Market, order.Mode, key.ApiKey!, key.SecretKey!, RefOf(order), CancellationToken.None);

        switch (result.Outcome)
        {
            case ExchangeOrderOutcome.Answered:
                return await TakeAsync(order, result.Order!);

            // It does not rest any more, or the call did not say: the exchange is asked what became of it.
            case ExchangeOrderOutcome.NotFound or ExchangeOrderOutcome.Unknown:
                return await AskAsync(order);

            case ExchangeOrderOutcome.KeyRefused:
                await RecheckKeyAsync(order);

                return order;

            default:
                return order;
        }
    }

    /// <summary>Writes what the exchange said of an order, and what follows from it for the position.</summary>
    private async Task<Order> TakeAsync(Order order, ExchangeOrder said)
    {
        var status = said.Status switch
        {
            ExchangeOrderStatus.Open => OrderStatus.Open,
            ExchangeOrderStatus.PartiallyFilled => OrderStatus.PartiallyFilled,
            ExchangeOrderStatus.Filled => OrderStatus.Filled,
            ExchangeOrderStatus.Canceled => OrderStatus.Canceled,
            ExchangeOrderStatus.Rejected => OrderStatus.Rejected,
            ExchangeOrderStatus.Expired => OrderStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(said), said.Status, null),
        };

        if (order.Purpose != OrderPurpose.Entry)
        {
            return await MoveAsync(order, status, said.ExchangeOrderId, said.ExecutedQuantity, said.AveragePrice, said: true);
        }

        // An entry at the market is over at once. One that is not yet is asked about again.
        if (!said.IsOver)
        {
            return await MoveAsync(order, OrderStatus.Unknown, said.ExchangeOrderId, said: true);
        }

        // Nothing of it was executed: there is no position.
        if (said.ExecutedQuantity <= 0m)
        {
            return await MoveAsync(order, status, said.ExchangeOrderId, said: true);
        }

        return await OpenedAsync(order, said, status);
    }

    /// <summary>
    /// Opens the position that an executed entry gave. What the agent holds of it is what was
    /// executed, less a fee that the exchange took in the asset that was bought, which the
    /// executions say. Where they are needed and cannot be read, the entry stays unknown, and is
    /// asked about again.
    /// </summary>
    private async Task<Order> OpenedAsync(Order entry, ExchangeOrder said, OrderStatus status)
    {
        var fills = said.Fills;
        var held = said.ExecutedQuantity;
        var average = said.AveragePrice;

        if (entry.Market == Market.Spot)
        {
            // The fee of a purchase on the Spot market may be taken in what was bought.
            if (fills.Count == 0 && await FillsOfAsync(entry, said.ExchangeOrderId) is { } read)
            {
                fills = read;
            }

            if (fills.Count == 0 || await SymbolAsync(entry.Market, entry.Mode, entry.Symbol, CancellationToken.None) is not { } symbol)
            {
                logger.LogWarning(
                    "The entry of agent {AgentId} was executed as the order {ExchangeOrderId}, and what the agent holds of it is not known yet",
                    entry.AgentId,
                    said.ExchangeOrderId);

                return await MoveAsync(entry, OrderStatus.Unknown, said.ExchangeOrderId, said: true);
            }

            held -= fills.Where(fill => fill.FeeAsset == symbol.BaseAsset).Sum(fill => fill.Fee);

            // What an order can be for: a whole number of quantity steps.
            if (symbol.QuantityStep > 0m)
            {
                held = Math.Floor(held / symbol.QuantityStep) * symbol.QuantityStep;
            }
        }

        // The price that it was entered at, from the executions where the order does not say it.
        if (average is null && fills.Sum(fill => fill.Quantity) is > 0m and var executed)
        {
            average = fills.Sum(fill => fill.Price * fill.Quantity) / executed;
        }

        if (average is not { } entryPrice)
        {
            logger.LogWarning(
                "The entry of agent {AgentId} was executed as the order {ExchangeOrderId}, and the exchange has not said at which price yet",
                entry.AgentId,
                said.ExchangeOrderId);

            return await MoveAsync(entry, OrderStatus.Unknown, said.ExchangeOrderId, said: true);
        }

        if (held <= 0m)
        {
            // Less than an order can be for. Nothing can protect or close it.
            logger.LogWarning(
                "The entry of agent {AgentId} was executed as the order {ExchangeOrderId} for less than an order can be for, so it opens no position",
                entry.AgentId,
                said.ExchangeOrderId);

            return await MoveAsync(entry, status, said.ExchangeOrderId, said.ExecutedQuantity, entryPrice, said: true);
        }

        var position = await stored.OpenAsync(
            entry,
            status,
            said.ExchangeOrderId,
            said.ExecutedQuantity,
            entryPrice,
            held,
            [.. fills.Select(fill => RowOf(entry, fill))],
            CancellationToken.None);

        if (position is not null)
        {
            logger.LogInformation(
                "Agent {AgentId} opened a {PositionSide} position in {Symbol} with the order {ExchangeOrderId}",
                position.AgentId,
                position.Side,
                position.Symbol,
                said.ExchangeOrderId);
        }

        return await stored.FindAsync(entry.Id, CancellationToken.None) ?? entry;
    }

    /// <summary>
    /// Writes what has become of an order, and gives the order as it is stored after that:
    /// also when another has written it first.
    /// </summary>
    private async Task<Order> MoveAsync(
        Order order,
        OrderStatus status,
        string? exchangeOrderId = null,
        decimal executed = 0m,
        decimal? averagePrice = null,
        bool said = false)
    {
        var written = await stored.MoveAsync(order, status, exchangeOrderId, executed, averagePrice, said, CancellationToken.None);
        var now = await stored.FindAsync(order.Id, CancellationToken.None) ?? order;

        if (written && now.Status != order.Status)
        {
            logger.LogInformation(
                "The {OrderPurpose} of agent {AgentId} in {Symbol} is {OrderStatus}, as the order {ExchangeOrderId}",
                now.Purpose,
                now.AgentId,
                now.Symbol,
                now.Status,
                now.ExchangeOrderId);
        }

        return now;
    }

    /// <summary>Asks the exchange about an order, or gives nothing when the key of its user is not there.</summary>
    private async Task<ExchangeOrderResult?> FindAsync(Order order)
    {
        var key = await keys.ExchangeKeyAsync(order.UserId, order.Mode, CancellationToken.None);

        return key.Outcome == KeyHandOverOutcome.Handed
            ? await exchange.FindAsync(
                order.UserId, order.Market, order.Mode, key.ApiKey!, key.SecretKey!, RefOf(order), CancellationToken.None)
            : null;
    }

    /// <summary>The executions of an order as the exchange says them, or null when it did not say.</summary>
    private async Task<IReadOnlyList<ExchangeFill>?> FillsOfAsync(Order order, string exchangeOrderId)
    {
        var key = await keys.ExchangeKeyAsync(order.UserId, order.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            return null;
        }

        var read = await exchange.FillsAsync(
            order.UserId, order.Market, order.Mode, key.ApiKey!, key.SecretKey!, order.Symbol, exchangeOrderId, CancellationToken.None);

        return read.Outcome == ExchangeCallOutcome.Succeeded ? read.Fills : null;
    }

    /// <summary>The executions of an order as rows, or null when the exchange did not say them.</summary>
    private async Task<IReadOnlyList<Fill>?> FillsAsync(Order order, string exchangeOrderId) =>
        await FillsOfAsync(order, exchangeOrderId) is { } fills ? [.. fills.Select(fill => RowOf(order, fill))] : null;

    /// <summary>The rules of a symbol as the exchange says them, or null when it did not say or the market lacks it.</summary>
    private async Task<ExchangeSymbol?> SymbolAsync(Market on, TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        // The gateway shares the read among everyone that needs it (FR-BIN-3).
        var listing = await market.GetSymbolsAsync(on, mode, cancellationToken);

        return listing.Outcome == ExchangeCallOutcome.Succeeded
            ? listing.Symbols.FirstOrDefault(listed => listed.Symbol == symbol)
            : null;
    }

    /// <summary>
    /// Has the credential of an order's user checked again, after the exchange answered a call
    /// as it answers a wrong credential: the check decides whether it is (FR-KEY-3).
    /// </summary>
    private async Task RecheckKeyAsync(Order order)
    {
        try
        {
            await keyChecks.RevalidateExchangeKeyAsync(order.UserId, order.Mode, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The Binance key of user {UserId} for {TradingMode} could not be checked again", order.UserId, order.Mode);
        }
    }

    /// <summary>Tells the user of a position what became of it. That it could not be told is said, and stops nothing.</summary>
    private async Task TellAsync(Position position, string content)
    {
        try
        {
            await notifications.RaiseAsync(
                position.UserId, NotificationType.ProtectionFailure, position.AgentId, content, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The user {UserId} could not be told about the position of agent {AgentId}", position.UserId, position.AgentId);
        }
    }

    private void NotEntered(CycleStart cycle, string why) =>
        logger.LogInformation(
            "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}", cycle.Agent.Id, cycle.ClosedAt, why);

    private async Task<IDisposable> EnterAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);

        return new Turn(gate);
    }

    private static ExchangeOrderRequest RequestOf(Order order) => new(
        order.ClientOrderId,
        order.Symbol,
        order.Side == OrderSide.Buy ? ExchangeOrderSide.Buy : ExchangeOrderSide.Sell,
        TypeOf(order),
        order.Quantity,
        order.Price,

        // Everything but an entry only closes a position.
        Closes: order.Purpose != OrderPurpose.Entry);

    private static ExchangeOrderRef RefOf(Order order) => new(order.ClientOrderId, order.Symbol, TypeOf(order));

    private static ExchangeOrderType TypeOf(Order order) =>
        order.Type == OrderType.Stop ? ExchangeOrderType.Stop : ExchangeOrderType.Market;

    private static Fill RowOf(Order order, ExchangeFill fill) => new(
        Guid.CreateVersion7(), order.Id, order.UserId, fill.TradeId, fill.Price, fill.Quantity, fill.Fee, fill.FeeAsset, fill.At);

    /// <summary>The turn of one caller at the gate of an agent.</summary>
    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>
    /// Whether what asked for the orders is being stopped: the cycle, 45 seconds
    /// before the next close, or the application. Nothing is begun after that, and no wait goes on.
    /// </summary>
    private sealed class Pace(CancellationToken stopping, TimeProvider clock)
    {
        /// <summary>Canceled when what asked is stopped. It cancels what is not sent yet.</summary>
        public CancellationToken Token => stopping;

        public bool Stopping => stopping.IsCancellationRequested;

        /// <summary>
        /// Waits until a moment of the application's clock. It looks at the clock every beat, so
        /// that a clock that was set is noticed.
        /// </summary>
        /// <returns>Whether the moment came. It did not when what asked was stopped first.</returns>
        public async Task<bool> UntilAsync(DateTimeOffset moment)
        {
            while (!Stopping && clock.GetUtcNow() < moment)
            {
                try
                {
                    await Task.Delay(Beat, clock, stopping);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return !Stopping;
        }
    }
}
