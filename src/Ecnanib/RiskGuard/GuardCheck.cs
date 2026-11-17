using Ecnanib.Shared;

namespace Ecnanib.RiskGuard;

/// <summary>
/// The Risk Guard: fixed rules that every plan is checked against after the Risk Manager and
/// before any order (FR-RSK-3). It is rules only: it is handed the facts, reads nothing, calls no
/// model, and stores nothing. See docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plan that opens a position is rejected unless every rule holds</b> (FR-RSK-4, FR-RSK-6):
/// a stop-loss on the correct side of the entry, the reward-to-risk ratio, the distance of the
/// entry from the current price, the rules of the exchange for the price step, the quantity step,
/// and the least value, the loss limits, the stale limit, the mode, the market, and the symbol,
/// no global halt, and on Futures the liquidation buffer. A rule that is not known to hold does
/// not hold: such a plan is rejected as well.
/// </para>
/// <para>
/// <b>A plan that only closes a position or adjusts protection is held to the rules of the
/// exchange and to protection that only tightens,</b> so that risk can always be reduced. It is
/// rejected only for a rule that is known not to hold: an exchange that gives no answer stands
/// in the way of no close.
/// </para>
/// <para>
/// <b>Every rule is asked, and none is skipped:</b> a check says the outcome of each, also
/// after one that failed (FR-RSK-9). No rule can be turned off, and nothing that it is handed
/// makes it throw: a number that makes no sense is a rule that does not hold.
/// </para>
/// </remarks>
public static class GuardCheck
{
    // How many decimal places a measure that was worked out is kept to.
    private const int MeasureDecimals = 8;

    private static readonly GuardRule[] Rules = Enum.GetValues<GuardRule>();

    /// <summary>Checks a plan against every rule, and says what became of each and of the plan.</summary>
    /// <param name="case">The plan, and the facts that the rules are asked of.</param>
    public static GuardResult Of(GuardCase @case)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(@case.Limits);

        var facts = new Facts(@case);
        RuleResult[] results = [.. Rules.Select(rule => Asked(rule, facts))];

        // What opens a position needs every rule to hold. What only reduces risk is stopped by
        // a rule that is known not to hold, and by nothing less.
        var accepted = @case.OpensPosition
            ? results.All(result => result.Outcome is RuleOutcome.Passed or RuleOutcome.NotApplied)
            : results.All(result => result.Outcome != RuleOutcome.Failed);

