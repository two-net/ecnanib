using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.Tests.RiskGuard;

/// <summary>
/// The plans of the tests of the Risk Guard, each with facts that every rule holds for. A test
/// changes the one fact that it is about, with <c>with</c>, so what it shows is that fact alone.
/// </summary>
internal static class GuardCases
{
    /// <summary>
    /// The rules of the symbol of the tests: a price step of 0.1, a quantity step of 0.001, an
    /// order of 0.001 at least and 1000 at most, and a least value of 5.
    /// </summary>
    public static readonly SymbolRules Symbol = new(
        Trading: true, PriceStep: 0.1m, QuantityStep: 0.001m, MinQuantity: 0.001m, MaxQuantity: 1000m, MinValue: 5m);

    /// <summary>The risk settings of the agent of the tests: the defaults of the specification (SRS Appendix B).</summary>
    public static readonly GuardLimits Limits = new(
        RiskPerTradePercent: 2m,
        MinRewardToRisk: 1.5m,
        MaxEntryDistancePercent: 1m,
        LeverageCap: 5,
        LiquidationBuffer: 2m,
        DailyLossPercent: 6m,
        DrawdownPercent: 25m);

    /// <summary>A long position, with its stop-loss resting below its entry.</summary>
    public static readonly GuardedPosition Long = new(TradeSide.Long, Quantity: 0.01m, StopLoss: 59500m);

    /// <summary>A short position, with its stop-loss resting above its entry.</summary>
    public static readonly GuardedPosition Short = new(TradeSide.Short, Quantity: 0.01m, StopLoss: 61000m);

    /// <summary>
    /// A plan to open a long position on Spot at the market, which every rule holds for: the
    /// price is 60000, the stop-loss 1000 below it, and the take-profit 2000 above it, which is
    /// a reward-to-risk ratio of 2. It risks 1 percent of an equity of 1000, which is 10, over
    /// a distance of 1000: so the order comes to 0.01, worth 600. The agent holds nothing, has
    /// lost nothing, and is checked half a minute after the close.
    /// </summary>
    public static readonly GuardCase OpenLong = new(
        GuardedAction.OpenLong,
        Market.Spot,
        EntryPrice: null,
        StopLoss: 59000m,
        TakeProfit: 62000m,
        RiskFraction: 0.01m,
        CurrentPrice: 60000m,
        SymbolsKnown: true,
        Symbol,
        Limits,
        Position: null,
        Equity: 1000m,
        EquityAtDayStart: 1000m,
        PeakEquity: 1000m,
        SinceClose: TimeSpan.FromSeconds(30),
        StaleLimit: TimeSpan.FromSeconds(120),
        ModePermitted: true,
        MarketPermitted: true,
        GlobalHalt: false);

    /// <summary>
    /// The same on Futures, risking 2 percent: the order comes to 0.02, worth 1200, which takes
    /// a leverage of 2 to fit within the equity. The liquidation is 30 times as far from the
    /// entry as the stop-loss.
    /// </summary>
    public static readonly GuardCase OpenLongOnFutures = OpenLong with { Market = Market.Futures, RiskFraction = 0.02m };

    /// <summary>A plan to open a short position on Futures at the market: the stop-loss 1000 above the price, and the take-profit 2000 below it.</summary>
    public static readonly GuardCase OpenShort = OpenLongOnFutures with { Action = GuardedAction.OpenShort, StopLoss = 61000m, TakeProfit = 58000m };

    /// <summary>A plan to reverse a long position on Futures: it closes the long, and opens a short.</summary>
    public static readonly GuardCase Reverse = OpenShort with { Action = GuardedAction.Reverse, Position = Long };

    /// <summary>A plan to close the long position that the agent holds.</summary>
    public static readonly GuardCase Close = OpenLong with
    {
        Action = GuardedAction.Close, StopLoss = null, TakeProfit = null, RiskFraction = null, Position = Long,
    };

    /// <summary>A plan to move the stop-loss of the long position that the agent holds up, which reduces its risk.</summary>
    public static readonly GuardCase Adjust = Close with { Action = GuardedAction.AdjustProtection, StopLoss = 59800m };

    /// <summary>Every plan that opens a position, on each market that allows it.</summary>
    public static readonly IReadOnlyList<GuardCase> ThatOpen = [OpenLong, OpenLongOnFutures, OpenShort, Reverse];

    /// <summary>Every plan that only closes a position or adjusts protection.</summary>
    public static readonly IReadOnlyList<GuardCase> ThatReduceRisk = [Close, Adjust];

    /// <summary>The rules that a plan which only closes a position or adjusts protection is held to (FR-RSK-4).</summary>
    public static readonly IReadOnlyList<GuardRule> AskedOfWhatReducesRisk =
        [GuardRule.PriceStep, GuardRule.QuantityStep, GuardRule.HeldPosition, GuardRule.ProtectionTightens];

    /// <summary>What a check made of a rule.</summary>
    public static RuleOutcome OutcomeOf(this GuardCase @case, GuardRule rule) => GuardCheck.Of(@case).Of(rule).Outcome;

    /// <summary>The rules that stand in the way of a plan, by their names.</summary>
    public static IReadOnlyList<GuardRule> InTheWayOf(this GuardCase @case) => [.. GuardCheck.Of(@case).InTheWay.Select(rule => rule.Rule)];
}

/// <summary>The checks of the Risk Guard that a test needs a cycle to have, without a cycle that runs.</summary>
internal static class GuardedCycles
{
    /// <summary>
    /// The check of a plan to open a long position that the Risk Guard accepted, as it is stored
    /// with its cycle: with the outcome of every rule.
    /// </summary>
    public static RiskCheckRecord Check(Guid cycleId, Guid userId, DateTimeOffset checkedAt) =>
        Check(cycleId, userId, checkedAt, GuardCases.OpenLong, PlanAction.OpenLong);

    /// <summary>The check of a plan, as it is stored with its cycle.</summary>
    public static RiskCheckRecord Check(
        Guid cycleId, Guid userId, DateTimeOffset checkedAt, GuardCase @case, PlanAction action, int sequence = 1) =>
        RiskCheckRecord.Of(cycleId, userId, sequence, action, checkedAt, GuardCheck.Of(@case));
}

/// <summary>
/// Stands for something that keeps a global halt (FR-OPS-3): it says what the test set, and how
/// often it was asked. It is registered beside what an operator switches, which Agents keeps and
/// which is off unless a test switches it: the halt is on when either says so.
/// </summary>
internal sealed class StandInHalt(bool on = false) : IGlobalHalt
{
    private int _asked;

    /// <summary>Whether the halt is on.</summary>
    public bool On { get; set; } = on;

    /// <summary>How often it was asked.</summary>
    public int Asked => Volatile.Read(ref _asked);

    public Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asked);

        return Task.FromResult(On);
    }
}

/// <summary>What has the Risk Guard check the plan of a cycle writes to the log, and the area it writes under.</summary>
internal static class PlanChecksLog
{
    public const string Area = "Ecnanib.DecisionEngine.PlanChecks";

    public const string Accepted = "The Risk Guard accepted the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}";
    public const string Rejected =
        "The Risk Guard rejected the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}: {GuardRules}";
    public const string Missing =
        "The exchange did not give {Missing} for the Risk Guard to check the plan of agent {AgentId} for the close at {ClosedAt} ({ExchangeCallOutcome})";
}
