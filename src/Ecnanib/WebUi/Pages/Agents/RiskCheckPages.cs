using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// What the page of the risk checks of an agent says, in one place: what the Risk Guard is, what
/// became of a plan, and for every rule what it asks and why it held or did not, with the
/// numbers (FR-RSK-9). Every rule and every rejection is explained in plain language
/// (NFR-USA-2). See "The page" in docs/risk-guard.md.
/// </summary>
public static class RiskCheckPages
{
    /// <summary>What leads to the page, from the other pages of the agent.</summary>
    public const string Link = "Risk checks of the agent";

    /// <summary>What the Risk Guard is, in plain language (FR-RSK-3).</summary>
    public const string Explained =
        "Before any order, what the agent's team decided is checked against fixed rules: the Risk Guard. The rules are no AI. " +
        "They are the same in every cycle, and nothing switches them off: no answer of a model, and no setting. A plan that " +
        "would open a position goes on only when every rule holds.";

    /// <summary>Why a plan that reduces risk is held to fewer rules (FR-RSK-4).</summary>
    public const string ReducingRiskExplained =
        "A plan that only closes the position or adjusts its protection is held to the rules of the exchange and to protection " +
        "that only tightens, and to no other rule, so that risk can always be reduced.";

    /// <summary>What the page says of an agent that has no check.</summary>
    public const string NoneYet =
        "No plan of this agent has been checked yet. A plan is checked when the agent's team decides on something other than " +
        "holding, and the Risk Manager did not veto it.";

    /// <summary>What a plan that was checked does, in words.</summary>
    public static string Describe(PlanAction action) => action switch
    {
        PlanAction.OpenLong => "Open a long position",
        PlanAction.OpenShort => "Open a short position",
        PlanAction.Reverse => "Reverse the position",
        PlanAction.Close => "Close the position",
        PlanAction.AdjustProtection => "Adjust the protection",
        PlanAction.Hold => "Hold",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action."),
    };

    /// <summary>The verdict of a check, as a word.</summary>
    public static string Describe(GuardVerdict verdict) => verdict switch
    {
        GuardVerdict.Accepted => "Accepted",
        GuardVerdict.Rejected => "Rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "No such verdict."),
    };

    /// <summary>What the verdict of a check means for its plan.</summary>
    public static string Explain(RiskCheckView check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var opens = check.Action is PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse;

        return (check.Verdict, check.Action, check.Sequence) switch
        {
            (GuardVerdict.Accepted, PlanAction.Close, > 1) =>
                "This is the close of a plan to reverse, of which what it would open was rejected. Closing reduces risk, " +
                "so the close goes on by itself.",
            (GuardVerdict.Accepted, _, _) => "Every rule that this plan is held to holds, so it goes on to the orders.",
            (GuardVerdict.Rejected, PlanAction.Reverse, _) =>
                "The plan does not go on as it is: a rule does not hold, or is not known to hold. What it would open is " +
                "rejected, and its close is checked by itself in the next check.",
            (GuardVerdict.Rejected, _, _) when opens =>
                "The plan does not go on: a rule does not hold, or is not known to hold. No position is opened.",
            _ => "The plan does not go on: a rule that it is held to does not hold.",
        };
    }

    /// <summary>The outcome of a rule, as words: it never relies on a colour.</summary>
    public static string Describe(RuleOutcome outcome) => outcome switch
    {
        RuleOutcome.Passed => "Holds",
        RuleOutcome.Failed => "Does not hold",
        RuleOutcome.Unknown => "Not known",
        RuleOutcome.NotApplied => "Not asked",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such outcome."),
    };

    /// <summary>The name of a rule, as a user reads it.</summary>
    public static string NameOf(GuardRule rule) => rule switch
    {
        GuardRule.StopLossSide => "Stop-loss on the correct side",
        GuardRule.RewardToRisk => "Reward-to-risk ratio",
        GuardRule.EntryDistance => "Distance of the entry",
        GuardRule.PriceStep => "Price step",
        GuardRule.QuantityStep => "Quantity step",
        GuardRule.MinimumValue => "Least value of an order",
        GuardRule.HeldPosition => "Open position",
        GuardRule.ProtectionTightens => "Protection only tightens",
        GuardRule.DailyLossLimit => "Daily loss limit",
        GuardRule.DrawdownLimit => "Drawdown limit",
        GuardRule.NotStale => "Stale limit",
        GuardRule.ModePermitted => "Mode",
        GuardRule.MarketPermitted => "Market",
        GuardRule.SymbolPermitted => "Symbol",
        GuardRule.NoGlobalHalt => "Global halt",
        GuardRule.LiquidationBuffer => "Liquidation buffer",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No such rule."),
    };

