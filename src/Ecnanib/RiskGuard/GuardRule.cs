namespace Ecnanib.RiskGuard;

/// <summary>
/// The fixed rules of the Risk Guard (FR-RSK-4, FR-RSK-6), each named for what must hold. A
/// member is stored by its name, with the outcome of a check, so renaming one changes stored
/// data and needs a migration. See "The rules" in docs/risk-guard.md.
/// </summary>
public enum GuardRule
{
    /// <summary>A stop-loss is present, and on the correct side of the entry: below it for a long, above it for a short.</summary>
    StopLossSide,

    /// <summary>The reward-to-risk ratio at the entry meets the minimum of the agent.</summary>
    RewardToRisk,

    /// <summary>The entry price is within the allowed distance of the current price.</summary>
    EntryDistance,

    /// <summary>Every price that the plan names is a multiple of the price step of the symbol.</summary>
    PriceStep,

    /// <summary>The quantity of every order is a multiple of the quantity step of the symbol.</summary>
    QuantityStep,

    /// <summary>The quantity of the entry is at least the least quantity of an order in the symbol.</summary>
    MinimumQuantity,

    /// <summary>The value of the entry, its price times its quantity, is at least the least value of an order in the symbol.</summary>
    MinimumValue,

    /// <summary>
    /// The plan goes with what the agent holds now: a plan that opens a position needs none, for
    /// an agent has one at most (<see cref="FixedLimits.OpenPositionsPerAgent"/>), and a plan
    /// that reverses, closes, or adjusts protection needs one.
    /// </summary>
    HeldPosition,

    /// <summary>An adjustment moves the stop-loss only in the direction that reduces risk (FR-CYC-6).</summary>
    ProtectionTightens,

    /// <summary>The loss of the agent in the UTC day has not reached its daily loss limit.</summary>
    DailyLossLimit,

    /// <summary>The fall of the agent's equity from its peak has not reached its drawdown limit.</summary>
    DrawdownLimit,

    /// <summary>The decision is not stale: it is checked within the stale limit of the agent's timeframe after the close (FR-CYC-9).</summary>
    NotStale,

    /// <summary>The mode of the agent is permitted: its user has a valid Binance key for the environment.</summary>
    ModePermitted,

    /// <summary>The market of the agent is permitted: that key may trade on it.</summary>
    MarketPermitted,

    /// <summary>The symbol of the agent is permitted: its market has it in the agent's environment, and the exchange takes orders in it now.</summary>
    SymbolPermitted,

    /// <summary>No global halt is on (FR-OPS-3).</summary>
    NoGlobalHalt,

    /// <summary>
    /// On Futures, the estimated liquidation price is further from the entry than the stop-loss
    /// by at least the liquidation buffer of the agent (FR-RSK-6).
    /// </summary>
    LiquidationBuffer,
}

/// <summary>What a check made of one rule (FR-RSK-9). A member is stored by its name.</summary>
public enum RuleOutcome
{
    /// <summary>The rule holds.</summary>
    Passed,

    /// <summary>The rule does not hold.</summary>
    Failed,

    /// <summary>
    /// Whether the rule holds is not known: something that it needs was not there, such as a
    /// price that the exchange did not give, or the size of an order that could not be computed
    /// without one. A plan that opens a position is not accepted on it.
    /// </summary>
    Unknown,

    /// <summary>The plan is not held to the rule: a plan that only closes a position or adjusts protection is held to few.</summary>
    NotApplied,
}

/// <summary>What the Risk Guard made of a plan. A member is stored by its name.</summary>
public enum GuardVerdict
{
    /// <summary>The plan goes on to the orders.</summary>
    Accepted,

    /// <summary>The plan does not go on.</summary>
    Rejected,
}

/// <summary>The outcome of one rule in one check, with the numbers that say why (FR-RSK-9).</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Outcome">What the check made of it.</param>
/// <param name="Measured">
/// What the rule measured, where it measures something: a ratio, a distance in percent, the
/// price or the quantity that is off its step. Null where it measures nothing, or could not.
/// See the table of the rules in docs/risk-guard.md for what it is for each.
/// </param>
/// <param name="Threshold">What the measure was held to: a limit of the agent, a step, or a price. Null where there is none.</param>
public sealed record RuleResult(GuardRule Rule, RuleOutcome Outcome, decimal? Measured, decimal? Threshold);
