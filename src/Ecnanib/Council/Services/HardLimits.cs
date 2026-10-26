using System.Globalization;
using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Council.Services;

/// <summary>The Risk Manager's decision after the limits: the action to record and the fields that survived.</summary>
internal sealed record LimitsOutcome(
    DecisionAction Action,
    OrderType? EntryType,
    decimal? EntryPrice,
    decimal? StopPrice,
    IReadOnlyList<decimal> Targets,
    decimal? PartialFraction,
    decimal? RiskPercent,
    IReadOnlyList<LimitCheck> Checks)
{
    public bool Rejected => Checks.Any(c => c.Status == LimitCheckStatus.Fail);

    public IEnumerable<string> FailedLimits => Checks.Where(c => c.Status == LimitCheckStatus.Fail).Select(c => c.Limit);
}

/// <summary>
/// Enforces the hard limits after the council regardless of its output (FR-AI-11): the FR-EXE-04 and FR-EXE-09
/// limits that need no venue (entries allowed, no-entry windows, a setup or discretionary mode, direction, the
/// position cap counting open entry orders, a stop on the right side within the strategy's distance bound, risk per
/// trade within the agent's limit and the cap, leverage) and FR-AI-15 (a stop is only ever tightened). A violated
/// limit on an entry rejects it (no_trade); on a modify it leaves the position as it is (hold). Sizing, notional and
/// balance belong to execution, which appends its own checks.
/// </summary>
internal static class HardLimits
{
    public static LimitsOutcome Apply(FinalDecisionDocument document, CouncilContext context, RiskCapsOptions caps)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caps);

        return document.ProposedAction switch
        {
            DecisionAction.OpenLong or DecisionAction.OpenShort => Opening(document, context, caps),
            DecisionAction.Hold or DecisionAction.Modify or DecisionAction.Close => Managing(document, context),
            _ => new LimitsOutcome(DecisionAction.NoTrade, null, null, null, [], null, null, []),
        };
    }

    public static JsonElement ToJson(IReadOnlyList<LimitCheck> checks) =>
        JsonSerializer.SerializeToElement(checks.Select(c => new { limit = c.Limit, status = c.Status.ToString().ToLowerInvariant(), detail = c.Detail }));

    private static LimitsOutcome Opening(FinalDecisionDocument document, CouncilContext context, RiskCapsOptions caps)
    {
        var agent = context.Agent;
        var side = document.ProposedAction == DecisionAction.OpenLong ? PositionSide.Long : PositionSide.Short;
        var checks = new List<LimitCheck>();
        var last = context.Market.Latest?.Close;
        var entry = document.EntryType == OrderType.Market ? last : document.EntryPrice;

        Add(checks, LimitNames.EntriesAllowed, context.EntriesAllowed, "new entries are allowed", "new entries are not allowed in this evaluation (the agent is paused, or its open state could not be managed)");

        var close = context.Evaluation.CandleOpenTime + agent.Timeframe.ToTimeSpan();
        var window = agent.EntryWindows.FirstOrDefault(w => EntryWindowSchedule.IsActive(w, close));
        Add(checks, LimitNames.NoEntryWindow, window is null, "no no-entry window is active", "a no-entry window is active at the candle close");

        var setup = context.Output.Setup;
        if (setup is null)
            Add(checks, LimitNames.SetupRequired, agent.Discretionary, "discretionary mode allows an entry without a setup", "the strategy reports no setup and discretionary mode is off");
        else if (setup.Side != side)
            Add(checks, LimitNames.SetupRequired, agent.Discretionary, $"discretionary mode allows a {Lower(side)} entry against the {Lower(setup.Side)} setup", $"the setup is {Lower(setup.Side)} and discretionary mode is off");
        else
            checks.Add(new LimitCheck(LimitNames.SetupRequired, LimitCheckStatus.Pass, $"the strategy's {Lower(side)} setup"));

        Add(checks, LimitNames.Direction, agent.MarketType != MarketType.Spot || side == PositionSide.Long, $"{Lower(side)} is allowed on {Market(agent.MarketType)}", "a Spot agent is long-only");

        Add(checks, LimitNames.DailyLoss, agent.PauseCause != PauseCause.DailyLoss, "the daily loss limit is not reached", "the daily loss limit is reached");

        var positionCap = PositionRoom.Cap(agent, caps.MaxOpenPositions);
        var open = PositionRoom.Open(context.OpenTrades, context.OpenOrders);
        Add(checks, LimitNames.OpenPositions, open < positionCap, $"{open} of {positionCap} open (positions and pending entries)", $"{open} of {positionCap} already open (positions and pending entries)");

        var stop = document.StopPrice;
        Add(checks, LimitNames.StopRequired, stop is not null, "a stop is set", "no stop was given");

        if (stop is { } s && entry is { } e)
        {
            var rightSide = side == PositionSide.Long ? s < e : s > e;
            Add(checks, LimitNames.StopSide, rightSide, $"stop {N(s)} is on the losing side of the entry {N(e)}", $"stop {N(s)} is not on the losing side of the entry {N(e)}");

            // NFR-REL-03: a strategy that bounds the stop distance needs the ATR to check it; without one the entry is refused, not waved through.
            if (!TryReadDecimal(context.Parameters, StrategyParameters.MaxStopDistanceAtr, out var maxAtr))
            {
                checks.Add(new LimitCheck(LimitNames.StopDistance, LimitCheckStatus.Pass, "not applicable: the strategy declares no stop-distance bound"));
            }
            else if (!context.Output.Indicators.TryGetValue("atr", out var atr) || atr <= 0)
            {
                checks.Add(new LimitCheck(LimitNames.StopDistance, LimitCheckStatus.Fail, $"the strategy bounds the stop at {N(maxAtr)} ATR but no ATR is available"));
            }
            else
            {
                var distance = Math.Abs(e - s) / atr;
                Add(checks, LimitNames.StopDistance, distance <= maxAtr, $"{N(Math.Round(distance, 2))} ATR, at most {N(maxAtr)}", $"{N(Math.Round(distance, 2))} ATR is beyond the strategy's bound of {N(maxAtr)} ATR");
            }
        }
        else if (stop is not null)
        {
            Add(checks, LimitNames.StopSide, false, "", "no entry price to compare the stop with");
        }

        var riskLimit = Math.Min(agent.RiskLimits.RiskPerTradePercent, caps.MaxRiskPerTradePercent);
        decimal? risk = document.RiskPercent;
        if (risk is null)
        {
            risk = riskLimit;
            checks.Add(new LimitCheck(LimitNames.RiskPerTrade, LimitCheckStatus.Pass, $"not given; the agent's limit of {N(riskLimit)}% applies"));
        }
        else if (risk > riskLimit)
        {
            checks.Add(new LimitCheck(LimitNames.RiskPerTrade, LimitCheckStatus.Clamped, $"{N(risk.Value)}% reduced to the limit of {N(riskLimit)}%"));
            risk = riskLimit;
        }
        else
        {
            checks.Add(new LimitCheck(LimitNames.RiskPerTrade, LimitCheckStatus.Pass, $"{N(risk.Value)}% within the limit of {N(riskLimit)}%"));
        }

        if (agent.MarketType == MarketType.UsdFutures)
            Add(checks, LimitNames.Leverage, (agent.Leverage ?? 1) <= caps.MaxLeverage, $"{agent.Leverage ?? 1}x within the cap of {caps.MaxLeverage}x", $"{agent.Leverage}x is above the cap of {caps.MaxLeverage}x");

        var targets = FilterTargets(document.Targets, side, entry ?? last, checks);

        if (checks.Any(c => c.Status == LimitCheckStatus.Fail))
            return new LimitsOutcome(DecisionAction.NoTrade, null, null, null, [], null, null, checks);
        return new LimitsOutcome(document.ProposedAction, document.EntryType, document.EntryType == OrderType.Market ? null : document.EntryPrice, stop, targets, null, risk, checks);
    }

    private static LimitsOutcome Managing(FinalDecisionDocument document, CouncilContext context)
    {
        var checks = new List<LimitCheck>();
        var trade = context.OpenTrades.FirstOrDefault();
        if (trade is null)
        {
            checks.Add(new LimitCheck(LimitNames.PositionRequired, LimitCheckStatus.Fail, "no position is open"));
            return new LimitsOutcome(DecisionAction.NoTrade, null, null, null, [], null, null, checks);
        }

        checks.Add(new LimitCheck(LimitNames.PositionRequired, LimitCheckStatus.Pass, $"{Lower(trade.Side)} position of {N(trade.Size)} open"));
        if (document.ProposedAction == DecisionAction.Close)
            return new LimitsOutcome(DecisionAction.Close, null, null, null, [], null, null, checks);

        if (document.ProposedAction == DecisionAction.Hold)
        {
            if (document.StopPrice is not null || document.Targets.Count > 0 || document.PartialFraction is not null)
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Clamped, "hold keeps the current stop and targets; propose modify to change them"));
            return new LimitsOutcome(DecisionAction.Hold, null, null, null, [], null, null, checks);
        }

        var last = context.Market.Latest?.Close;
        decimal? stop = document.StopPrice;
        if (stop is { } proposed)
        {
            if (last is { } price && (trade.Side == PositionSide.Long ? proposed >= price : proposed <= price))
            {
                checks.Add(new LimitCheck(LimitNames.StopSide, LimitCheckStatus.Fail, $"stop {N(proposed)} would trigger at once against the last close {N(price)}"));
            }
            else if (trade.CurrentStop is { } current && (trade.Side == PositionSide.Long ? proposed < current : proposed > current))
            {
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Clamped, $"stop {N(proposed)} would widen the current stop {N(current)}; the stop stays"));
                stop = null;
            }
            else if (trade.CurrentStop == proposed)
            {
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Pass, $"stop {N(proposed)} unchanged"));
                stop = null;
            }
            else
            {
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Pass, trade.CurrentStop is { } c ? $"stop tightened from {N(c)} to {N(proposed)}" : $"stop set to {N(proposed)}"));
            }
        }

        var targets = FilterTargets(document.Targets, trade.Side, last, checks);
        if (document.PartialFraction is { } fraction)
            checks.Add(new LimitCheck(LimitNames.PartialFraction, LimitCheckStatus.Pass, $"take {N(fraction * 100)}% of the position"));

        if (checks.Any(c => c.Status == LimitCheckStatus.Fail))
            return new LimitsOutcome(DecisionAction.Hold, null, null, null, [], null, null, checks);
        if (stop is null && targets.Count == 0 && document.PartialFraction is null)
        {
            checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Clamped, "nothing left to modify; the position is held"));
            return new LimitsOutcome(DecisionAction.Hold, null, null, null, [], null, null, checks);
        }

        return new LimitsOutcome(DecisionAction.Modify, null, null, stop, targets, document.PartialFraction, null, checks);
    }

    /// <summary>Targets must lie on the winning side of the reference price; the others are dropped (clamped).</summary>
    private static IReadOnlyList<decimal> FilterTargets(IReadOnlyList<decimal> targets, PositionSide side, decimal? reference, List<LimitCheck> checks)
    {
        if (targets.Count == 0)
            return [];
        if (reference is not { } price)
            return targets;
        var kept = targets.Where(t => side == PositionSide.Long ? t > price : t < price).ToList();
        if (kept.Count == targets.Count)
            return targets;
        checks.Add(new LimitCheck(LimitNames.Targets, LimitCheckStatus.Clamped, $"{targets.Count - kept.Count} target(s) not beyond {N(price)} dropped"));
        return kept;
    }

    private static void Add(List<LimitCheck> checks, string limit, bool pass, string passDetail, string failDetail) =>
        checks.Add(new LimitCheck(limit, pass ? LimitCheckStatus.Pass : LimitCheckStatus.Fail, pass ? passDetail : failDetail));

    private static bool TryReadDecimal(IReadOnlyDictionary<string, JsonElement> parameters, string name, out decimal value)
    {
        value = 0;
        return parameters.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value);
    }

    private static string Lower(PositionSide side) => side.ToString().ToLowerInvariant();

    private static string Market(MarketType market) => market == MarketType.Spot ? "Spot" : "Futures";

    /// <summary>Plain decimal without trailing zeros, as the detail text reads best.</summary>
    private static string N(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
