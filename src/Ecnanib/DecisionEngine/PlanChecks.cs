using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Has the Risk Guard check what goes on of the plan of a cycle, after the Risk Manager and
/// before any order (FR-RSK-3; SRS 3.2, step 6): it gathers the facts that the rules are asked
/// of, asks <see cref="GuardCheck"/>, and keeps the outcome of every rule for the record of the
/// cycle (FR-RSK-9), with the size that the check computed for the order (FR-RSK-5). See
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Risk Guard is rules only, so the facts are gathered here,</b> as the plan is checked,
/// and not taken from the data package, which is as old as the calls of the team: the price that
/// the symbol last traded at and the rules of the symbol, from the exchange, what the agent
/// holds now, whether the Binance key of its user lets it trade on its market, whether a global
/// halt is on, and how long ago the candle closed. No fact comes from what a model answered but
/// the plan itself, which was validated.
/// </para>
/// <para>
/// <b>It says no size.</b> The Risk Guard computes the quantity and the leverage of the order
/// from the risk fraction of the plan and from the facts, as it checks the plan, and its result
/// says them: a case has no place for either.
/// </para>
/// <para>
/// <b>It calls no model,</b> and places nothing: what it accepted is handed to what places the
/// orders, by the cycle. What it reads of the exchange is market data, which every agent shares.
/// </para>
/// <para>
/// <b>Of a reverse that is rejected, the close alone is checked again, and goes on when it is
/// accepted.</b> A reverse closes the open position and opens one on the opposite side. What it
/// opens is what was rejected: the close reduces risk, as it does of a reverse whose decision is
/// stale (FR-CYC-9), and open positions are still managed while a global halt is on (FR-OPS-3).
/// </para>
/// </remarks>
internal sealed class PlanChecks(
    IExchangeMarketData market,
    IEnumerable<IAgentHoldings> holdings,
    IKeyValidation keys,
    IEnumerable<IGlobalHalt> halts,
    TradingSettings settings,
    TimeProvider clock,
    ILogger<PlanChecks> logger)
{
    // What keeps positions and orders, when something does. The one that was registered last
    // answers, as a service is found.
    private readonly IAgentHoldings? _holdings = holdings.LastOrDefault();

    // What keeps the global halt, when something does. The halt is on when any of them says so.
    private readonly IReadOnlyList<IGlobalHalt> _halts = [.. halts];

    /// <summary>
    /// Checks what goes on of the plan of a cycle, and keeps each check for the record of the
    /// cycle.
    /// </summary>
    /// <param name="cycle">The cycle.</param>
    /// <param name="goesOn">What the team decided goes on: a plan that does not hold.</param>
    /// <param name="kept">What the cycle keeps for its record.</param>
    /// <param name="cancellationToken">Canceled when the cycle is stopped.</param>
    /// <returns>
    /// What the Risk Guard accepted, which is what goes on to the orders: the check of the plan,
    /// or of the close alone of a reverse that was rejected. Null when nothing goes on.
    /// </returns>
    public async Task<GuardResult?> CheckAsync(CycleStart cycle, TradePlan goesOn, CycleCalls kept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(goesOn);
        ArgumentNullException.ThrowIfNull(kept);

        var facts = await FactsAsync(cycle, cancellationToken);
        var result = Check(cycle, goesOn, facts, kept, sequence: 1);

        if (!result.IsAccepted && goesOn.Action == PlanAction.Reverse)
        {
            // What the reverse opens is rejected. Its close reduces risk, and is checked as a
            // plan to close is: on the same facts, which were read a moment ago.
            result = Check(cycle, goesOn.ClosingOnly(), facts, kept, sequence: 2);
        }

        return result.IsAccepted ? result : null;
    }

    /// <summary>Checks one plan on the facts, keeps the check, and says what became of it.</summary>
    private GuardResult Check(CycleStart cycle, TradePlan plan, Facts facts, CycleCalls kept, int sequence)
    {
        var result = GuardCheck.Of(CaseOf(cycle, plan, facts));

        kept.Add(RiskCheckRecord.Of(cycle.CycleId, cycle.Agent.UserId, sequence, plan.Action, facts.CheckedAt, result));

        if (result.IsAccepted)
        {
            logger.LogInformation(
                "The Risk Guard accepted the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}",
                cycle.Agent.Id,
                plan.Action,
                cycle.ClosedAt);
        }
        else
        {
            // A rejection is a decision, and no failure. It names the rules and what became of
            // each, and never a price or a number of the plan.
            logger.LogInformation(
                "The Risk Guard rejected the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}: {GuardRules}",
                cycle.Agent.Id,
                plan.Action,
                cycle.ClosedAt,
                string.Join(", ", result.InTheWay.Select(rule => $"{rule.Rule}: {rule.Outcome}")));
        }

        return result;
    }

    /// <summary>The facts that the rules are asked of, as they are now. All are asked for at once.</summary>
    private async Task<Facts> FactsAsync(CycleStart cycle, CancellationToken cancellationToken)
    {
        var agent = cycle.Agent;

        // The gateway shares each read among the agents that need it (FR-BIN-3): the price
        // counts for a second, and the rules of the symbols for an hour.
        var pricing = market.GetPriceAsync(agent.Market, agent.Mode, agent.Symbol, cancellationToken);
        var listing = market.GetSymbolsAsync(agent.Market, agent.Mode, cancellationToken);

        // What the agent holds may have changed while its team was called: a stop-loss that is
        // hit closes the position. So it is asked again, and not read from the data package.
        var holding = _holdings is null
            ? Task.FromResult(AgentHoldings.NothingYet(agent))
            : _holdings.OfAsync(agent, cancellationToken);

        // Answered from what the last check of the key recorded: Binance is asked nothing.
        var blocking = keys.BlockForAsync(agent.UserId, agent.Mode, agent.Market, cancellationToken);
        var halting = HaltedAsync(cancellationToken);

        await Task.WhenAll(pricing, listing, holding, blocking, halting);

        var price = await pricing;
        var symbols = await listing;

        if (price.Outcome != ExchangeCallOutcome.Succeeded)
        {
            Missing(cycle, "the price", price.Outcome);
        }

        if (symbols.Outcome != ExchangeCallOutcome.Succeeded)
        {
            Missing(cycle, "the rules of the symbols", symbols.Outcome);
        }

        var known = symbols.Outcome == ExchangeCallOutcome.Succeeded;
        var symbol = symbols.Symbols.FirstOrDefault(listed => listed.Symbol == agent.Symbol);

        return new Facts(
            price.Price,
            known,
            known && symbol is not null
                ? new SymbolRules(
                    symbol.Trading, symbol.PriceStep, symbol.QuantityStep, symbol.MinQuantity, symbol.MaxQuantity, symbol.MinValue)
                : null,
            await holding,
            await blocking,
            await halting,

            // When the facts had been read: a decision that was in time when the team decided,
            // and is stale by now, opens no position (FR-CYC-9).
            clock.GetUtcNow());
    }

    private async Task<bool> HaltedAsync(CancellationToken cancellationToken)
    {
        foreach (var halt in _halts)
        {
            if (await halt.IsOnAsync(cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private void Missing(CycleStart cycle, string missing, ExchangeCallOutcome outcome) =>
        logger.LogWarning(
            "The exchange did not give {Missing} for the Risk Guard to check the plan of agent {AgentId} for the close at {ClosedAt} ({ExchangeCallOutcome})",
            missing,
            cycle.Agent.Id,
            cycle.ClosedAt,
            outcome);

    /// <summary>A plan and the facts, in the words of the Risk Guard, which may use nothing of the Decision Engine.</summary>
    private GuardCase CaseOf(CycleStart cycle, TradePlan plan, Facts facts)
    {
        var agent = cycle.Agent;

        return new GuardCase(
            plan.Action switch
            {
                PlanAction.OpenLong => GuardedAction.OpenLong,
                PlanAction.OpenShort => GuardedAction.OpenShort,
                PlanAction.Reverse => GuardedAction.Reverse,
                PlanAction.Close => GuardedAction.Close,
                PlanAction.AdjustProtection => GuardedAction.AdjustProtection,
                _ => throw new ArgumentException(
                    $"A plan to {plan.Action} changes nothing, and nothing of it goes on to the Risk Guard.", nameof(plan)),
            },
            agent.Market,
            plan.EntryPrice,
            plan.StopLoss,
            plan.TakeProfit,

            // The risk fraction as the Risk Manager left it: the Risk Guard computes the size
            // of the order from it, and no model names a quantity or a leverage (FR-RSK-5).
            plan.RiskFraction,
            facts.Price,
            facts.SymbolsKnown,
            facts.Symbol,

            // The settings of the agent as they were stored when the turn of the cycle came: a
            // change applies from the agent's next cycle (FR-AGT-8).
            new GuardLimits(
                agent.RiskPerTradePercent,
                agent.MinRewardToRisk,
                agent.MaxEntryDistancePercent,
                agent.LeverageCap,
                agent.LiquidationBuffer,
                agent.DailyLossPercent,
                agent.DrawdownPercent),
            facts.Holds.Position is { } held
                ? new GuardedPosition(held.Side == PositionSide.Long ? TradeSide.Long : TradeSide.Short, held.Quantity, held.StopLoss)
                : null,
            facts.Holds.Equity,
            facts.Holds.EquityAtDayStart,
            facts.Holds.PeakEquity,
            SinceClose: facts.CheckedAt - cycle.ClosedAt,
            StaleLimit: settings.StaleSeconds.Of(agent.Timeframe),

            // The mode is permitted with a valid key for the environment, and the market when
            // that key may trade on it (FR-KEY-5). With no valid key, neither is.
            ModePermitted: facts.Block is not (StartBlock.NoKey or StartBlock.NotValid),
            MarketPermitted: facts.Block is null,
            GlobalHalt: facts.Halted);
    }

    /// <summary>The facts of the checks of one cycle.</summary>
    /// <param name="Price">The price that the symbol last traded at, or null when the exchange did not say.</param>
    /// <param name="SymbolsKnown">Whether the exchange said which symbols the market has.</param>
    /// <param name="Symbol">The rules of the agent's symbol, or null when the market lacks it or the exchange did not say.</param>
    /// <param name="Holds">What the agent holds now.</param>
    /// <param name="Block">What about the Binance key of the agent's user keeps it from trading on its market, or null when nothing does.</param>
    /// <param name="Halted">Whether a global halt is on.</param>
    /// <param name="CheckedAt">When the facts had been read.</param>
    private sealed record Facts(
        decimal? Price,
        bool SymbolsKnown,
        SymbolRules? Symbol,
        AgentHoldings Holds,
        StartBlock? Block,
        bool Halted,
        DateTimeOffset CheckedAt);
}
