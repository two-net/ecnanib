using System.Globalization;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// The texts of the page of the risk checks of an agent, in one place (NFR-USA-4): what the Risk
/// Guard is, what became of a plan, and for every rule what it asks and why it held or did not,
/// with the numbers (FR-RSK-9). Every rule and every rejection is explained in plain language
/// (NFR-USA-2). See "The page" in docs/risk-guard.md.
/// </summary>
public static class RiskCheckPages
{
    /// <summary>What leads to the page, from the other pages of the agent.</summary>
    public static string Link => "Risk checks of the agent";

    /// <summary>The title and the heading of the page.</summary>
    public static string TitleOf(string agentName) => $"Risk checks of the agent {agentName}";

    /// <summary>What stands before the badge of the mode of the agent, under the heading of the page.</summary>
    public static string ModeLabel => "Mode:";

    /// <summary>What leads from the page to the state of the agent.</summary>
    public static string StateLink => "State of the agent";

    /// <summary>What leads from the page to the settings of the agent.</summary>
    public static string SettingsLink => "Settings of the agent";

    /// <summary>What leads from the page back to the list of the agents.</summary>
    public static string BackToAgents => "Back to your agents";

    /// <summary>What the page says above the checks when it does not show every check of the agent.</summary>
    public static string LatestListed(int listed, int total) =>
        $"The latest {listed.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} checks, latest first.";

    /// <summary>What the page says above the checks when it shows every check of the agent.</summary>
    public static string EveryListed => "Every check of the agent's plans, latest first.";

    /// <summary>What stands between the verdict of a check and the badge of the mode that it was made in.</summary>
    public static string MadeIn => "in";

    /// <summary>
    /// Which candle the cycle of a check was for, after the badge of its mode, and whether the
    /// cycle was a test: <c>for the candle that closed at 17 November 2026, 09:00 UTC, in a test cycle</c>.
    /// </summary>
    /// <param name="closedAt">When the candle closed, as the page writes a time.</param>
    /// <param name="kind">What started the cycle of the check.</param>
    public static string ForCandle(string closedAt, CycleKind kind) => kind == CycleKind.Test
        ? $"for the candle that closed at {closedAt}, in a test cycle"
        : $"for the candle that closed at {closedAt}";

    /// <summary>The name of the table of the rules of a check, which is read out and not shown.</summary>
    public static string RulesCaption => "The rules of the check";

    /// <summary>The heading of the column that names a rule.</summary>
    public static string RuleHeading => "Rule";

    /// <summary>The heading of the column that says whether a rule held.</summary>
    public static string OutcomeHeading => "Outcome";

    /// <summary>The heading of the column that says why a rule held or did not.</summary>
    public static string WhyHeading => "Why";

    /// <summary>What the Risk Guard is, in plain language (FR-RSK-3).</summary>
    public static string Explained =>
        "Before any order, what the agent's team decided is checked against fixed rules: the Risk Guard. The rules are no AI. " +
        "They are the same in every cycle, and nothing switches them off: no answer of a model, and no setting. A plan that " +
        "would open a position goes on only when every rule holds.";

    /// <summary>Why a plan that reduces risk is held to fewer rules (FR-RSK-4).</summary>
    public static string ReducingRiskExplained =>
        "A plan that only closes the position or adjusts its protection is held to the rules of the exchange and to protection " +
        "that only tightens, and to no other rule, so that risk can always be reduced.";

    /// <summary>What the page says of an agent that has no check.</summary>
    public static string NoneYet =>
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

        if (check is { Kind: CycleKind.Test, Verdict: GuardVerdict.Accepted })
        {
            // What the Risk Guard accepted of a test cycle goes on to nothing (FR-CYC-12).
            return check is { Action: PlanAction.Close, Sequence: > 1 }
                ? "This is the close of a plan to reverse, of which what it would open was rejected. Closing reduces risk, so " +
                  "the close would go on by itself. The check is of a test cycle, which places no order."
                : "Every rule that this plan is held to holds, so it would go on to the orders. The check is of a test cycle, " +
                  "which places no order.";
        }

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

    /// <summary>
    /// What the check computed for the order of a plan that opens a position: its quantity, and
    /// on Futures its leverage (FR-RSK-5). Null for a plan that opens nothing, which has no
    /// order of its own to size.
    /// </summary>
    public static string? Sized(RiskCheckView check)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (check.Action is not (PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse))
        {
            return null;
        }

        if (check.Quantity is not { } quantity || check.Leverage is not { } leverage)
        {
            return "No size was computed for the order: something that it follows from was not known.";
        }

        if (quantity <= 0m)
        {
            return "The order came to no quantity, so there is nothing to place: the plan has no stop-loss on the losing side of " +
                "its entry to measure its risk by, or the risk that was approved buys less than one step of the quantity.";
        }

        const string Follows = "follows from the risk that was approved, the agent's equity, and the distance from the entry to the stop-loss";

        // Whether the check was made on Futures is told from the check itself: the liquidation
        // buffer is asked there, and the market of the agent may have been changed since.
        var onFutures = check.Rules.Any(rule => rule.Rule == GuardRule.LiquidationBuffer && rule.Outcome != RuleOutcome.NotApplied);