    /// <summary>Why a rule held or did not, in plain language, with the numbers that the check stored.</summary>
    /// <param name="result">The outcome of the rule.</param>
    /// <param name="action">What the plan that was checked does.</param>
    public static string Says(RuleResult result, PlanAction action)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Outcome == RuleOutcome.NotApplied)
        {
            return NotAsked(result.Rule, action);
        }

        // What decides which sentence is said is whether a number was stored. What a sentence
        // writes is the number, or that it was not kept: a check stores both where a sentence
        // names both, so the words for a number that is not there are for a row that nothing
        // of the application wrote.
        var hasMeasure = result.Measured is not null;
        var hasThreshold = result.Threshold is not null;
        var measured = result.Measured is { } number ? AgentPages.Number(number) : NotKept;
        var threshold = result.Threshold is { } bound ? AgentPages.Number(bound) : NotKept;

        return (result.Rule, result.Outcome) switch
        {
            (GuardRule.StopLossSide, RuleOutcome.Passed) =>
                $"The stop-loss at {measured} is on the losing side of the entry at {threshold}.",
            (GuardRule.StopLossSide, RuleOutcome.Failed) when !hasMeasure => "The plan has no stop-loss.",
            (GuardRule.StopLossSide, RuleOutcome.Failed) when !hasThreshold => "The entry price of the plan is no price.",
            (GuardRule.StopLossSide, RuleOutcome.Failed) =>
                $"The stop-loss at {measured} is not on the losing side of the entry at {threshold}: it stands below the entry " +
                "of a long position, and above the entry of a short one.",
            (GuardRule.StopLossSide, _) => "The entry is not known: the exchange did not say the current price, or the agent holds no position to reverse.",

            (GuardRule.RewardToRisk, RuleOutcome.Passed) =>
                $"The take-profit is {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}.",
            (GuardRule.RewardToRisk, RuleOutcome.Failed) when !hasMeasure =>
                $"The plan has no stop-loss and take-profit on the correct sides of its entry, so it has no ratio. The agent asks for at least {threshold}.",
            (GuardRule.RewardToRisk, RuleOutcome.Failed) =>
                $"The take-profit is only {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}.",
            (GuardRule.RewardToRisk, _) => "The entry is not known: the exchange did not say the current price, or the agent holds no position to reverse.",

            (GuardRule.EntryDistance, RuleOutcome.Passed) =>
                $"The entry is {measured} percent from the current price. The agent allows up to {threshold} percent.",
            (GuardRule.EntryDistance, RuleOutcome.Failed) when !hasMeasure => "The entry price of the plan is no price.",
            (GuardRule.EntryDistance, RuleOutcome.Failed) =>
                $"The entry is {measured} percent from the current price, which is more than the {threshold} percent that the agent allows.",
            (GuardRule.EntryDistance, _) => "The exchange did not say the current price.",

            (GuardRule.PriceStep, RuleOutcome.Passed) when !hasThreshold => "The plan names no price that the exchange holds to a step.",
            (GuardRule.PriceStep, RuleOutcome.Passed) => $"Every price of the plan is a multiple of the price step of the symbol, {threshold}.",
            (GuardRule.PriceStep, RuleOutcome.Failed) when !hasThreshold => $"The price {measured} of the plan is no price.",
            (GuardRule.PriceStep, RuleOutcome.Failed) => $"The price {measured} is not a multiple of the price step of the symbol, {threshold}.",
            (GuardRule.PriceStep, _) => "The exchange did not say the rules of the symbol.",

            (GuardRule.QuantityStep, RuleOutcome.Passed) when !hasThreshold => "The symbol has no quantity step.",
            (GuardRule.QuantityStep, RuleOutcome.Passed) => $"The quantity is a multiple of the quantity step of the symbol, {threshold}.",
            (GuardRule.QuantityStep, RuleOutcome.Failed) when !hasThreshold => $"The quantity {measured} is no quantity of an order.",
            (GuardRule.QuantityStep, RuleOutcome.Failed) => $"The quantity {measured} is not a multiple of the quantity step of the symbol, {threshold}.",
            (GuardRule.QuantityStep, _) when !hasThreshold => "The exchange did not say the rules of the symbol.",
            (GuardRule.QuantityStep, _) => "The quantity of the order is not known.",

            (GuardRule.MinimumValue, RuleOutcome.Passed) => $"The entry is worth {measured}. The least value of an order in the symbol is {threshold}.",
            (GuardRule.MinimumValue, RuleOutcome.Failed) when !hasMeasure => "The entry has no price or no quantity that makes sense.",
            (GuardRule.MinimumValue, RuleOutcome.Failed) =>
                $"The entry is worth {measured}, which is less than the least value of an order in the symbol, {threshold}.",
            (GuardRule.MinimumValue, _) when !hasThreshold => "The exchange did not say the rules of the symbol.",
            (GuardRule.MinimumValue, _) => "The value of the entry is not known: its quantity or its price is not known.",

            (GuardRule.HeldPosition, RuleOutcome.Passed) when hasThreshold => "The agent holds no position, so it can open one.",
            (GuardRule.HeldPosition, RuleOutcome.Passed) => "The agent holds the position that the plan acts on.",
            (GuardRule.HeldPosition, RuleOutcome.Failed) when hasThreshold =>
                $"The agent holds a position already, and has {threshold} at most at one time.",
            (GuardRule.HeldPosition, _) => "The agent holds no position now, so there is nothing to close, reverse, or protect.",

            (GuardRule.ProtectionTightens, RuleOutcome.Passed) when !hasMeasure => "The plan moves the take-profit alone, which moves in either direction.",
            (GuardRule.ProtectionTightens, RuleOutcome.Passed) when !hasThreshold =>
                "The position has no stop-loss resting, so any stop-loss reduces its risk.",
            (GuardRule.ProtectionTightens, RuleOutcome.Passed) => $"The new stop-loss at {measured} adds no risk to the one that rests at {threshold}.",
            (GuardRule.ProtectionTightens, RuleOutcome.Failed) =>
                $"The new stop-loss at {measured} would add risk to the one that rests at {threshold}. A stop-loss moves only in the direction that reduces risk.",
            (GuardRule.ProtectionTightens, _) => "The agent holds no position now, so there is no stop-loss to compare with.",

            (GuardRule.DailyLossLimit, RuleOutcome.Passed) =>
                $"The agent has lost {measured} percent of its equity in the UTC day. Its daily loss limit is {threshold} percent.",
            (GuardRule.DailyLossLimit, RuleOutcome.Failed) =>
                $"The agent has lost {measured} percent of its equity in the UTC day, which reaches its daily loss limit of {threshold} percent.",
            (GuardRule.DailyLossLimit, _) => "The equity of the agent as the day began is not known.",

            (GuardRule.DrawdownLimit, RuleOutcome.Passed) =>
                $"The equity of the agent is {measured} percent below its peak. Its drawdown limit is {threshold} percent.",
            (GuardRule.DrawdownLimit, RuleOutcome.Failed) =>
                $"The equity of the agent is {measured} percent below its peak, which reaches its drawdown limit of {threshold} percent.",
            (GuardRule.DrawdownLimit, _) => "The peak equity of the agent is not known.",

            (GuardRule.NotStale, RuleOutcome.Passed) =>
                $"The plan was checked {measured} seconds after the candle closed. The stale limit of the agent's timeframe is {threshold} seconds.",
            (GuardRule.NotStale, _) =>
                $"The plan was checked {measured} seconds after the candle closed, which is later than the stale limit of the agent's " +
                $"timeframe, {threshold} seconds. A decision that is stale opens no position.",

            (GuardRule.ModePermitted, RuleOutcome.Passed) => "You have a valid Binance key for the environment that the agent trades in.",
            (GuardRule.ModePermitted, _) => "You have no valid Binance key for the environment that the agent trades in.",

            (GuardRule.MarketPermitted, RuleOutcome.Passed) => "Your Binance key may trade on the market of the agent.",
            (GuardRule.MarketPermitted, _) => "You have no valid Binance key that may trade on the market of the agent.",

            (GuardRule.SymbolPermitted, RuleOutcome.Passed) => "The market has the symbol of the agent, and the exchange takes orders in it.",
            (GuardRule.SymbolPermitted, RuleOutcome.Failed) => "The market does not have the symbol of the agent now, or the exchange takes no orders in it.",
            (GuardRule.SymbolPermitted, _) => "The exchange did not say which symbols the market has.",

            (GuardRule.NoGlobalHalt, RuleOutcome.Passed) => "No global halt is on.",
            (GuardRule.NoGlobalHalt, _) =>
                "A global halt is on. While it is, no agent opens a new position. Open positions are still managed and protected.",

            (GuardRule.LiquidationBuffer, RuleOutcome.Passed) =>
                $"The estimated liquidation price is {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}.",
            (GuardRule.LiquidationBuffer, RuleOutcome.Failed) when !hasMeasure =>
                "The plan has no stop-loss on the correct side of its entry, or no leverage that makes sense.",
            (GuardRule.LiquidationBuffer, RuleOutcome.Failed) =>
                $"The estimated liquidation price is only {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}.",
            (GuardRule.LiquidationBuffer, _) => "The leverage of the order is not known, or its entry is not.",

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Rule, "No such rule."),
        };
    }

    /// <summary>What a sentence writes in the place of a number that a check did not store.</summary>
    private const string NotKept = "a number that was not kept";

    /// <summary>Why a plan is not held to a rule.</summary>
    private static string NotAsked(GuardRule rule, PlanAction action) => rule switch
    {
        GuardRule.ProtectionTightens => "Asked only of a plan that adjusts the protection.",
        GuardRule.LiquidationBuffer when action is PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse =>
            "Asked on Futures only: a position on Spot is not liquidated.",
        _ => "Not asked of a plan that only closes the position or adjusts its protection, so that risk can always be reduced.",
    };
}