        return new GuardResult(accepted ? GuardVerdict.Accepted : GuardVerdict.Rejected, results, @case);
    }

    private static RuleResult Asked(GuardRule rule, Facts facts)
    {
        try
        {
            return rule switch
            {
                GuardRule.StopLossSide => StopLossSide(facts),
                GuardRule.RewardToRisk => RewardToRisk(facts),
                GuardRule.EntryDistance => EntryDistance(facts),
                GuardRule.PriceStep => PriceStep(facts),
                GuardRule.QuantityStep => QuantityStep(facts),
                GuardRule.MinimumValue => MinimumValue(facts),
                GuardRule.HeldPosition => HeldPosition(facts),
                GuardRule.ProtectionTightens => ProtectionTightens(facts),
                GuardRule.DailyLossLimit => LossLimit(rule, facts, facts.Case.EquityAtDayStart, facts.Case.Limits.DailyLossPercent),
                GuardRule.DrawdownLimit => LossLimit(rule, facts, facts.Case.PeakEquity, facts.Case.Limits.DrawdownPercent),
                GuardRule.NotStale => NotStale(facts),
                GuardRule.ModePermitted => Flag(rule, facts, facts.Case.ModePermitted),
                GuardRule.MarketPermitted => Flag(rule, facts, facts.Case.MarketPermitted),
                GuardRule.SymbolPermitted => SymbolPermitted(facts),
                GuardRule.NoGlobalHalt => Flag(rule, facts, !facts.Case.GlobalHalt),
                GuardRule.LiquidationBuffer => LiquidationBuffer(facts),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No such rule."),
            };
        }
        catch (OverflowException)
        {
            // Numbers that cannot be worked out with are no numbers of a market: the rule does not hold.
            return new RuleResult(rule, RuleOutcome.Failed, Measured: null, Threshold: null);
        }
    }

    private static RuleResult StopLossSide(Facts facts)
    {
        const GuardRule Rule = GuardRule.StopLossSide;

        if (!facts.Opens)
        {
            return NotApplied(Rule);
        }

        // A stop-loss is present.
        if (facts.Stop is not { } stop)
        {
            return Failed(Rule, measured: null, facts.Entry);
        }

        if (facts.EntryMakesNoSense)
        {
            return Failed(Rule, stop, threshold: null);
        }

        if (facts.Side is not { } side || facts.Entry is not { } entry)
        {
            return Unknown(Rule, stop, facts.Entry);
        }

        // And on the correct side of the entry. One that stands at the entry risks nothing, and protects nothing.
        return Held(Rule, side == TradeSide.Long ? stop < entry : stop > entry, stop, entry);
    }

    private static RuleResult RewardToRisk(Facts facts)
    {
        const GuardRule Rule = GuardRule.RewardToRisk;
        var minimum = facts.Case.Limits.MinRewardToRisk;

        if (!facts.Opens)
        {
            return NotApplied(Rule);
        }

        if (facts.Stop is not { } stop || facts.Target is not { } target || facts.EntryMakesNoSense)
        {
            return Failed(Rule, measured: null, minimum);
        }

        if (facts.Side is not { } side || facts.Entry is not { } entry)
        {
            return Unknown(Rule, measured: null, minimum);
        }

        // Both are counted in the direction of the trade, so a stop-loss or a take-profit on
        // the wrong side is no risk and no reward.
        var risk = side == TradeSide.Long ? entry - stop : stop - entry;
        var reward = side == TradeSide.Long ? target - entry : entry - target;

        if (risk <= 0m)
        {
            return Failed(Rule, measured: null, minimum);
        }

        return Held(Rule, reward >= minimum * risk, Measure(reward / risk), minimum);
    }

    private static RuleResult EntryDistance(Facts facts)
    {
        const GuardRule Rule = GuardRule.EntryDistance;
        var most = facts.Case.Limits.MaxEntryDistancePercent;

        if (!facts.Opens)
        {
            return NotApplied(Rule);
        }

        if (facts.EntryMakesNoSense)
        {
            return Failed(Rule, measured: null, most);
        }

        if (facts.Price is not { } price || facts.Entry is not { } entry)
        {
            return Unknown(Rule, measured: null, most);
        }

        var distance = Math.Abs(entry - price);

        return Held(Rule, distance * 100m <= most * price, Measure(distance * 100m / price), most);
    }

    private static RuleResult PriceStep(Facts facts)
    {
        const GuardRule Rule = GuardRule.PriceStep;
        var @case = facts.Case;

        // The prices that the plan names: of an entry at a limit, of the stop-loss, and of the take-profit.
        decimal?[] named = facts.Opens
            ? [@case.EntryPrice, @case.StopLoss, @case.TakeProfit]
            : @case.Action == GuardedAction.AdjustProtection ? [@case.StopLoss, @case.TakeProfit] : [];
        decimal[] prices = [.. named.OfType<decimal>()];

        foreach (var price in prices)
        {
            if (price <= 0m)
            {
                return Failed(Rule, price, facts.Symbol?.PriceStep);
            }
        }

        // A plan that names no price has none that is off the step: it is held to no step.
        if (prices.Length == 0)
        {
            return Passed(Rule, measured: null, threshold: null);
        }

        if (facts.Symbol is not { } symbol)
        {
            return Unknown(Rule, measured: null, threshold: null);
        }

        return OnStep(Rule, prices, symbol.PriceStep);
    }

    private static RuleResult QuantityStep(Facts facts)
    {
        const GuardRule Rule = GuardRule.QuantityStep;
        var @case = facts.Case;

        // The quantity of each order: of the entry, which is the size, and of what closes or
        // protects the open position, which is what the position holds.
        decimal?[] ordered = @case.Action switch
        {
            GuardedAction.OpenLong or GuardedAction.OpenShort => [@case.Size?.Quantity],
            GuardedAction.Reverse => [@case.Position?.Quantity, @case.Size?.Quantity],
            _ => [@case.Position?.Quantity],
        };
        decimal[] quantities = [.. ordered.OfType<decimal>()];

        foreach (var quantity in quantities)
        {
            if (quantity <= 0m)
            {
                return Failed(Rule, quantity, facts.Symbol?.QuantityStep);
            }
        }

        if (quantities.Length < ordered.Length || facts.Symbol is not { } symbol)
        {
            return Unknown(Rule, measured: null, facts.Symbol?.QuantityStep);
        }

        return OnStep(Rule, quantities, symbol.QuantityStep);
    }

    private static RuleResult MinimumValue(Facts facts)
    {
        const GuardRule Rule = GuardRule.MinimumValue;
        var least = facts.Symbol?.MinValue;

        if (!facts.Opens)
        {
            return NotApplied(Rule);
        }

        if (facts.EntryMakesNoSense || facts.Case.Size is { Quantity: <= 0m })
        {
            return Failed(Rule, measured: null, least);
        }

        if (facts.Case.Size is not { } size || facts.Symbol is not { } symbol || facts.Entry is not { } entry)
        {
            return Unknown(Rule, measured: null, least);
        }

        var value = size.Quantity * entry;

        return Held(Rule, symbol.MinValue <= 0m || value >= symbol.MinValue, value, symbol.MinValue);
    }

    private static RuleResult HeldPosition(Facts facts)
    {
        const GuardRule Rule = GuardRule.HeldPosition;
        var held = facts.Case.Position is null ? 0 : 1;

        // What opens a position opens one more than the agent holds, and an agent has one at most.
        return facts.Case.Action is GuardedAction.OpenLong or GuardedAction.OpenShort
            ? Held(Rule, held < FixedLimits.OpenPositionsPerAgent, held, FixedLimits.OpenPositionsPerAgent)
            : Held(Rule, held > 0, held, threshold: null);
    }

    private static RuleResult ProtectionTightens(Facts facts)
    {
        const GuardRule Rule = GuardRule.ProtectionTightens;
        var @case = facts.Case;

        if (@case.Action != GuardedAction.AdjustProtection)
        {
            return NotApplied(Rule);
        }

        // The take-profit moves in either direction: a plan that moves it alone adds no risk.
        if (@case.StopLoss is not { } moved)
        {
            return Passed(Rule, measured: null, @case.Position?.StopLoss);
        }

        if (@case.Position is not { } position)
        {
            return Unknown(Rule, moved, threshold: null);
        }

        // A position with no stop-loss resting takes any: whatever is placed reduces the risk of having none.
        if (position.StopLoss is not { } resting)
        {
            return Passed(Rule, moved, threshold: null);
        }

        // Up for a long, and down for a short. One that stays where it is adds no risk.
        return Held(Rule, position.Side == TradeSide.Long ? moved >= resting : moved <= resting, moved, resting);
    }

    private static RuleResult LossLimit(GuardRule rule, Facts facts, decimal from, decimal limit)
    {
        if (!facts.Opens)
        {
            return NotApplied(rule);
        }

        if (from <= 0m)
        {
            return Unknown(rule, measured: null, limit);
        }

        var loss = from - facts.Case.Equity;

        // A loss that is at the limit has reached it.
        return Held(rule, loss * 100m < limit * from, Measure(Math.Max(loss, 0m) * 100m / from), limit);
    }

    private static RuleResult NotStale(Facts facts)
    {
        const GuardRule Rule = GuardRule.NotStale;
        var @case = facts.Case;

        // At the limit itself a decision is in time, as it is where the team decides.
        return facts.Opens
            ? Held(Rule, @case.SinceClose <= @case.StaleLimit, Seconds(@case.SinceClose), Seconds(@case.StaleLimit))
            : NotApplied(Rule);
    }

    private static RuleResult SymbolPermitted(Facts facts)
    {
        const GuardRule Rule = GuardRule.SymbolPermitted;

        if (!facts.Opens)
        {
            return NotApplied(Rule);
        }

        return facts.Case.SymbolsKnown
            ? Held(Rule, facts.Case.Symbol is { Trading: true }, measured: null, threshold: null)
            : Unknown(Rule, measured: null, threshold: null);
    }

    private static RuleResult LiquidationBuffer(Facts facts)
    {
        const GuardRule Rule = GuardRule.LiquidationBuffer;
        var buffer = facts.Case.Limits.LiquidationBuffer;

        if (!facts.Opens || facts.Case.Market != Market.Futures)
        {
            return NotApplied(Rule);
        }

        if (facts.Stop is not { } stop || facts.EntryMakesNoSense || facts.Case.Size is { Leverage: < 1 })
        {
            return Failed(Rule, measured: null, buffer);
        }

        if (facts.Case.Size is not { } size || facts.Side is not { } side || facts.Entry is not { } entry)
        {
            return Unknown(Rule, measured: null, buffer);
        }

        var toStop = side == TradeSide.Long ? entry - stop : stop - entry;

        if (toStop <= 0m)
        {
            return Failed(Rule, measured: null, buffer);
        }

        // The estimate, for isolated margin: the position is liquidated when the price has moved
        // against it by the margin, which is the entry divided by the leverage. It leaves out
        // the maintenance margin, which brings the liquidation a little nearer.
        var toLiquidation = entry / size.Leverage;

        return Held(Rule, entry >= buffer * toStop * size.Leverage, Measure(toLiquidation / toStop), buffer);
    }

    private static RuleResult Flag(GuardRule rule, Facts facts, bool holds) =>
        facts.Opens ? Held(rule, holds, measured: null, threshold: null) : NotApplied(rule);

    /// <summary>Whether every value is a multiple of a step. A step of zero or less is no rule.</summary>
    private static RuleResult OnStep(GuardRule rule, decimal[] values, decimal step)
    {
        if (step <= 0m)
        {
            return Passed(rule, measured: null, threshold: null);
        }

        foreach (var value in values)
        {
            if (value % step != 0m)
            {
                return Failed(rule, value, step);
            }
        }

        return Passed(rule, measured: null, step);
    }

    private static decimal Measure(decimal value) => Math.Round(value, MeasureDecimals, MidpointRounding.AwayFromZero);

    private static decimal Seconds(TimeSpan time) => (decimal)time.Ticks / TimeSpan.TicksPerSecond;

    private static RuleResult Held(GuardRule rule, bool holds, decimal? measured, decimal? threshold) =>
        new(rule, holds ? RuleOutcome.Passed : RuleOutcome.Failed, measured, threshold);

    private static RuleResult Passed(GuardRule rule, decimal? measured, decimal? threshold) =>
        new(rule, RuleOutcome.Passed, measured, threshold);

    private static RuleResult Failed(GuardRule rule, decimal? measured, decimal? threshold) =>
        new(rule, RuleOutcome.Failed, measured, threshold);

    private static RuleResult Unknown(GuardRule rule, decimal? measured, decimal? threshold) =>
        new(rule, RuleOutcome.Unknown, measured, threshold);

    private static RuleResult NotApplied(GuardRule rule) => new(rule, RuleOutcome.NotApplied, Measured: null, Threshold: null);

    /// <summary>The facts of a case as the rules read them: a price that is not above zero is no price.</summary>
    private sealed class Facts(GuardCase @case)
    {
        public GuardCase Case { get; } = @case;

        public bool Opens { get; } = @case.OpensPosition;

        /// <summary>The side that the plan opens. A reverse opens the side opposite to the position that is held.</summary>
        public TradeSide? Side { get; } = @case.Action switch
        {
            GuardedAction.OpenLong => TradeSide.Long,
            GuardedAction.OpenShort => TradeSide.Short,
            GuardedAction.Reverse => @case.Position?.Side switch
            {
                TradeSide.Long => TradeSide.Short,
                TradeSide.Short => TradeSide.Long,
                _ => null,
            },
            _ => null,
        };

        /// <summary>The current price, when the exchange said one.</summary>
        public decimal? Price { get; } = AboveZero(@case.CurrentPrice);

        /// <summary>Whether the plan names a limit price that is no price.</summary>
        public bool EntryMakesNoSense { get; } = @case.EntryPrice is <= 0m;

        /// <summary>The price that the plan enters at: its limit price, or the current price for an entry at the market.</summary>
        public decimal? Entry { get; } = @case.EntryPrice is null ? AboveZero(@case.CurrentPrice) : AboveZero(@case.EntryPrice);

        public decimal? Stop { get; } = AboveZero(@case.StopLoss);

        public decimal? Target { get; } = AboveZero(@case.TakeProfit);

        /// <summary>The rules of the symbol, when the exchange said them and the market has the symbol.</summary>
        public SymbolRules? Symbol { get; } = @case.SymbolsKnown ? @case.Symbol : null;

        private static decimal? AboveZero(decimal? value) => value > 0m ? value : null;
    }
}