        return onFutures
            ? $"The system computed the size of the order, and no model chose it: a quantity of {AgentPages.Number(quantity)}, at a " +
                $"leverage of {leverage}. The quantity {Follows}. The leverage is the lowest that fits the margin of the position " +
                "within the agent's equity, up to the agent's leverage cap."
            : $"The system computed the size of the order, and no model chose it: a quantity of {AgentPages.Number(quantity)}. " +
                $"It {Follows}, and the position is worth at most the agent's equity.";
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
        GuardRule.MinimumQuantity => "Least quantity of an order",
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
    /// <param name="kind">
    /// What started the cycle of the check: the stale limit of a test cycle counts from the
    /// moment that it was run, and not from the close of its candle (FR-CYC-12).
    /// </param>
    public static string Says(RuleResult result, PlanAction action, CycleKind kind = CycleKind.Scheduled)
    {
        ArgumentNullException.ThrowIfNull(result);

        // What the seconds of the stale limit are counted from.
        var since = kind == CycleKind.Test ? "the test cycle was run" : "the candle closed";

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
                $"The plan has no stop-loss and take-profit on the correct sides of its entry, so it has no ratio. The agent asks for at least {threshold}: {RewardToRiskSetting}",
            (GuardRule.RewardToRisk, RuleOutcome.Failed) =>
                $"The take-profit is only {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}: {RewardToRiskSetting}",
            (GuardRule.RewardToRisk, _) => "The entry is not known: the exchange did not say the current price, or the agent holds no position to reverse.",

            (GuardRule.EntryDistance, RuleOutcome.Passed) =>
                $"The entry is {measured} percent from the current price. The agent allows up to {threshold} percent.",
            (GuardRule.EntryDistance, RuleOutcome.Failed) when !hasMeasure => "The entry price of the plan is no price.",
            (GuardRule.EntryDistance, RuleOutcome.Failed) =>
                $"The entry is {measured} percent from the current price, which is more than the {threshold} percent that the agent " +
                "allows: its maximum entry distance, which is one of its risk settings. No order is placed for an entry that far away.",
            (GuardRule.EntryDistance, _) => "The exchange did not say the current price.",

            (GuardRule.PriceStep, RuleOutcome.Passed) when !hasThreshold => "The plan names no price that the exchange holds to a step.",
            (GuardRule.PriceStep, RuleOutcome.Passed) => $"Every price of the plan is a multiple of the price step of the symbol, {threshold}.",
            (GuardRule.PriceStep, RuleOutcome.Failed) when !hasThreshold => $"The price {measured} of the plan is no price.",
            (GuardRule.PriceStep, RuleOutcome.Failed) =>
                $"The price {measured} is not a multiple of the price step of the symbol, {threshold}. Binance sets the step, and takes " +
                "no order off it. The service does not round a price that the team planned, so the plan is rejected.",
            (GuardRule.PriceStep, _) => "The exchange did not say the rules of the symbol.",

            (GuardRule.QuantityStep, RuleOutcome.Passed) when !hasThreshold => "The symbol has no quantity step.",
            (GuardRule.QuantityStep, RuleOutcome.Passed) => $"The quantity is a multiple of the quantity step of the symbol, {threshold}.",
            (GuardRule.QuantityStep, RuleOutcome.Failed) when !hasThreshold || result.Measured is <= 0m =>
                $"The quantity {measured} is no quantity of an order.",
            (GuardRule.QuantityStep, RuleOutcome.Failed) =>
                $"The quantity {measured} is not a multiple of the quantity step of the symbol, {threshold}. Binance sets the step, and " +
                "takes no order off it.",
            (GuardRule.QuantityStep, _) when !hasThreshold => "The exchange did not say the rules of the symbol.",
            (GuardRule.QuantityStep, _) => "The quantity of the order is not known.",

            (GuardRule.MinimumQuantity, RuleOutcome.Passed) when result.Threshold is not > 0m =>
                $"The order is for {measured}. The symbol has no least quantity of an order.",
            (GuardRule.MinimumQuantity, RuleOutcome.Passed) =>
                $"The order is for {measured}. The least quantity of an order in the symbol is {threshold}.",
            (GuardRule.MinimumQuantity, RuleOutcome.Failed) when !hasMeasure => "The order came to no quantity above zero.",
            (GuardRule.MinimumQuantity, RuleOutcome.Failed) =>
                $"The order is for {measured}, which is less than the least quantity of an order in the symbol, {threshold}. {NeverRaised}",
            (GuardRule.MinimumQuantity, _) when !hasThreshold => "The exchange did not say the rules of the symbol.",
            (GuardRule.MinimumQuantity, _) => "The quantity of the order is not known.",

            (GuardRule.MinimumValue, RuleOutcome.Passed) => $"The entry is worth {measured}. The least value of an order in the symbol is {threshold}.",
            (GuardRule.MinimumValue, RuleOutcome.Failed) when !hasMeasure => "The entry has no price or no quantity that makes sense.",
            (GuardRule.MinimumValue, RuleOutcome.Failed) =>
                $"The entry is worth {measured}, which is less than the least value of an order in the symbol, {threshold}. {NeverRaised}",
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
                $"The agent has lost {measured} percent of its equity in the UTC day, which reaches its daily loss limit of {threshold} " +
                "percent, one of its risk settings. An agent that has reached it opens nothing: the service closes its position, " +
                "halts it, and resumes it when the next UTC day begins.",
            (GuardRule.DailyLossLimit, _) => "The equity of the agent as the day began is not known.",

            (GuardRule.DrawdownLimit, RuleOutcome.Passed) =>
                $"The equity of the agent is {measured} percent below its peak. Its drawdown limit is {threshold} percent.",
            (GuardRule.DrawdownLimit, RuleOutcome.Failed) =>
                $"The equity of the agent is {measured} percent below its peak, which reaches its drawdown limit of {threshold} " +
                "percent, one of its risk settings. An agent that has reached it opens nothing: the service closes its position " +
                "and halts it, until you resume it.",
            (GuardRule.DrawdownLimit, _) => "The peak equity of the agent is not known.",

            (GuardRule.NotStale, RuleOutcome.Passed) =>
                $"The plan was checked {measured} seconds after {since}. The stale limit of the agent's timeframe is {threshold} seconds.",
            (GuardRule.NotStale, _) =>
                $"The plan was checked {measured} seconds after {since}, which is later than the stale limit of the agent's " +
                $"timeframe, {threshold} seconds. A decision that is stale opens no position.",

            (GuardRule.ModePermitted, RuleOutcome.Passed) => "You have a valid Binance key for the environment that the agent trades in.",
            (GuardRule.ModePermitted, _) =>
                "You have no valid Binance key for the environment that the agent trades in. Add the key, or have it checked " +
                "again, on the page of your keys.",

            (GuardRule.MarketPermitted, RuleOutcome.Passed) => "Your Binance key may trade on the market of the agent.",
            (GuardRule.MarketPermitted, _) =>
                "You have no valid Binance key that may trade on the market of the agent: the key is missing or not valid, or " +
                "it lacks the permission to trade on that market. The page of your keys says which permissions it has.",

            (GuardRule.SymbolPermitted, RuleOutcome.Passed) => "The market has the symbol of the agent, and the exchange takes orders in it.",
            (GuardRule.SymbolPermitted, RuleOutcome.Failed) =>
                "The market does not have the symbol of the agent now, or the exchange takes no orders in it. An agent is given " +
                "another symbol on the page of its settings, while it is a draft or is stopped with nothing open.",
            (GuardRule.SymbolPermitted, _) => "The exchange did not say which symbols the market has.",

            (GuardRule.NoGlobalHalt, RuleOutcome.Passed) => "No global halt is on.",
            (GuardRule.NoGlobalHalt, _) =>
                "A global halt is on. While it is, no agent opens a new position. Open positions are still managed and protected.",

            (GuardRule.LiquidationBuffer, RuleOutcome.Passed) =>
                $"The estimated liquidation price is {measured} times as far from the entry as the stop-loss. The agent asks for at least {threshold}.",
            (GuardRule.LiquidationBuffer, RuleOutcome.Failed) when !hasMeasure =>
                "The plan has no stop-loss on the correct side of its entry, or its entry price is no price.",
            (GuardRule.LiquidationBuffer, RuleOutcome.Failed) =>
                $"The estimated liquidation price is only {measured} times as far from the entry as the stop-loss. The agent asks for " +
                $"at least {threshold}: its liquidation buffer, which is one of its risk settings. It keeps the stop-loss well " +
                "before the price at which Binance would liquidate the position.",
            (GuardRule.LiquidationBuffer, _) => "The leverage of the order is not known, or its entry is not.",

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Rule, "No such rule."),
        };
    }

    /// <summary>What a sentence writes in the place of a number that a check did not store.</summary>
    private const string NotKept = "a number that was not kept";

    /// <summary>
    /// Whose the least reward-to-risk ratio is, at the end of a sentence that says what the
    /// agent asks for: a rejection says whose limit a plan did not meet (NFR-USA-2).
    /// </summary>
    private const string RewardToRiskSetting = "its minimum reward-to-risk ratio, which is one of its risk settings.";

    /// <summary>
    /// Whose the least quantity and the least value of an order are, and why an order that is
    /// below one of them is rejected and not made larger (FR-RSK-5).
    /// </summary>
    private const string NeverRaised =
        "Binance sets it. The service does not raise an order to it, because a larger order would risk more than was approved.";

    /// <summary>Why a plan is not held to a rule.</summary>
    private static string NotAsked(GuardRule rule, PlanAction action) => rule switch
    {
        GuardRule.ProtectionTightens => "Asked only of a plan that adjusts the protection.",
        GuardRule.LiquidationBuffer when action is PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse =>
            "Asked on Futures only: a position on Spot is not liquidated.",
        _ => "Not asked of a plan that only closes the position or adjusts its protection, so that risk can always be reduced.",
    };
}
