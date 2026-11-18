using System.Collections.Concurrent;
using System.Globalization;
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
/// FR-RSK-7, FR-BIN-S1 to FR-BIN-S3, FR-BIN-F1 to FR-BIN-F3, FR-BIN-F6). It is the one place of
/// the Decision Engine that uses the orders of the Exchange Gateway: an entry is made here from
/// what the Risk Guard accepted, and from nothing else (NFR-SEC-6). See docs/orders.md.
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
/// <b>On the Spot market the stop-loss is one order of a pair with the take-profit</b>
/// (FR-BIN-S2). The exchange takes both with one call or neither, and ends one when the other is
/// triggered. So the two are sent, sent once more, and taken off together, and never one of
/// them alone: a row of a pair is no order that <see cref="SendAsync"/> sends. What has become
/// of one of the two is written when the exchange has said it of that one.
/// </para>
/// <para>
/// <b>On the Futures market the stop-loss and the take-profit are two orders of their own</b>
/// (FR-BIN-F3): each only reduces the position, and is triggered on the mark price. The exchange
/// ends neither for the other. So the stop-loss is placed first, and the take-profit beside it
/// once the stop-loss rests, and when one of the two has closed the position, the other is
/// taken off the exchange, so that it never acts on a position that the agent opens later. A
/// take-profit that the exchange refuses closes the position at market, as a stop-loss that it
/// refuses does. One that was not placed for another reason is placed by what settles the
/// orders: the position has its stop-loss meanwhile.
/// </para>
/// <para>
/// <b>An entry at a price rests</b> until it is executed, and is taken off the exchange when
/// the next candle of its agent has closed, which is when the next cycle starts (FR-BIN-S2).
/// It is taken off earlier when a part of it is executed that can be protected, so that what
/// was bought has its protection at once, and when its agent no longer opens positions. What
/// settles the orders asks about it every minute, and protects what it bought.
/// </para>
/// <para>
/// <b>No entry is placed</b> while new entries are paused for the market and the environment of
/// the agent, because the clock of the service is not near the exchange's (FR-BIN-5), for an
/// agent that no longer opens positions, in a symbol that takes no stop, or on the Spot market
/// no pair, or on the Futures market no take-profit, for a sale on the Spot market, where an
/// agent sells only what it bought (FR-BIN-S1), and while the account of its user does not hold
/// what the entry needs: its value on the Spot market (FR-BIN-S3), and its margin on the
/// Futures market (FR-BIN-F6).
/// </para>
/// <para>
/// <b>On the Futures market an agent trades with isolated margin, in one-way mode</b>
/// (FR-BIN-F2). Before an entry the account of its user is read: an account that keeps a long
/// and a short position of a symbol apart, that margins with more than one asset, or that holds
/// a position in the symbol which the agent did not open, gets no entry, its user is told what
/// to change, and the agent waits for them. Those are settings of the whole account, which the
/// service does not change. Then the symbol is given isolated margin and the leverage that the
/// Risk Guard computed, which are settings of the symbol, and only then the entry is placed.
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
    IExchangeAdapter account,
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

    /// <summary>
    /// The time in which the user of an agent is told once that its entries are skipped because
    /// the account does not hold what they need, or because the exchange does not set the margin
    /// of the symbol. A cycle that decides to enter comes with every candle, and the page of an
    /// agent shows a few notifications: one for each would leave no room for the others.
    /// </summary>
    public static readonly TimeSpan SkippedSaidEvery = TimeSpan.FromDays(1);

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
                    await OpenAsync(cycle, accepted, held.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy, pace, closedJustNow: true);
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

            // An entry that still rests would open a position after this: it is taken off. What
            // was executed of it by then is a position, which is closed below.
            foreach (var entry in await stored.LiveOrdersAsync(agentId, CancellationToken.None))
            {
                if (entry.Purpose == OrderPurpose.Entry && OrderStatuses.IsResting(entry.Status))
                {
                    await TakeOffEntryAsync(entry, "UserClose");
                }
            }

            if (await stored.OpenPositionAsync(agentId, CancellationToken.None) is not { } position)
            {
                // An entry whose outcome is not known, or that could not be taken off, may still open one.
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
    /// Opens a position: an entry for the quantity that the Risk Guard computed, at the market
    /// or at the price of the plan, and then its protection. On the Futures market the symbol is
    /// given isolated margin and the leverage that the Risk Guard computed before the entry
    /// (FR-BIN-F2).
    /// </summary>
    /// <param name="cycle">The cycle whose plan it is.</param>
    /// <param name="accepted">What the Risk Guard accepted.</param>
    /// <param name="side">Whether the entry buys or sells.</param>
    /// <param name="pace">Whether the cycle is being stopped.</param>
    /// <param name="closedJustNow">Whether the plan closed a position of the agent a moment ago, as one that reverses does.</param>
    private async Task OpenAsync(CycleStart cycle, GuardResult accepted, OrderSide side, Pace pace, bool closedJustNow = false)
    {
        var agent = cycle.Agent;
        var plan = accepted.Case;
        var spot = agent.Market == Market.Spot;

        if (spot && side == OrderSide.Sell)
        {
            // An agent of the Spot market buys, and sells what it bought to exit: it does not
            // borrow or short (FR-BIN-S1). A sale that opens would sell what else the account holds.
            NotEntered(cycle, "SpotLongOnly");

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
        if (await SymbolAsync(agent.Market, agent.Mode, agent.Symbol, pace.Token) is not { StopOrders: true } symbol)
        {
            NotEntered(cycle, "NoStopOrders");

            return;
        }

        // Nor on the Spot market where the pair of its stop-loss and its take-profit cannot (FR-BIN-S2).
        if (spot && plan.TakeProfit is not null && !symbol.PairOrders)
        {
            NotEntered(cycle, "NoPairOrders");

            return;
        }

        // Nor on the Futures market where no take-profit can rest beside the stop-loss (FR-BIN-F3).
        if (!spot && plan.TakeProfit is not null && !symbol.TakeProfitOrders)
        {
            NotEntered(cycle, "NoTakeProfitOrders");

            return;
        }

        // An agent that no longer opens positions, or holds one, is told so, and not that its
        // account is short. It is asked again as the row of the entry is written.
        if (await stored.EntryRefusalAsync(agent, pace.Token) is { } early)
        {
            NotEntered(cycle, early.ToString());

            return;
        }

        // The account of the Futures market is one that the agent can trade in as it trades:
        // in one-way mode, with isolated margin, and with no position in the symbol (FR-BIN-F2).
        if (!spot && await AccountInTheWayAsync(agent, closedJustNow, pace.Token) is { } inTheWay)
        {
            NotEntered(cycle, inTheWay);

            return;
        }

        // What the entry needs of the asset that the symbol is priced in: its value on the
        // Spot market (FR-BIN-S3), and the margin of that value at the leverage of the order on
        // the Futures market (FR-BIN-F6).
        var leverage = Math.Max(size.Leverage, 1);
        var value = size.Quantity * (plan.EntryPrice ?? plan.CurrentPrice);
        var needs = spot ? value : value / leverage;

        if (await ShortAsync(agent, needs, leverage, rechecksKey: true, pace.Token) is { } shortOf)
        {
            NotEntered(cycle, shortOf);

            return;
        }

        // The margin of the symbol, and its leverage, are set before the entry (FR-BIN-F2).
        if (!spot && await MarginNotSetAsync(agent, leverage, pace.Token) is { } notSet)
        {
            NotEntered(cycle, notSet);

            return;
        }

        var (entry, refusal) = await stored.BeginEntryAsync(
            agent,
            cycle.CycleId,
            side,
            size.Quantity,
            stopLoss,
            plan.TakeProfit,
            plan.EntryPrice,

            // The next close of the candles of the agent: the next cycle starts then.
            cycle.ClosedAt + agent.Timeframe.Length(),
            pace.Token);

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
            await ProtectAsync(position, cycle.CycleId, pace, previous: null);
        }
        else if (OrderStatuses.IsResting(entry.Status))
        {
            logger.LogInformation(
                "The entry of agent {AgentId} in {Symbol} rests on the exchange as the order {ExchangeOrderId}, until {CancelAfter}",
                entry.AgentId,
                entry.Symbol,
                entry.ExchangeOrderId,
                entry.CancelAfter);
        }
        else if (entry.Status == OrderStatus.Rejected)
        {
            // Another agent of the account may have spent what this entry counted on, or the
            // price moved: the exchange refuses an entry that the account cannot pay for.
            await ShortAsync(agent, needs, leverage, rechecksKey: false, CancellationToken.None);
        }
    }

    public async Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Only the Futures market has modes of an account, and positions.
        return agent.Market == Market.Futures ? await ConflictAsync(agent, rechecksKey: false, cancellationToken) : null;
    }

    /// <summary>
    /// What in the account of an agent's user on the Futures market stands in the way of the
    /// agent, which trades one symbol there in one-way mode with isolated margin (FR-BIN-F2):
    /// a mode of the whole account, which the service does not change, or a position in the
    /// symbol that the agent did not open. An account that cannot be read is one of which it is
    /// not known.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="rechecksKey">Whether a credential that the exchange refuses is checked again.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task<AccountConflict?> ConflictAsync(Agent agent, bool rechecksKey, CancellationToken cancellationToken)
    {
        var key = await keys.ExchangeKeyAsync(agent.UserId, agent.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            return AccountConflict.NotKnown;
        }

        var read = await account.GetFuturesAccountAsync(
            agent.UserId, agent.Mode, key.ApiKey!, key.SecretKey!, agent.Symbol, cancellationToken);

        if (read.Outcome != ExchangeCallOutcome.Succeeded)
        {
            if (rechecksKey && read.Outcome == ExchangeCallOutcome.AuthenticationFailed)
            {
                await RecheckKeyAsync(agent.UserId, agent.Mode);
            }

            return AccountConflict.NotKnown;
        }

        if (read.HedgeMode)
        {
            return AccountConflict.HedgeMode;
        }

        if (read.MultiAssetsMargin)
        {
            return AccountConflict.MultiAssetsMode;
        }

        // A position in the symbol is the agent's own when its records hold one, or an order
        // that may have opened one. What the account holds beside that is not compared.
        return read.HoldsPosition
            && await stored.OpenPositionAsync(agent.Id, cancellationToken) is null
            && (await stored.LiveOrdersAsync(agent.Id, cancellationToken)).Count == 0
                ? AccountConflict.PositionInSymbol
                : null;
    }

    /// <summary>
    /// Whether the account of an agent of the Futures market stands in the way of an entry, in
    /// which case none is placed. Where it is a mode of the account, or a position in the
    /// symbol, its user is told what to change, and the agent waits for them (FR-BIN-F2).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="closedJustNow">
    /// Whether the agent closed a position of its own a moment ago, for a plan that reverses.
    /// A position that the account still says then may be that one, which the exchange has not
    /// taken off its list yet: nobody is told for it, and no agent is made to wait.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Why no entry is placed, or null when nothing of the account stands in its way.</returns>
    private async Task<string?> AccountInTheWayAsync(Agent agent, bool closedJustNow, CancellationToken cancellationToken)
    {
        if ((await keys.ExchangeKeyAsync(agent.UserId, agent.Mode, CancellationToken.None)).Outcome != KeyHandOverOutcome.Handed)
        {
            // Without a key nothing is sent: the row of the entry says so.
            return null;
        }

        var conflict = await ConflictAsync(agent, rechecksKey: true, cancellationToken);

        if (conflict is null)
        {
            return null;
        }

        if (conflict == AccountConflict.NotKnown || (closedJustNow && conflict == AccountConflict.PositionInSymbol))
        {
            // It is not known that anything is to change: a failure leads to no entry (NFR-REL-2).
            // A position that is still there is found by the next entry.
            return "AccountUnknown";
        }

        var toChange = conflict switch
        {
            AccountConflict.HedgeMode =>
                "your Binance Futures account is in hedge mode, and the agent trades in one-way mode. Switch the position mode " +
                "of the account to one-way at Binance",
            AccountConflict.MultiAssetsMode =>
                "your Binance Futures account is in Multi-Assets mode, in which Binance takes no isolated margin. Switch the " +
                "asset mode of the account to single-asset at Binance",
            _ => $"your Binance Futures account holds a position in {agent.Symbol} that the agent did not open. Close it at Binance",
        };

        try
        {
            await notifications.RaiseAsync(
                agent.UserId,
                NotificationType.AttentionRequired,
                agent.Id,
                $"An entry in {agent.Symbol} ({agent.Mode}) was skipped, and the agent waits for you: {toChange}, and resume the agent.",
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The user {UserId} could not be told that agent {AgentId} waits for them", agent.UserId, agent.Id);
        }

        await agents.RequireAttentionAsync(agent.UserId, agent.Id, AgentStateCause.ExchangeSettingConflict, CancellationToken.None);

        return "AccountConflict";
    }

    /// <summary>
    /// Has the symbol of an agent of the Futures market use isolated margin, at the leverage
    /// that the Risk Guard computed for the order, before the entry is placed (FR-BIN-F2). Both
    /// are settings of the symbol in the account of the agent's user, and neither is one of the
    /// whole account. An entry is placed only once the exchange has said that both are set.
    /// </summary>
    /// <returns>Why no entry is placed, or null when both are set.</returns>
    private async Task<string?> MarginNotSetAsync(Agent agent, int leverage, CancellationToken cancellationToken)
    {
        var key = await keys.ExchangeKeyAsync(agent.UserId, agent.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            // Without a key nothing is sent: the row of the entry says so.
            return null;
        }

        var set = await exchange.SetIsolatedMarginAsync(
            agent.UserId, agent.Mode, key.ApiKey!, key.SecretKey!, agent.Symbol, leverage, cancellationToken);

        switch (set)
        {
            case ExchangeOrderOutcome.Answered:
                return null;

            case ExchangeOrderOutcome.KeyRefused:
                await RecheckKeyAsync(agent.UserId, agent.Mode);

                return "MarginNotSet";

            // The exchange refused the setting, which it does while the account has orders or a
            // position in the symbol that the setting would change the margin of.
            case ExchangeOrderOutcome.Rejected:
                await SkippedAsync(
                    agent,
                    $"An entry in {agent.Symbol} ({agent.Mode}) was skipped: Binance did not set isolated margin at a leverage of " +
                    $"{leverage} for {agent.Symbol}. It does not change the margin of a symbol while the account has open orders or a " +
                    "position in it: cancel or close them at Binance. This is said once in a day for an agent.");

                return "MarginRefused";

            // It is not known how the symbol is margined: a failure leads to no entry (NFR-REL-2).
            default:
                return "MarginNotSet";
        }
    }

    /// <summary>
    /// Whether the account of an agent holds less of the asset that its symbol is priced in
    /// than an entry needs, in which case the entry is skipped and its user is told: the value
    /// of the entry on the Spot market (FR-BIN-S3), and its margin in the wallet of the Futures
    /// market (FR-BIN-F6). An account that cannot be read is one that no entry is placed in
    /// (NFR-REL-2).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="needs">
    /// What the entry needs: its quantity at its price, and on the Futures market that value
    /// divided by the leverage of the order. Null when the price is not known.
    /// </param>
    /// <param name="leverage">The leverage of the order on the Futures market, which the notification says.</param>
    /// <param name="rechecksKey">Whether a credential that the exchange refuses is checked again.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Why no entry is placed, or null when the account covers it.</returns>
    private async Task<string?> ShortAsync(Agent agent, decimal? needs, int leverage, bool rechecksKey, CancellationToken cancellationToken)
    {
        var key = await keys.ExchangeKeyAsync(agent.UserId, agent.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            // Without a key nothing is sent: the row of the entry says so.
            return null;
        }

        if (needs is not { } amount)
        {
            return "BalanceUnknown";
        }

        var read = await account.GetBalancesAsync(agent.UserId, agent.Market, agent.Mode, key.ApiKey!, key.SecretKey!, cancellationToken);

        if (read.Outcome != ExchangeCallOutcome.Succeeded)
        {
            if (rechecksKey && read.Outcome == ExchangeCallOutcome.AuthenticationFailed)
            {
                await RecheckKeyAsync(agent.UserId, agent.Mode);
            }

            return "BalanceUnknown";
        }

        // An asset that is not among the balances is one that the account holds none of.
        var free = read.Balances.FirstOrDefault(balance => balance.Asset == agent.QuoteAsset)?.Available ?? 0m;

        if (free >= amount)
        {
            return null;
        }

        if (agent.Market == Market.Spot)
        {
            await SkippedAsync(
                agent,
                $"An entry in {agent.Symbol} ({agent.Mode}) was skipped: it needs {Written(amount)} {agent.QuoteAsset}, and the account " +
                $"has {Written(free)} {agent.QuoteAsset} free. This is said once in a day for an agent.");

            return "QuoteBalance";
        }

        await SkippedAsync(
            agent,
            $"An entry in {agent.Symbol} ({agent.Mode}) was skipped: it needs {Written(amount)} {agent.QuoteAsset} of margin at a " +
            $"leverage of {leverage}, and the Futures wallet has {Written(free)} {agent.QuoteAsset} available. This is said once in a " +
            "day for an agent.");

        return "Margin";
    }

    /// <summary>
    /// Tells the user of an agent that an entry of it was skipped, once in
    /// <see cref="SkippedSaidEvery"/>. That it could not be told is said, and stops nothing.
    /// </summary>
    private async Task SkippedAsync(Agent agent, string content)
    {
        try
        {
            await notifications.RaiseOnceAsync(
                agent.UserId, NotificationType.EntrySkipped, agent.Id, content, SkippedSaidEvery, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The user {UserId} could not be told that an entry of agent {AgentId} was skipped", agent.UserId, agent.Id);
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
    /// Moves the protection of the open position to the prices of the plan. What rests is taken
    /// off first, and placed at the new price. When it cannot be placed there, the old price is
    /// tried again, and when that cannot be placed either, the position is closed at market
    /// (FR-RSK-7). On the Spot market the stop-loss and the take-profit are one pair, which is
    /// taken off and placed anew when either price changes. On the Futures market they are two
    /// orders, and each price moves its own: the stop-loss rests on while the take-profit moves.
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

        var stopLoss = plan.StopLoss ?? position.StopLoss;
        var takeProfit = plan.TakeProfit ?? position.TakeProfit;

        if (stopLoss == position.StopLoss && takeProfit == position.TakeProfit)
        {
            return;
        }

        if (Pairs(position.Market, takeProfit))
        {
            if (!await TakeOffProtectionAsync(position))
            {
                // It rests as it was: the position is protected at the old prices.
                logger.LogWarning(
                    "The stop-loss of the position of agent {AgentId} could not be taken off the exchange, so it stays where it is",
                    position.AgentId);

                return;
            }

            if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } now)
            {
                return;
            }

            await stored.ProtectionToAsync(now.Id, stopLoss, takeProfit, CancellationToken.None);

            if (await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } paired)
            {
                await ProtectAsync(paired, cycle.CycleId, pace, previous: new Protection(position.StopLoss, position.TakeProfit));
            }

            return;
        }

        // Each price moves its own order. The stop-loss first: it is what the position must have.
        if (stopLoss != position.StopLoss)
        {
            if (!await TakeOffAsync(position, OrderPurpose.StopLoss))
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

            if (await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } moved)
            {
                await ProtectAsync(moved, cycle.CycleId, pace, previous: new Protection(position.StopLoss, moved.TakeProfit));
            }
        }

        if (takeProfit is not { } target || target == position.TakeProfit || pace.Stopping)
        {
            return;
        }

        // Then the take-profit, while the stop-loss rests where it is.
        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } held)
        {
            return;
        }

        if (!await TakeOffAsync(held, OrderPurpose.TakeProfit))
        {
            logger.LogWarning(
                "The take-profit of the position of agent {AgentId} could not be taken off the exchange, so it stays where it is",
                position.AgentId);

            return;
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null })
        {
            return;
        }

        await stored.TakeProfitToAsync(position.Id, target, CancellationToken.None);

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } aimed)
        {
            await ProtectAsync(aimed, cycle.CycleId, pace, previous: new Protection(aimed.StopLoss, position.TakeProfit));
        }
    }

    /// <summary>
    /// Closes a position at market: takes its protection off the exchange, and places the order
    /// that closes it. When it cannot be closed, the protection is placed again.
    /// </summary>
    private async Task<PositionCloseOutcome> CloseAsync(Position position, ExitReason reason, Guid? cycleId, Pace pace)
    {
        // The protection first: on the Spot market it holds what the close sells, and on the
        // Futures market it would be left behind.
        if (!await TakeOffProtectionAsync(position))
        {
            // The position is open as it was, with its stop-loss.
            logger.LogWarning(
                "The position of agent {AgentId} was not closed: its stop-loss could not be taken off the exchange", position.AgentId);

            return PositionCloseOutcome.Failed;
        }

        // The stop-loss, or the take-profit, may have been executed before it could be taken off.
        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } open)
        {
            return PositionCloseOutcome.NothingOpen;
        }

        if (await CloseAtMarketAsync(open, reason, cycleId, pace))
        {
            return PositionCloseOutcome.Closed;
        }

        // Not closed, and unprotected now. The protection goes back, unless what asked is being
        // stopped: then it is what settles the orders that puts it back.
        if (!pace.Stopping && await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } left)
        {
            await ProtectAsync(left, cycleId, pace, previous: null);
        }

        return await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null }
            ? PositionCloseOutcome.Failed
            : PositionCloseOutcome.Closed;
    }

    /// <summary>
    /// Takes the protection of a position off the exchange, and says whether none rests any
    /// more: it was taken off, or was executed, or there was none. A pair is taken off as one.
    /// Where the two are orders of their own, the take-profit is taken off first, so that what
    /// is left when the exchange does not take one off is the stop-loss. It is false when an
    /// order of the agent is still with the exchange, or may be.
    /// </summary>
    private async Task<bool> TakeOffProtectionAsync(Position position)
    {
        var pairs = new HashSet<Guid>();
        var live = await stored.LiveOrdersAsync(position.AgentId, CancellationToken.None);

        foreach (var order in live.OrderBy(order => order.Purpose == OrderPurpose.StopLoss))
        {
            if (order.Purpose is not (OrderPurpose.StopLoss or OrderPurpose.TakeProfit) || !OrderStatuses.IsResting(order.Status))
            {
                // An order whose outcome is not known, an entry, or another close. None is this one's to end.
                return false;
            }

            if (order.PairId is { } pairId)
            {
                // The second order of a pair went with the first.
                if (pairs.Add(pairId) && !await TakeOffPairAsync(pairId))
                {
                    return false;
                }

                continue;
            }

            if (OrderStatuses.IsLive((await CancelAsync(order)).Status))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Takes the order of one purpose that protects a position off the exchange, where it is an
    /// order of its own and of no pair, and says whether none of that purpose is with the
    /// exchange any more, or may be: it was taken off, or was executed, or there was none.
    /// </summary>
    private async Task<bool> TakeOffAsync(Position position, OrderPurpose purpose)
    {
        foreach (var order in await stored.LiveOrdersAsync(position.AgentId, CancellationToken.None))
        {
            if (order.Purpose != purpose)
            {
                continue;
            }

            if (!OrderStatuses.IsResting(order.Status) || OrderStatuses.IsLive((await CancelAsync(order)).Status))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Takes what is left of the protection of a position that is closed off the exchange: on
    /// the Futures market the stop-loss and the take-profit are two orders, and the exchange
    /// ends neither when the other has closed the position (FR-BIN-F3). Left to rest, it would
    /// act on a position that the agent opens later.
    /// </summary>
    private async Task TakeOffLeftBehindAsync(Guid agentId)
    {
        foreach (var order in await stored.LeftBehindAsync(agentId, CancellationToken.None))
        {
            // One whose outcome is not known is asked about first, and is not sent again.
            if (OrderStatuses.IsResting(order.Status))
            {
                await CancelAsync(order);
            }
        }
    }

    /// <summary>
    /// Takes a pair that rests off the exchange with one call, and writes what became of each
    /// of its orders. Whatever the call says but the two orders, each is asked about: a pair of
    /// which one order was executed is over at the exchange, and has nothing to take off.
    /// </summary>
    /// <returns>Whether neither order of the pair is with the exchange any more.</returns>
    private async Task<bool> TakeOffPairAsync(Guid pairId)
    {
        if (await stored.PairAsync(pairId, CancellationToken.None) is not { } pair)
        {
            return false;
        }

        var key = await keys.ExchangeKeyAsync(pair.StopLoss.UserId, pair.StopLoss.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            return false;
        }

        var result = await exchange.CancelPairAsync(
            pair.StopLoss.UserId,
            pair.StopLoss.Mode,
            key.ApiKey!,
            key.SecretKey!,
            new ExchangePairRef(Order.PairIdOf(pairId), pair.StopLoss.Symbol, pair.StopLoss.ClientOrderId, pair.TakeProfit.ClientOrderId),
            CancellationToken.None);

        if (result.Outcome == ExchangeOrderOutcome.Answered)
        {
            if (OrderStatuses.IsLive(pair.StopLoss.Status))
            {
                await TakeAsync(pair.StopLoss, result.StopLoss!);
            }

            if (OrderStatuses.IsLive(pair.TakeProfit.Status))
            {
                await TakeAsync(pair.TakeProfit, result.TakeProfit!);
            }
        }
        else
        {
            if (result.Outcome == ExchangeOrderOutcome.KeyRefused)
            {
                await RecheckKeyAsync(pair.StopLoss.UserId, pair.StopLoss.Mode);
            }

            await AskPairAsync(pair);
        }

        return await stored.PairAsync(pairId, CancellationToken.None) is { } now && now.Both.All(order => !OrderStatuses.IsLive(order.Status));
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
    /// position says (FR-RSK-7), and its take-profit with it: on the Spot market as one pair
    /// (FR-BIN-S2), and on the Futures market as an order of its own, which is placed once the
    /// stop-loss rests (FR-BIN-F3). Each is placed at most twice: a second time when the first
    /// was not placed. When the exchange refuses one, or the stop-loss is not placed at the
    /// second try, the position is closed at market at once, and its user is told.
    /// </summary>
    /// <param name="position">The position.</param>
    /// <param name="cycleId">The cycle that asks, or null when none does.</param>
    /// <param name="pace">Whether what asks is being stopped: then the stop-loss is sent once, and the rest is left to what settles the orders.</param>
    /// <param name="previous">
    /// The prices that the protection was at before a plan moved it. When it cannot be placed at
    /// the new prices, it is placed at these again before the position is given up on.
    /// </param>
    /// <returns>Whether the position is protected by its stop-loss, or closed.</returns>
    private async Task<bool> ProtectAsync(Position position, Guid? cycleId, Pace pace, Protection? previous)
    {
        var what = "stop-loss";
        var placed = await PlaceStopLossAsync(position, cycleId, pace);

        if (placed == Placed.Rests && TakesProfitAlone(position.Market, position.TakeProfit))
        {
            // The position has its stop-loss. A take-profit that is not placed now is placed by
            // what settles the orders, and only one that the exchange refuses closes the position.
            if (await PlaceTakeProfitAsync(position, cycleId, pace) != Placed.Failed)
            {
                return true;
            }

            what = "take-profit";
            placed = Placed.Failed;
        }

        if (placed != Placed.Failed)
        {
            return placed == Placed.Rests;
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null } open)
        {
            return true;
        }

        // Protection that a plan moved goes back to where it was, and rests there as before.
        if (previous is { } old && (old.StopLoss != open.StopLoss || old.TakeProfit != open.TakeProfit))
        {
            if (what == "stop-loss")
            {
                logger.LogWarning(
                    "The stop-loss of the position of agent {AgentId} could not be placed at its new price, so it is placed at the old one again",
                    open.AgentId);
            }
            else
            {
                logger.LogWarning(
                    "The take-profit of the position of agent {AgentId} could not be placed at its new price, so it is placed at the old one again",
                    open.AgentId);
            }

            await stored.ProtectionToAsync(open.Id, old.StopLoss, old.TakeProfit, CancellationToken.None);

            return await stored.PositionAsync(open.Id, CancellationToken.None) is not { ClosedAt: null } restored
                || await ProtectAsync(restored, cycleId, pace, previous: null);
        }

        // What still rests for the position is taken off before it is closed: an order that
        // closes is not begun beside one that rests. When it cannot be taken off, the position
        // is as it was, and what settles the orders tries again.
        if (!await TakeOffProtectionAsync(open))
        {
            return false;
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null })
        {
            return true;
        }

        // The protective order cannot be placed: the position is closed at market at once, and
        // its user is told (FR-RSK-7).
        if (what == "stop-loss")
        {
            logger.LogWarning(
                "The stop-loss of the position of agent {AgentId} could not be placed, so the position is closed at market", open.AgentId);
        }
        else
        {
            logger.LogWarning(
                "The take-profit of the position of agent {AgentId} could not be placed, so the position is closed at market", open.AgentId);
        }

        if (await CloseAtMarketAsync(open, ExitReason.ProtectionFailed, cycleId, pace))
        {
            await TellAsync(
                open,
                $"The {what} of the position in {open.Symbol} ({open.Mode}) could not be placed, so the position was closed at market.");

            return true;
        }

        // A position whose take-profit was refused had its stop-loss taken off for the close.
        // It goes back, and the take-profit is given up: tried again with every run, it would
        // have the stop-loss taken off every time. The position says no take-profit from then
        // on, until a plan names one, and its user is told.
        if (what == "take-profit"
            && !pace.Stopping
            && await stored.PositionAsync(position.Id, CancellationToken.None) is { ClosedAt: null } left
            && await PlaceStopLossAsync(left, cycleId, pace) == Placed.Rests)
        {
            await stored.ProtectionToAsync(left.Id, left.StopLoss, takeProfit: null, CancellationToken.None);

            logger.LogWarning(
                "The position of agent {AgentId} could not be closed at market, so it is open with its stop-loss, and with no take-profit",
                left.AgentId);

            await TellAsync(
                left,
                $"The take-profit of the position in {left.Symbol} ({left.Mode}) could not be placed, and the position could not be " +
                "closed at market either. It is open with its stop-loss, and with no take-profit. Close it with \"Close position " +
                "now\", or at Binance.");

            return true;
        }

        if (await stored.PositionAsync(position.Id, CancellationToken.None) is not { ClosedAt: null })
        {
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
                $"The {what} of the position in {open.Symbol} ({open.Mode}) could not be placed, and the position could not be " +
                "closed at market either. It is open with no stop-loss. Close it with \"Close position now\", or at Binance.");

            await agents.RequireAttentionAsync(open.UserId, open.AgentId, AgentStateCause.PositionUnprotected, CancellationToken.None);
        }

        return false;
    }

    /// <summary>
    /// Has the stop-loss of an open position rest on the exchange, at most twice: a second time
    /// when the first was not placed. On the Spot market it is placed as one pair with the
    /// take-profit of a position that says one. One that rests already is left as it is.
    /// </summary>
    /// <returns>
    /// That it rests, or the position is closed. That it is left to what settles the orders: its
    /// outcome is not known, another is at it, or what asked is being stopped. Or that it
    /// failed: the exchange refused it, or it was not placed at the second try.
    /// </returns>
    private async Task<Placed> PlaceStopLossAsync(Position position, Guid? cycleId, Pace pace)
    {
        var pairs = Pairs(position.Market, position.TakeProfit);

        // Where the take-profit is an order of its own, the stop-loss may rest already, and
        // the take-profit be what is missing.
        if (!pairs
            && (await stored.LiveOrdersAsync(position.AgentId, CancellationToken.None)).Any(order =>
                order is { Purpose: OrderPurpose.StopLoss, PairId: null }
                && order.PositionId == position.Id
                && OrderStatuses.IsResting(order.Status)))
        {
            return Placed.Rests;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Order stopLoss;
            Order? takeProfit = null;

            if (pairs)
            {
                if (await stored.BeginPairAsync(position.Id, cycleId, CancellationToken.None) is not { } pair)
                {
                    // Closed meanwhile, or another is at it.
                    return await stored.IsSettledAsync(position.AgentId, CancellationToken.None) ? Placed.Rests : Placed.Later;
                }

                pair = await SendPairAsync(pair);
                pair = await FollowPairAsync(pair, pace);
                (stopLoss, takeProfit) = pair;
            }
            else
            {
                if (await stored.BeginForPositionAsync(position.Id, OrderPurpose.StopLoss, reason: null, cycleId, CancellationToken.None)
                    is not { } alone)
                {
                    // Closed meanwhile, or another is at it.
                    return await stored.IsSettledAsync(position.AgentId, CancellationToken.None) ? Placed.Rests : Placed.Later;
                }

                stopLoss = await SendAsync(alone, CancellationToken.None);
                stopLoss = await FollowAsync(stopLoss, pace);
            }

            if (OrderStatuses.IsResting(stopLoss.Status) || stopLoss.Status == OrderStatus.Filled)
            {
                await stored.ProtectedAgainAsync(position.Id, CancellationToken.None);

                logger.LogInformation(
                    "The stop-loss of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}",
                    position.AgentId,
                    stopLoss.ExchangeOrderId);

                if (takeProfit is not null && OrderStatuses.IsResting(takeProfit.Status))
                {
                    logger.LogInformation(
                        "The take-profit of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}",
                        position.AgentId,
                        takeProfit.ExchangeOrderId);
                }

                return Placed.Rests;
            }

            // Not known whether the exchange has it: it is asked about by what settles the
            // orders, and nothing is placed beside it. And nothing more is begun for a cycle
            // that is being stopped.
            if (OrderStatuses.IsUnsettled(stopLoss.Status) || pace.Stopping)
            {
                return Placed.Later;
            }

            // Refused: the exchange would refuse it again.
            if (stopLoss.Status == OrderStatus.Rejected)
            {
                break;
            }
        }

        return Placed.Failed;
    }

    /// <summary>
    /// Has the take-profit of an open position of the Futures market rest on the exchange,
    /// beside its stop-loss, as an order of its own that only reduces the position (FR-BIN-F3).
    /// It is placed at most twice: a second time when the first was not placed.
    /// </summary>
    /// <returns>
    /// That it rests, or was executed. That it is left to what settles the orders: its outcome
    /// is not known, it was not placed, the key of its user is not there, or what asked is being
    /// stopped. The position has its stop-loss meanwhile. Or that it failed: the exchange
    /// refused it, as it refuses one that the price has passed.
    /// </returns>
    private async Task<Placed> PlaceTakeProfitAsync(Position position, Guid? cycleId, Pace pace)
    {
        var live = await stored.LiveOrdersAsync(position.AgentId, CancellationToken.None);

        if (live.FirstOrDefault(order => order.Purpose == OrderPurpose.TakeProfit) is { } there)
        {
            // It rests already, or is under way.
            return OrderStatuses.IsResting(there.Status) ? Placed.Rests : Placed.Later;
        }

        // Nothing more is begun for a cycle that is being stopped, and no row is written for an
        // order that cannot be sent.
        if (pace.Stopping
            || (await keys.ExchangeKeyAsync(position.UserId, position.Mode, CancellationToken.None)).Outcome != KeyHandOverOutcome.Handed)
        {
            return Placed.Later;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (await stored.BeginTakeProfitAsync(position.Id, cycleId, CancellationToken.None) is not { } takeProfit)
            {
                // Closed meanwhile, its stop-loss no longer rests, or another is at it.
                return Placed.Later;
            }

            takeProfit = await SendAsync(takeProfit, CancellationToken.None);
            takeProfit = await FollowAsync(takeProfit, pace);

            if (OrderStatuses.IsResting(takeProfit.Status))
            {
                logger.LogInformation(
                    "The take-profit of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}",
                    position.AgentId,
                    takeProfit.ExchangeOrderId);

                return Placed.Rests;
            }

            if (takeProfit.Status == OrderStatus.Filled)
            {
                // It closed the position, and the stop-loss is what it left behind.
                await TakeOffLeftBehindAsync(position.AgentId);

                return Placed.Rests;
            }

            if (OrderStatuses.IsUnsettled(takeProfit.Status) || pace.Stopping)
            {
                return Placed.Later;
            }

            // Refused: the exchange would refuse it again.
            if (takeProfit.Status == OrderStatus.Rejected)
            {
                return Placed.Failed;
            }
        }

        // Not placed at the second try. It is owed, and the position has its stop-loss.
        return Placed.Later;
    }

    /// <summary>Settles the orders of an agent with the exchange. See <see cref="IPlanOrders.SettleAsync"/>.</summary>
    private async Task SettleAsync(Guid agentId, Pace pace)
    {
        var pairs = new HashSet<Guid>();
        var takenOff = false;

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

            // The two orders of a pair are asked about together.
            if (order.PairId is { } pairId)
            {
                if (pairs.Add(pairId) && await stored.PairAsync(pairId, CancellationToken.None) is { } pair)
                {
                    await AskPairAsync(pair);
                }

                continue;
            }

            var asked = await AskAsync(order);

            // An entry that still rests, and is not to rest any longer (FR-BIN-S2).
            if (asked.Purpose == OrderPurpose.Entry && OrderStatuses.IsResting(asked.Status) && await DueAsync(asked) is { } why)
            {
                await TakeOffEntryAsync(asked, why);
                takenOff = true;
            }
        }

        // What an entry that was taken off has bought is protected also when what asked is
        // being stopped: no entry is left without protection (NFR-REL-5).
        if (pace.Stopping && !takenOff)
        {
            return;
        }

        // What one order of a position of the Futures market left behind when the other closed
        // the position is taken off: the exchange ends neither for the other (FR-BIN-F3).
        await TakeOffLeftBehindAsync(agentId);

        // A take-profit of a pair that rests without its stop-loss protects nothing. It is
        // taken off, or the exchange says what became of it, and the position gets its
        // protection anew.
        if (await stored.OpenPositionAsync(agentId, CancellationToken.None) is { } unprotected
            && await stored.LiveOrdersAsync(agentId, CancellationToken.None) is { Count: > 0 } left
            && left.All(order => order.Purpose == OrderPurpose.TakeProfit && order.Status == OrderStatus.Open && order.PairId is not null))
        {
            await TakeOffProtectionAsync(unprotected);
        }

        // An open position that no order is under way for has its stop-loss, or gets it now,
        // and on the Futures market its take-profit beside it.
        if (await stored.OpenPositionAsync(agentId, CancellationToken.None) is { } position
            && Unprotected(position, await stored.LiveOrdersAsync(agentId, CancellationToken.None)))
        {
            await ProtectAsync(position, cycleId: null, pace, previous: null);
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
    /// Why an entry that rests is taken off the exchange now, or null when it rests on: the
    /// next candle of its agent has closed, a part of it is executed that can be protected, or
    /// its agent no longer opens positions (FR-BIN-S2).
    /// </summary>
    private async Task<string?> DueAsync(Order entry)
    {
        if (entry.CancelAfter is { } due && clock.GetUtcNow() >= due)
        {
            return "TimeIsUp";
        }

        // What was bought has no protection while the rest of the entry rests. On the Spot
        // market a part that no order can be for is left to grow, or to the end of the entry.
        // On the Futures market an order that only reduces a position is held to no least
        // value, so any part is protected at once: it is leveraged, and has no stop until then.
        if (entry.Status == OrderStatus.PartiallyFilled
            && (entry.Market == Market.Futures
                || (await SymbolAsync(entry.Market, entry.Mode, entry.Symbol, CancellationToken.None) is { } symbol
                    && CanBeOrdered(symbol, entry.ExecutedQuantity, entry.AveragePrice ?? entry.Price ?? 0m))))
        {
            return "PartlyExecuted";
        }

        return await stored.TakesEntriesAsync(entry.UserId, entry.AgentId, CancellationToken.None) ? null : "NotRunning";
    }

    /// <summary>
    /// Takes an entry that rests off the exchange, and writes what became of it: what was
    /// executed of it by then opens a position.
    /// </summary>
    /// <returns>The entry as it is stored after that. It still rests when the exchange did not take it off.</returns>
    private async Task<Order> TakeOffEntryAsync(Order entry, string why)
    {
        logger.LogInformation(
            "The entry of agent {AgentId} in {Symbol} that still rests is taken off the exchange: {EntryTakenOffReason}",
            entry.AgentId,
            entry.Symbol,
            why);

        return await CancelAsync(entry);
    }

    /// <summary>
    /// Sends an order whose row is written, once, and writes what the call said of it.
    /// </summary>
    /// <param name="order">The order, which is being sent. It is of no pair: a pair is sent as one, by <see cref="SendPairAsync"/>.</param>
    /// <param name="beforeSending">Cancels the call while it is not sent yet. Nothing cancels it after that.</param>
    /// <returns>The order as it is stored after that.</returns>
    /// <exception cref="OperationCanceledException">The call was cancelled before it was sent. The order was not placed.</exception>
    private async Task<Order> SendAsync(Order order, CancellationToken beforeSending)
    {
        if (order.PairId is not null)
        {
            // Sent alone, it would rest outside its pair, where nothing takes it off.
            throw new InvalidOperationException("An order of a pair is sent with the other order of the pair, and never alone.");
        }

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
                await RecheckKeyAsync(order.UserId, order.Mode);

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
    /// Sends a pair whose two rows are written, once, as one call, and writes what the call
    /// said: of each order what the exchange answered of it, and of both that they were not
    /// placed, were refused, or that it is not known.
    /// </summary>
    /// <returns>The pair as it is stored after that.</returns>
    private async Task<OrderPair> SendPairAsync(OrderPair pair)
    {
        var (stopLoss, takeProfit) = pair;
        var key = await keys.ExchangeKeyAsync(stopLoss.UserId, stopLoss.Mode, CancellationToken.None);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            logger.LogWarning(
                "The {OrderPurpose} of agent {AgentId} was not sent: the Binance key of its user for {TradingMode} is not there ({KeyHandOverOutcome})",
                stopLoss.Purpose,
                stopLoss.AgentId,
                stopLoss.Mode,
                key.Outcome);

            return await MovePairAsync(pair, OrderStatus.NotPlaced);
        }

        var result = await exchange.PlacePairAsync(
            stopLoss.UserId,
            stopLoss.Mode,
            key.ApiKey!,
            key.SecretKey!,
            new ExchangePairRequest(
                Order.PairIdOf(pair.Id),
                stopLoss.Symbol,
                stopLoss.Quantity,
                stopLoss.ClientOrderId,
                stopLoss.Price!.Value,
                takeProfit.ClientOrderId,
                takeProfit.Price!.Value),
            CancellationToken.None);

        switch (result.Outcome)
        {
            case ExchangeOrderOutcome.Answered:
                await TakeAsync(stopLoss, result.StopLoss!);
                await TakeAsync(takeProfit, result.TakeProfit!);

                return await stored.PairAsync(pair.Id, CancellationToken.None) ?? pair;

            case ExchangeOrderOutcome.NotSent:
                return await MovePairAsync(pair, OrderStatus.NotPlaced);

            case ExchangeOrderOutcome.KeyRefused:
                await RecheckKeyAsync(stopLoss.UserId, stopLoss.Mode);

                return await MovePairAsync(pair, OrderStatus.Rejected);

            // The exchange refused it. A pair that was sent before may be what it refused this
            // one for: both orders are asked about before the refusal is believed, and a
            // refusal is believed of both only when the exchange has neither.
            case ExchangeOrderOutcome.Rejected or ExchangeOrderOutcome.NotFound:
                if (stopLoss.Sends > 1)
                {
                    var first = await FindAsync(stopLoss);
                    var second = await FindAsync(takeProfit);

                    if (first is not { Outcome: ExchangeOrderOutcome.NotFound } || second is not { Outcome: ExchangeOrderOutcome.NotFound })
                    {
                        // What became of each is written when the exchange says it of that one.
                        pair = await MovePairAsync(pair, OrderStatus.Unknown);

                        if (first is { Outcome: ExchangeOrderOutcome.Answered, Order: { } firstThere })
                        {
                            await TakeAsync(pair.StopLoss, firstThere);
                        }

                        if (second is { Outcome: ExchangeOrderOutcome.Answered, Order: { } secondThere })
                        {
                            await TakeAsync(pair.TakeProfit, secondThere);
                        }

                        return await stored.PairAsync(pair.Id, CancellationToken.None) ?? pair;
                    }
                }

                return await MovePairAsync(pair, OrderStatus.Rejected);

            // No word of what the exchange made of it: it is asked about before anything else.
            default:
                return await MovePairAsync(pair, OrderStatus.Unknown);
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

    /// <summary>The same for a pair whose outcome is unknown: both orders are asked about, as one.</summary>
    private async Task<OrderPair> FollowPairAsync(OrderPair pair, Pace pace)
    {
        while (pair.Both.All(order => order.Status == OrderStatus.Unknown))
        {
            if (!await pace.UntilAsync(pair.StopLoss.UpdatedAt + ExchangeOrderTimes.FinalAfter))
            {
                break;
            }

            await AskPairAsync(pair);

            var asked = await stored.PairAsync(pair.Id, CancellationToken.None) ?? pair;

            if (asked.Both.All(order => order.Status == OrderStatus.Unknown) && asked.StopLoss.UpdatedAt == pair.StopLoss.UpdatedAt)
            {
                // The exchange said nothing of it.
                break;
            }

            pair = asked;
        }

        return pair;
    }

    /// <summary>
    /// Asks the exchange what became of an order, by its identifier, and writes what it said.
    /// An order that the exchange does not have was not placed once it could no longer arrive:
    /// an entry is then over, and a stop-loss or a close is sent once more, as the same order.
    /// An order of a pair is never sent alone: see <see cref="AskPairAsync"/>.
    /// </summary>
    /// <returns>The order as it is stored after that.</returns>
    private async Task<Order> AskAsync(Order order) => await HeardAsync(order, await FindAsync(order));

    /// <summary>Writes what the exchange said when it was asked about an order, and does what follows from it.</summary>
    /// <param name="order">The order that was asked about.</param>
    /// <param name="result">How the question ended, or null when it could not be asked.</param>
    /// <returns>The order as it is stored after that.</returns>
    private async Task<Order> HeardAsync(Order order, ExchangeOrderResult? result)
    {
        switch (result?.Outcome)
        {
            case ExchangeOrderOutcome.Answered:
                return await TakeAsync(order, result.Order!);

            // An entry of which a part is executed is not one that the exchange forgets: the
            // answer says nothing of it, and it is asked about again.
            case ExchangeOrderOutcome.NotFound when order is { Purpose: OrderPurpose.Entry, Status: OrderStatus.PartiallyFilled }:
                return order;

            case ExchangeOrderOutcome.NotFound when OrderStatuses.IsResting(order.Status):
                // The exchange's word that it has no such order counts once it has had the
                // order for as long as one takes to arrive. Before that it says nothing: an
                // order that was written off while it rests would be placed a second time.
                if (clock.GetUtcNow() < order.UpdatedAt + ExchangeOrderTimes.FinalAfter)
                {
                    return order;
                }

                // It rested, and is gone.
                return await MoveAsync(order, OrderStatus.Canceled, said: true);

            case ExchangeOrderOutcome.NotFound:
                var since = order.UpdatedAt + (order.Status == OrderStatus.Sending ? OnItsWay : ExchangeOrderTimes.FinalAfter);

                if (clock.GetUtcNow() < since)
                {
                    // It may still arrive: the answer says nothing yet.
                    return order;
                }

                // No entry is sent again, and no order of a pair alone. What protects or closes
                // a position is, once, and only while the position is open: on the Futures
                // market the other order of the position may have closed it meanwhile.
                if (order.Purpose != OrderPurpose.Entry
                    && order.PairId is null
                    && order.PositionId is { } protects
                    && await stored.PositionAsync(protects, CancellationToken.None) is { ClosedAt: null }
                    && await stored.SendAgainAsync(order, CancellationToken.None))
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
                await RecheckKeyAsync(order.UserId, order.Mode);

                return order;

            // The exchange said nothing of the order.
            default:
                return order;
        }
    }

    /// <summary>
    /// Asks the exchange what became of the two orders of a pair, each by its own identifier,
    /// and writes of each what the exchange said of it. A pair of which the exchange has
    /// neither order, once they could no longer arrive, is sent once more, as the same pair,
    /// and after that it was not placed. When one of the two has ended and the other was asked
    /// about before, the other is asked about again: the exchange ends it when the first is
    /// triggered.
    /// </summary>
    /// <remarks>
    /// The exchange holds the two as one list, and ends neither while the other rests. So an
    /// answer that it has no such order, of one that rested, says nothing while it has the other
    /// resting: an order that was written off on that answer would leave the position looking
    /// unprotected when the pair ends, and a second pair would be placed for what the agent no
    /// longer holds, which could sell what else the account holds (FR-BIN-S3).
    /// </remarks>
    private async Task AskPairAsync(OrderPair pair)
    {
        if (pair.Both.All(order => OrderStatuses.IsUnsettled(order.Status)))
        {
            await AskUnsaidPairAsync(pair);

            return;
        }

        var heard = new List<(Order Order, ExchangeOrderResult? Said)>();

        foreach (var order in pair.Both)
        {
            if (OrderStatuses.IsLive(order.Status))
            {
                heard.Add((order, await FindAsync(order)));
            }
        }

        var rests = heard.Any(one => one.Said is { Outcome: ExchangeOrderOutcome.Answered, Order.IsOver: false });
        Order? resting = null;
        var ended = false;

        foreach (var (order, said) in heard)
        {
            if (rests && said?.Outcome == ExchangeOrderOutcome.NotFound && OrderStatuses.IsResting(order.Status))
            {
                continue;
            }

            var now = await HeardAsync(order, said);

            if (!OrderStatuses.IsLive(now.Status))
            {
                ended = true;
            }
            else if (OrderStatuses.IsResting(now.Status))
            {
                resting ??= now;
            }
        }

        if (ended && resting is not null)
        {
            await AskAsync(resting);
        }
    }

    /// <summary>
    /// Asks about a pair of which the call that placed it did not say what the exchange made of
    /// it, or that was cut off while it was being sent: both rows say so.
    /// </summary>
    private async Task AskUnsaidPairAsync(OrderPair pair)
    {
        var (stopLoss, takeProfit) = pair;
        var first = await FindAsync(stopLoss);
        var second = await FindAsync(takeProfit);

        if (first?.Outcome == ExchangeOrderOutcome.KeyRefused || second?.Outcome == ExchangeOrderOutcome.KeyRefused)
        {
            await RecheckKeyAsync(stopLoss.UserId, stopLoss.Mode);

            return;
        }

        // The exchange has the pair: each order is written as the exchange says it. One that
        // it did not say is asked about again.
        if (first is { Outcome: ExchangeOrderOutcome.Answered } || second is { Outcome: ExchangeOrderOutcome.Answered })
        {
            if (first is { Outcome: ExchangeOrderOutcome.Answered, Order: { } firstThere })
            {
                await TakeAsync(stopLoss, firstThere);
            }

            if (second is { Outcome: ExchangeOrderOutcome.Answered, Order: { } secondThere })
            {
                await TakeAsync(takeProfit, secondThere);
            }

            return;
        }

        // Only the word of the exchange that it has neither says that there is no pair.
        if (first?.Outcome != ExchangeOrderOutcome.NotFound || second?.Outcome != ExchangeOrderOutcome.NotFound)
        {
            return;
        }

        var since = stopLoss.UpdatedAt + (stopLoss.Status == OrderStatus.Sending ? OnItsWay : ExchangeOrderTimes.FinalAfter);

        if (clock.GetUtcNow() < since)
        {
            // It may still arrive: the answer says nothing yet.
            return;
        }

        if (await stored.SendPairAgainAsync(pair, CancellationToken.None))
        {
            logger.LogWarning(
                "The exchange does not have the {OrderPurpose} of agent {AgentId}, which could no longer arrive, so it is sent once more",
                "Pair",
                stopLoss.AgentId);

            if (await stored.PairAsync(pair.Id, CancellationToken.None) is { } again && again.Both.All(order => order.Status == OrderStatus.Sending))
            {
                await SendPairAsync(again);
            }

            return;
        }

        await MovePairAsync(pair, OrderStatus.NotPlaced, said: true);
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
                await RecheckKeyAsync(order.UserId, order.Mode);

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

        if (!said.IsOver)
        {
            // An entry at a price rests until it is executed or taken off, and opens no
            // position while it does: what it bought by then is a position when it is over. An
            // entry at the market is over at once, and one that is not yet is asked about again.
            return order.Type == OrderType.Limit
                ? await MoveAsync(order, status, said.ExchangeOrderId, said.ExecutedQuantity, said.AveragePrice, said: true)
                : await MoveAsync(order, OrderStatus.Unknown, said.ExchangeOrderId, said: true);
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
    /// executions say: an order of the agent sells no more than the agent bought (FR-BIN-S3).
    /// Where they are needed and cannot be read, the entry stays unknown, and is asked about
    /// again.
    /// </summary>
    private async Task<Order> OpenedAsync(Order entry, ExchangeOrder said, OrderStatus status)
    {
        var fills = said.Fills;
        var held = said.ExecutedQuantity;
        var average = said.AveragePrice;
        ExchangeSymbol? rules = null;

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

            rules = symbol;
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

        // Less than an order can be for, as of an entry at a price that was taken off with a
        // small part of it executed: nothing can protect or close it, so it is no position.
        if (held <= 0m || (rules is not null && entry.Type == OrderType.Limit && !CanBeOrdered(rules, held, entryPrice)))
        {
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
            Moved(now);
        }

        return now;
    }

    /// <summary>
    /// Writes what has become of a pair as a whole, and gives the pair as it is stored after
    /// that: also when another has written it first.
    /// </summary>
    private async Task<OrderPair> MovePairAsync(OrderPair pair, OrderStatus status, bool said = false)
    {
        var written = await stored.MovePairAsync(pair, status, said, CancellationToken.None);
        var now = await stored.PairAsync(pair.Id, CancellationToken.None) ?? pair;

        if (written)
        {
            foreach (var (before, after) in pair.Both.Zip(now.Both))
            {
                if (after.Status != before.Status)
                {
                    Moved(after);
                }
            }
        }

        return now;
    }

    private void Moved(Order now) =>
        logger.LogInformation(
            "The {OrderPurpose} of agent {AgentId} in {Symbol} is {OrderStatus}, as the order {ExchangeOrderId}",
            now.Purpose,
            now.AgentId,
            now.Symbol,
            now.Status,
            now.ExchangeOrderId);

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
    /// Has the credential of a user checked again, after the exchange answered a call as it
    /// answers a wrong credential: the check decides whether it is (FR-KEY-3).
    /// </summary>
    private async Task RecheckKeyAsync(Guid userId, TradingMode mode)
    {
        try
        {
            await keyChecks.RevalidateExchangeKeyAsync(userId, mode, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The Binance key of user {UserId} for {TradingMode} could not be checked again", userId, mode);
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

    /// <summary>
    /// Whether the protection of a position is the pair of the Spot market: a stop-loss and a
    /// take-profit of which the exchange ends one when the other is triggered (FR-BIN-S2). On
    /// the Futures market the two are orders of their own, and for a position that names no
    /// take-profit it is the stop-loss alone.
    /// </summary>
    private static bool Pairs(Market market, decimal? takeProfit) => market == Market.Spot && takeProfit is not null;

    /// <summary>
    /// Whether the take-profit of a position is an order of its own, which rests beside the
    /// stop-loss: on the Futures market, which has no pair (FR-BIN-F3).
    /// </summary>
    private static bool TakesProfitAlone(Market market, decimal? takeProfit) => market == Market.Futures && takeProfit is not null;

    /// <summary>
    /// Whether an open position lacks protection that can be placed now, from the orders of its
    /// agent that are not over: it has no stop-loss, and nothing is under way but a take-profit
    /// of its own, or its stop-loss rests and the take-profit that is an order of its own is
    /// missing. While any other order of the agent is not over, nothing is placed beside it.
    /// </summary>
    private static bool Unprotected(Position position, IReadOnlyList<Order> live)
    {
        if (live.Any(order => order.PairId is not null
            || order.PositionId != position.Id
            || order.Purpose is not (OrderPurpose.StopLoss or OrderPurpose.TakeProfit)))
        {
            return false;
        }

        var takeProfit = live.Any(order => order.Purpose == OrderPurpose.TakeProfit);

        return live.FirstOrDefault(order => order.Purpose == OrderPurpose.StopLoss) is not { } stopLoss
            || (stopLoss.Status == OrderStatus.Open && !takeProfit && TakesProfitAlone(position.Market, position.TakeProfit));
    }

    /// <summary>
    /// Whether an order can be for a quantity at a price: the exchange takes none below the
    /// least quantity of the symbol, or below its least value.
    /// </summary>
    private static bool CanBeOrdered(ExchangeSymbol symbol, decimal quantity, decimal price) =>
        quantity > 0m && quantity >= symbol.MinQuantity && quantity * price >= symbol.MinValue;

    /// <summary>An amount as a notification says it: in digits, with a point, and no more decimal places than it has.</summary>
    private static string Written(decimal amount) => amount.ToString("0.########", CultureInfo.InvariantCulture);

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

    private static ExchangeOrderType TypeOf(Order order) => order.Type switch
    {
        OrderType.Market => ExchangeOrderType.Market,

        // A take-profit that is an order of its own is triggered on the side of a profit. One
        // of a pair is an order of the list that the exchange holds, and is asked about as one.
        OrderType.Stop when order is { Purpose: OrderPurpose.TakeProfit, PairId: null } => ExchangeOrderType.TakeProfit,
        OrderType.Stop => ExchangeOrderType.Stop,
        OrderType.Limit => ExchangeOrderType.Limit,
        _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, "The exchange has no such order."),
    };

    private static Fill RowOf(Order order, ExchangeFill fill) => new(
        Guid.CreateVersion7(), order.Id, order.UserId, fill.TradeId, fill.Price, fill.Quantity, fill.Fee, fill.FeeAsset, fill.At);

    /// <summary>The prices of the protection of a position: its stop-loss, and its take-profit when it names one.</summary>
    private readonly record struct Protection(decimal StopLoss, decimal? TakeProfit);

    /// <summary>What became of placing an order that protects a position.</summary>
    private enum Placed
    {
        /// <summary>It rests on the exchange, or the position needs it no more.</summary>
        Rests,

        /// <summary>It is left to what settles the orders.</summary>
        Later,

        /// <summary>It cannot be placed: the exchange refused it, or it was not placed at the second try.</summary>
        Failed,
    }

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