/// <summary>
/// What a check of the Risk Guard made of a plan: the verdict, and the outcome of every rule
/// (FR-RSK-9). Only <see cref="GuardCheck"/> makes one, so whatever holds one holds a plan that
/// was checked against every rule (FR-RSK-3).
/// </summary>
public sealed class GuardResult
{
    internal GuardResult(GuardVerdict verdict, IReadOnlyList<RuleResult> rules, GuardCase @case)
    {
        Verdict = verdict;
        Rules = rules;
        Case = @case;
    }

    /// <summary>Whether the plan goes on to the orders.</summary>
    public GuardVerdict Verdict { get; }

    /// <summary>The outcome of every rule, in the order of <see cref="GuardRule"/>: one for each, also for a rule that the plan is not held to.</summary>
    public IReadOnlyList<RuleResult> Rules { get; }

    /// <summary>The plan that was checked, with the facts that it was checked on: what an order is made from is this, and nothing else.</summary>
    public GuardCase Case { get; }

    /// <summary>Whether the plan was accepted.</summary>
    public bool IsAccepted => Verdict == GuardVerdict.Accepted;

    /// <summary>The outcome of one rule.</summary>
    public RuleResult Of(GuardRule rule) => Rules.Single(result => result.Rule == rule);

    /// <summary>
    /// The rules that stand in the way of the plan, in the order of <see cref="GuardRule"/>: for
    /// a plan that opens a position those that failed and those that are not known to hold, and
    /// for every other plan those that failed. Empty for a plan that was accepted.
    /// </summary>
    public IReadOnlyList<RuleResult> InTheWay =>
    [
        .. Rules.Where(result => result.Outcome == RuleOutcome.Failed || (Case.OpensPosition && result.Outcome == RuleOutcome.Unknown)),
    ];
}
