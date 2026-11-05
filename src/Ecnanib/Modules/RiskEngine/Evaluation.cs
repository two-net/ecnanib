using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Modules.RiskEngine.ExchangeMath;

namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// One evaluation of one intent. Every rule runs, in <see cref="RiskRule"/> order, and is recorded with what it
/// looked at (FR-RISK-10). A rule that depends on one that failed is NotEvaluated, and only a failed Settings or
/// Action rule stops the rest. The verdict passes when no rule failed.
/// </summary>
/// <remarks>
/// Exits and risk-reducing changes are never blocked by policy: the loss limits, cooldown, funding and the rest
/// apply to entries only, so the kill switch and the emergency close can always pass (AC-01). An exit fails only
/// when it can't be sent at all, such as when there's no position.
/// <para>
/// s is +1 for a long and −1 for a short, so s × (a − b) &gt; 0 reads "a is on the profitable side of b".
/// Comparisons multiply rather than divide, so no rounding can move a boundary.
/// </para>
/// </remarks>
internal sealed class Evaluation
{
    private static readonly RiskRule[] Order = Enum.GetValues<RiskRule>();

    private readonly RiskCaps _caps;
    private readonly OrderIntent _intent;
    private readonly RiskInputs _inputs;
    private readonly RiskSettings _effective;
    private readonly ActionKind _kind;
    private readonly bool _futures;
    private readonly Trace _trace = new();

    private PositionSide _side = PositionSide.Long;
    private bool _add;
    private LossLimitBreach? _breach;

    // Set by Prices.
    private decimal _entry;
    private decimal? _stop;
    private decimal? _target;

    // Set by Size.
    private decimal _quantity;
    private decimal _moneyAtRisk;

    // Set by LiquidationBuffer.
    private decimal? _liquidation;

    public Evaluation(RiskCaps caps, OrderIntent intent, RiskInputs inputs)
    {
        _caps = caps;
        _intent = intent;
        _inputs = inputs;
        _effective = caps.Apply(inputs.Settings);
        _futures = inputs.Market == Market.Futures;
        _kind = intent.Action switch
        {
            IntentAction.Buy or IntentAction.OpenLong or IntentAction.OpenShort => ActionKind.Entry,
            IntentAction.Sell or IntentAction.Close => ActionKind.Exit,
            IntentAction.AdjustStop => ActionKind.AdjustStop,
            IntentAction.AdjustTarget => ActionKind.AdjustTarget,
            _ => ActionKind.Cancel,
        };
    }

    private enum ActionKind
    {
        Entry,
        Exit,
        AdjustStop,
        AdjustTarget,
        Cancel,
    }

    private decimal S => _side == PositionSide.Long ? 1m : -1m;

    private bool Entry => _kind == ActionKind.Entry;

    private OpenPosition Position => _inputs.Position!;

    private MarketPrices Prices => _inputs.Prices;

    public RiskEvaluation Run()
    {
        foreach (var rule in Order)
        {
            _trace.Add(Gated(rule) ? Trace.NotEvaluated(rule) : Evaluate(rule));
        }

        var passed = !_trace.Failed;

        return new RiskEvaluation(
            passed ? RiskVerdict.Pass : RiskVerdict.Fail,
            _intent,
            _caps,
            _effective,
            _trace.Results,
            passed ? Sized() : null,
            _breach);
    }

    /// <summary>Settings and Action decide what the rest means, so nothing runs after either fails.</summary>
    private bool Gated(RiskRule rule) => rule switch
    {
        RiskRule.Settings => false,
        RiskRule.Action => !_trace.Passed(RiskRule.Settings),
        _ => !_trace.Passed(RiskRule.Settings, RiskRule.Action),
    };

    private RuleResult Evaluate(RiskRule rule) => rule switch
    {
        RiskRule.Settings => Entry ? Rules.Settings(_caps, _inputs.Settings, _inputs.CapitalAllocation) : Trace.NotApplicable(rule),
        RiskRule.Action => ActionRule(),
        RiskRule.SymbolTrading => SymbolTrading(),
        RiskRule.DailyLoss => LossLimit(rule, LossLimits.Daily(_effective, _inputs.CapitalAllocation, _inputs.Record, _inputs.Now)),
        RiskRule.WeeklyLoss => LossLimit(rule, LossLimits.Weekly(_effective, _inputs.CapitalAllocation, _inputs.Record, _inputs.Now)),
        RiskRule.ConsecutiveLosses => LossLimit(rule, LossLimits.Consecutive(_effective, _inputs.Record)),
        RiskRule.EntriesPerDay => EntriesPerDay(),
        RiskRule.Cooldown => Cooldown(),
        RiskRule.PendingEntry => PendingEntry(),
        RiskRule.Funding => Funding(),
        RiskRule.Prices => PricesRule(),
        RiskRule.StopRequired => StopRequired(),
        RiskRule.StopSide => StopSide(),
        RiskRule.StopDistance => StopDistance(),
        RiskRule.StopNotWidened => StopNotWidened(),
        RiskRule.TargetSide => TargetSide(),
        RiskRule.RewardToRisk => RewardToRisk(),
        RiskRule.NoAveragingDown => NoAveragingDown(),
        RiskRule.Size => Size(),
        RiskRule.Quantity => QuantityRule(),
        RiskRule.Notional => Notional(),
        RiskRule.LiquidationBuffer => LiquidationBuffer(),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };

    private RuleResult ActionRule()
    {
        const RiskRule rule = RiskRule.Action;
        var position = _inputs.Position;
        var inputs = new Inputs()
            .Add("Action", _intent.Action)
            .Add("Origin", _intent.Origin)
            .Add("Market", _inputs.Market)
            .Add("EntryType", _intent.EntryType)
            .Add("PositionSide", position?.Side)
            .Add("PositionQuantity", position?.Quantity)
            .Add("PositionEntry", position?.AverageEntry)
            .Add("PositionStop", position?.Stop)
            .Add("PositionTarget", position?.Target)
            .Add("HasPendingEntry", _inputs.HasPendingEntry);

        if (!AllowedOn(_inputs.Market, _intent.Action))
        {
            return Trace.Fail(rule, RiskReason.ActionNotForMarket, inputs);
        }

        // Spot holds only longs, and a position is never empty or free.
        if (position is not null
            && (position.Quantity <= 0m || position.AverageEntry <= 0m || (!_futures && position.Side == PositionSide.Short)))
        {
            return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
        }

        switch (_kind)
        {
            case ActionKind.Entry:
                if (_intent.EntryType is null)
                {
                    return Trace.Fail(rule, RiskReason.EntryTypeMissing, inputs);
                }

                _side = _intent.Action == IntentAction.OpenShort ? PositionSide.Short : PositionSide.Long;
                inputs.Add("Side", _side);

                // One-way mode nets an opposite order against the position, so turning it round takes a close first.
                if (position is not null && position.Side != _side)
                {
                    return Trace.Fail(rule, RiskReason.OppositePosition, inputs);
                }

                _add = position is not null;
                inputs.Add("AddsToPosition", _add);
                break;

            case ActionKind.Cancel:
                if (!_inputs.HasPendingEntry)
                {
                    return Trace.Fail(rule, RiskReason.NoPendingEntry, inputs);
                }

                break;

            default:
                if (position is null)
                {
                    return Trace.Fail(rule, RiskReason.NoPosition, inputs);
                }

                _side = position.Side;
                break;
        }

        return Trace.Pass(rule, inputs);
    }

    private static bool AllowedOn(Market market, IntentAction action) => action switch
    {
        IntentAction.Buy or IntentAction.Sell => market == Market.Spot,
        IntentAction.OpenLong or IntentAction.OpenShort or IntentAction.Close => market == Market.Futures,
        IntentAction.AdjustStop or IntentAction.AdjustTarget or IntentAction.CancelPending => true,
        _ => false,
    };

    private RuleResult SymbolTrading()
    {
        if (!Entry)
        {
            return Trace.NotApplicable(RiskRule.SymbolTrading);
        }

        var inputs = new Inputs().Add("Trading", _inputs.Filters.Trading);

        return _inputs.Filters.Trading
            ? Trace.Pass(RiskRule.SymbolTrading, inputs)
            : Trace.Fail(RiskRule.SymbolTrading, RiskReason.SymbolNotTrading, inputs);
    }

    private RuleResult LossLimit(RiskRule rule, (RuleResult Result, LossLimitBreach? Breach) check)
    {
        if (!Entry)
        {
            return Trace.NotApplicable(rule);
        }

        _breach = LossLimits.MostRestrictive(_breach, check.Breach);

        return check.Result;
    }

    private RuleResult EntriesPerDay()
    {
        const RiskRule rule = RiskRule.EntriesPerDay;

        if (!Entry)
        {
            return Trace.NotApplicable(rule);
        }

        var entries = _inputs.Record.EntriesToday;
        var inputs = new Inputs().Add("EntriesToday", entries).Add("MaxNewEntriesPerDay", _effective.MaxNewEntriesPerDay);

        return entries < 0 ? Trace.Fail(rule, RiskReason.InputInvalid, inputs)
            : entries >= _effective.MaxNewEntriesPerDay ? Trace.Fail(rule, RiskReason.EntriesPerDayReached, inputs)
            : Trace.Pass(rule, inputs);
    }

    private RuleResult Cooldown()
    {
        const RiskRule rule = RiskRule.Cooldown;

        if (!Entry)
        {
            return Trace.NotApplicable(rule);
        }

        var since = _inputs.Record.CyclesSinceLastLoss;
        var inputs = new Inputs().Add("CyclesSinceLastLoss", since).Add("CooldownCyclesAfterLoss", _effective.CooldownCyclesAfterLoss);

        return since is null ? Trace.Pass(rule, inputs)
            : since < 0 ? Trace.Fail(rule, RiskReason.InputInvalid, inputs)
            : since < _effective.CooldownCyclesAfterLoss ? Trace.Fail(rule, RiskReason.CoolingDown, inputs)
            : Trace.Pass(rule, inputs);
    }

    private RuleResult PendingEntry()
    {
        if (!Entry)
        {
            return Trace.NotApplicable(RiskRule.PendingEntry);
        }

        // An unfilled entry plus a new one could together pass every cap, so they don't stack.
        var inputs = new Inputs().Add("HasPendingEntry", _inputs.HasPendingEntry);

        return _inputs.HasPendingEntry
            ? Trace.Fail(RiskRule.PendingEntry, RiskReason.EntryPending, inputs)
            : Trace.Pass(RiskRule.PendingEntry, inputs);
    }

    /// <summary>
    /// The rate against the position, per 8 hours, above the agent's threshold skips the entry. Funding is a
    /// fraction per interval, so the 8-hour percentage is rate × 100 × 8 ÷ interval, compared multiplied out.
    /// </summary>
    private RuleResult Funding()
    {
        const RiskRule rule = RiskRule.Funding;

        if (!Entry || !_futures)
        {
            return Trace.NotApplicable(rule);
        }

        var funding = _inputs.Funding;
        var threshold = _effective.FundingFilterPercent;
        var inputs = new Inputs()
            .Add("Side", _side)
            .Add("FundingRate", funding?.Rate)
            .Add("IntervalHours", funding?.IntervalHours)
            .Add("FundingFilterPercent", threshold);

        // Without a rate, nothing says funding is against the position.
        if (funding is null)
        {
            return Trace.Pass(rule, inputs);
        }

        if (funding.IntervalHours <= 0)
        {
            return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
        }

        inputs.Add("AgainstPercentPer8Hours", S * funding.Rate * 800m / funding.IntervalHours);

        return S * funding.Rate * 800m > threshold * funding.IntervalHours
            ? Trace.Fail(rule, RiskReason.FundingAgainstPosition, inputs)
            : Trace.Pass(rule, inputs);
    }

    /// <summary>
    /// Rounds every price to the tick before anything checks or sizes with it, in the direction that can't add
    /// risk: a limit entry to a better price, a stop toward the price, which can only tighten it, and a target
    /// toward the entry. Then each must be within PRICE_FILTER, and a limit price within PERCENT_PRICE's band.
    /// </summary>
    private RuleResult PricesRule()
    {
        const RiskRule rule = RiskRule.Prices;

        if (_kind is ActionKind.Exit or ActionKind.Cancel)
        {
            return Trace.NotApplicable(rule);
        }

        var inputs = new Inputs();
        var tick = _inputs.Filters.Price;

        if (tick is null)
        {
            return Trace.Fail(rule, RiskReason.FilterMissing, inputs.Add("PriceFilter", "none"));
        }

        inputs.Add("TickSize", tick.TickSize).Add("MinPrice", tick.Minimum).Add("MaxPrice", tick.Maximum);
        var longSide = _side == PositionSide.Long;
        var limitEntry = false;

        decimal Better(decimal price) => longSide ? Down(price, tick.TickSize, tick.Minimum) : Up(price, tick.TickSize, tick.Minimum);
        decimal Worse(decimal price) => longSide ? Up(price, tick.TickSize, tick.Minimum) : Down(price, tick.TickSize, tick.Minimum);

        if (Entry)
        {
            if (_intent.EntryType == EntryType.Market)
            {
                // What a market order would pay now: the ask to buy, the bid to sell.
                _entry = longSide ? Prices.Ask : Prices.Bid;
                inputs.Add("Bid", Prices.Bid).Add("Ask", Prices.Ask).Add("Entry", _entry);

                if (_entry <= 0m)
                {
                    return Trace.Fail(rule, RiskReason.PriceMissing, inputs);
                }
            }
            else
            {
                inputs.Add("LimitPrice", _intent.LimitPrice);

                if (_intent.LimitPrice is not > 0m)
                {
                    return Trace.Fail(rule, RiskReason.PriceMissing, inputs);
                }

                _entry = Better(_intent.LimitPrice.Value);
                limitEntry = true;
                inputs.Add("Entry", _entry);
            }
        }

        if (_kind is (ActionKind.Entry or ActionKind.AdjustStop) && _intent.StopPrice is { } stop)
        {
            inputs.Add("StopAsked", stop);

            if (stop <= 0m)
            {
                return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
            }

            // Toward the price: up for a long's stop, which sits below it.
            _stop = Worse(stop);
            inputs.Add("Stop", _stop);
        }

        if (_kind is (ActionKind.Entry or ActionKind.AdjustTarget) && _intent.TargetPrice is { } target)
        {
            inputs.Add("TargetAsked", target);

            if (target <= 0m)
            {
                return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
            }

            // Toward the entry: down for a long's target, which sits above it.
            _target = Better(target);
            inputs.Add("Target", _target);
        }

        if ((limitEntry && Outside(_entry)) || (_stop is { } roundedStop && Outside(roundedStop)) || (_target is { } roundedTarget && Outside(roundedTarget)))
        {
            return Trace.Fail(rule, RiskReason.PriceOutOfRange, inputs);
        }

        // Spot sends the target as a limit sell. A Futures target is a market order when it triggers.
        var bandedTarget = !_futures ? _target : null;

        if (_inputs.Filters.PercentPrice is { } band && (limitEntry || bandedTarget is not null))
        {
            // Binance's reference is the mark price on Futures and a recent average price on Spot, for which the
            // last price stands in.
            var reference = _futures ? Prices.Mark : Prices.Last;
            inputs.Add("BandReference", reference)
                .Add("BuyUp", band.BuyUp)
                .Add("BuyDown", band.BuyDown)
                .Add("SellUp", band.SellUp)
                .Add("SellDown", band.SellDown);

            if (reference is not > 0m)
            {
                return Trace.Fail(rule, _futures ? RiskReason.MarkPriceMissing : RiskReason.PriceMissing, inputs);
            }

            var entrySide = longSide ? OrderSide.Buy : OrderSide.Sell;

            if ((limitEntry && OutsideBand(_entry, entrySide, band, reference.Value))
                || (bandedTarget is { } spotTarget && OutsideBand(spotTarget, OrderSide.Sell, band, reference.Value)))
            {
                return Trace.Fail(rule, RiskReason.PriceOutsideBand, inputs);
            }
        }

        return Trace.Pass(rule, inputs);

        bool Outside(decimal price) => price <= 0m || OutOfRange(price, tick.Minimum, tick.Maximum);
    }

    private static bool OutsideBand(decimal price, OrderSide side, PercentPriceRule band, decimal reference)
    {
        var (up, down) = side == OrderSide.Buy ? (band.BuyUp, band.BuyDown) : (band.SellUp, band.SellDown);

        return (up > 0m && price > reference * up) || (down > 0m && price < reference * down);
    }

    private RuleResult StopRequired()
    {
        const RiskRule rule = RiskRule.StopRequired;

        if (_kind is not (ActionKind.Entry or ActionKind.AdjustStop))
        {
            return Trace.NotApplicable(rule);
        }

        var inputs = new Inputs().Add("Stop", _intent.StopPrice);

        return _intent.StopPrice is null
            ? Trace.Fail(rule, RiskReason.StopMissing, inputs)
            : Trace.Pass(rule, inputs);
    }

    /// <summary>
    /// An entry's stop is below a long's entry and above a short's (FR-RISK-05). A moved stop is on that side of
    /// the price that triggers it, or it would fire at once.
    /// </summary>
    private RuleResult StopSide()
    {
        const RiskRule rule = RiskRule.StopSide;

        if (_kind is not (ActionKind.Entry or ActionKind.AdjustStop))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Prices, RiskRule.StopRequired))
        {
            return Trace.NotEvaluated(rule);
        }

        var stop = _stop!.Value;
        var inputs = new Inputs().Add("Side", _side).Add("Stop", stop);

        if (Entry)
        {
            inputs.Add("Entry", _entry);

            return S * (_entry - stop) > 0m
                ? Trace.Pass(rule, inputs)
                : Trace.Fail(rule, RiskReason.StopWrongSide, inputs);
        }

        // Futures stops trigger on the mark price (#34 sends them with workingType MARK_PRICE). Spot's trigger on
        // trades, so the stop must be below both the last price and the bid.
        var trigger = _futures ? Prices.Mark : Math.Min(Prices.Last, Prices.Bid);
        inputs.Add("TriggerPrice", trigger);

        if (trigger is not > 0m)
        {
            return Trace.Fail(rule, _futures ? RiskReason.MarkPriceMissing : RiskReason.PriceMissing, inputs);
        }

        return S * (trigger.Value - stop) > 0m
            ? Trace.Pass(rule, inputs)
            : Trace.Fail(rule, RiskReason.StopWrongSide, inputs);
    }

    /// <summary>Between the agent's ATR bounds (FR-RISK-05). Entries only: a trailing stop may sit closer.</summary>
    private RuleResult StopDistance()
    {
        const RiskRule rule = RiskRule.StopDistance;

        if (!Entry)
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.StopSide))
        {
            return Trace.NotEvaluated(rule);
        }

        var distance = S * (_entry - _stop!.Value);
        var atr = _inputs.Atr;
        var inputs = new Inputs()
            .Add("Entry", _entry)
            .Add("Stop", _stop)
            .Add("Distance", distance)
            .Add("Atr", atr)
            .Add("MinStopDistanceAtr", _effective.MinStopDistanceAtr)
            .Add("MaxStopDistanceAtr", _effective.MaxStopDistanceAtr);

        if (atr is not > 0m)
        {
            return Trace.Fail(rule, RiskReason.AtrUnavailable, inputs);
        }

        inputs.Add("DistanceInAtr", distance / atr.Value);

        return distance < _effective.MinStopDistanceAtr * atr.Value ? Trace.Fail(rule, RiskReason.StopTooClose, inputs)
            : distance > _effective.MaxStopDistanceAtr * atr.Value ? Trace.Fail(rule, RiskReason.StopTooFar, inputs)
            : Trace.Pass(rule, inputs);
    }

    /// <summary>
    /// A moved stop must be strictly tighter than the current one (FR-RISK-06). An add's stop, which then guards the
    /// whole position, may equal the current one but not be wider.
    /// </summary>
    private RuleResult StopNotWidened()
    {
        const RiskRule rule = RiskRule.StopNotWidened;

        if (!(_kind == ActionKind.AdjustStop || (Entry && _add)))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Prices, RiskRule.StopRequired))
        {
            return Trace.NotEvaluated(rule);
        }

        var stop = _stop!.Value;
        var current = Position.Stop;
        var inputs = new Inputs().Add("Side", _side).Add("CurrentStop", current).Add("Stop", stop);

        // A position with no stop gains one, which can only reduce risk.
        if (current is null)
        {
            return Trace.Pass(rule, inputs);
        }

        var tightening = S * (stop - current.Value);

        if (tightening > 0m || (Entry && tightening == 0m))
        {
            return Trace.Pass(rule, inputs);
        }

        return Trace.Fail(rule, tightening == 0m ? RiskReason.StopUnchanged : RiskReason.StopWidened, inputs);
    }

    private RuleResult TargetSide()
    {
        const RiskRule rule = RiskRule.TargetSide;

        if (!((Entry && _intent.TargetPrice is not null) || _kind == ActionKind.AdjustTarget))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Prices))
        {
            return Trace.NotEvaluated(rule);
        }

        var inputs = new Inputs().Add("Side", _side).Add("Target", _target);

        if (Entry)
        {
            inputs.Add("Entry", _entry);

            return S * (_target!.Value - _entry) > 0m
                ? Trace.Pass(rule, inputs)
                : Trace.Fail(rule, RiskReason.TargetWrongSide, inputs);
        }

        // No target removes the take-profit, which leaves the stop to protect the position.
        if (_target is null)
        {
            return Trace.Pass(rule, inputs);
        }

        var price = _futures ? Prices.Mark : Prices.Last;
        inputs.Add("Price", price);

        if (price is not > 0m)
        {
            return Trace.Fail(rule, _futures ? RiskReason.MarkPriceMissing : RiskReason.PriceMissing, inputs);
        }

        return S * (_target.Value - price.Value) > 0m
            ? Trace.Pass(rule, inputs)
            : Trace.Fail(rule, RiskReason.TargetWrongSide, inputs);
    }

    private RuleResult RewardToRisk()
    {
        const RiskRule rule = RiskRule.RewardToRisk;

        if (!(Entry && _intent.TargetPrice is not null))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.StopSide, RiskRule.TargetSide))
        {
            return Trace.NotEvaluated(rule);
        }

        var reward = S * (_target!.Value - _entry);
        var risk = S * (_entry - _stop!.Value);
        var inputs = new Inputs()
            .Add("Entry", _entry)
            .Add("Stop", _stop)
            .Add("Target", _target)
            .Add("Reward", reward)
            .Add("Risk", risk)
            .Add("RewardToRisk", reward / risk)
            .Add("MinRewardToRisk", _effective.MinRewardToRisk);

        return reward >= _effective.MinRewardToRisk * risk
            ? Trace.Pass(rule, inputs)
            : Trace.Fail(rule, RiskReason.RewardToRiskTooLow, inputs);
    }

    /// <summary>
    /// An add needs the position in profit at the price it would close at now (FR-RISK-07). Break-even counts as a
    /// loss, and Binance's break-even price, which includes fees, is used when known.
    /// </summary>
    private RuleResult NoAveragingDown()
    {
        const RiskRule rule = RiskRule.NoAveragingDown;

        if (!(Entry && _add))
        {
            return Trace.NotApplicable(rule);
        }

        var reference = Position.BreakEven ?? Position.AverageEntry;
        var exit = _futures ? Prices.Mark : Prices.Bid;
        var inputs = new Inputs()
            .Add("Side", _side)
            .Add("AverageEntry", Position.AverageEntry)
            .Add("BreakEven", Position.BreakEven)
            .Add("ExitPrice", exit);

        if (exit is not > 0m)
        {
            return Trace.Fail(rule, _futures ? RiskReason.MarkPriceMissing : RiskReason.PriceMissing, inputs);
        }

        return S * (exit.Value - reference) > 0m
            ? Trace.Pass(rule, inputs)
            : Trace.Fail(rule, RiskReason.AveragingDown, inputs);
    }

    private RuleResult Size()
    {
        const RiskRule rule = RiskRule.Size;

        return _kind switch
        {
            ActionKind.Entry when !_trace.Passed(RiskRule.StopSide) => Trace.NotEvaluated(rule),
            ActionKind.Entry => EntrySize(),
            ActionKind.Exit => ExitSize(),
            _ => Trace.NotApplicable(rule),
        };
    }

    /// <summary>
    /// FR-RISK-03: quantity = (capital × risk %) ÷ |entry − stop|, then the smallest of every cap, rounded down to
    /// the step. On an add, one stop guards the whole position, so what it already risks at the new stop comes
    /// out of the budget, and its notional and cost count against the caps.
    /// </summary>
    private RuleResult EntrySize()
    {
        const RiskRule rule = RiskRule.Size;
        var inputs = new Inputs();
        var lot = _inputs.Filters.Lot;

        if (lot is null)
        {
            return Trace.Fail(rule, RiskReason.FilterMissing, inputs.Add("LotSize", "none"));
        }

        var asked = _intent.RiskPercent;
        inputs.Add("RiskPercentAsked", asked);

        if (asked is <= 0m)
        {
            return Trace.Fail(rule, RiskReason.RiskPercentInvalid, inputs);
        }

        if (_inputs.AvailableQuote < 0m)
        {
            return Trace.Fail(rule, RiskReason.InputInvalid, inputs.Add("AvailableQuote", _inputs.AvailableQuote));
        }

        var capital = _inputs.CapitalAllocation;
        var maximum = _effective.MaxRiskPerTradePercent;
        var riskPercent = Math.Min(asked ?? maximum, maximum);
        var budget = capital * riskPercent / 100m;
        var stop = _stop!.Value;
        var distance = S * (_entry - stop);
        var (heldQuantity, heldEntry) = _add ? (Position.Quantity, Position.AverageEntry) : (0m, 0m);
        var heldRisk = Math.Max(0m, heldQuantity * S * (heldEntry - stop));

        inputs.Add("RiskPercent", riskPercent)
            .Add("CapitalAllocation", capital)
            .Add("Budget", budget)
            .Add("Entry", _entry)
            .Add("Stop", stop)
            .Add("StopDistance", distance)
            .Add("HeldQuantity", heldQuantity)
            .Add("HeldEntry", heldEntry)
            .Add("HeldRisk", heldRisk)
            .Add("AvailableQuote", _inputs.AvailableQuote);

        if (budget - heldRisk <= 0m)
        {
            return Trace.Fail(rule, RiskReason.BudgetUsed, inputs);
        }

        var limits = new List<(string Name, decimal Quantity)>
        {
            ("Risk", (budget - heldRisk) / distance),
            ("MaxPositionNotional", ((_effective.MaxPositionNotional ?? capital) - (heldQuantity * _entry)) / _entry),
        };

        if (_futures)
        {
            // Margin is notional ÷ leverage, and the agent commits at most its capital as margin.
            var leverage = _effective.MaxLeverage;
            limits.Add(("CapitalAllocation", ((capital * leverage) - (heldQuantity * heldEntry)) / _entry));
            limits.Add(("AvailableMargin", _inputs.AvailableQuote * leverage / _entry));

            var bracketCap = _inputs.Brackets.Where(bracket => bracket.InitialLeverage >= leverage).Select(bracket => (decimal?)bracket.NotionalCap).Max();

            if (bracketCap is { } cap)
            {
                limits.Add(("LeverageBracket", (cap - (heldQuantity * _entry)) / _entry));
            }
        }
        else
        {
            limits.Add(("CapitalAllocation", (capital - (heldQuantity * heldEntry)) / _entry));
            limits.Add(("AvailableBalance", _inputs.AvailableQuote / _entry));
        }

        if (MaxQuantity() is { } maxQuantity)
        {
            limits.Add(("MaxQuantity", maxQuantity));
        }

        if (_inputs.Filters.Notional?.Maximum is { } maxNotional && maxNotional > 0m)
        {
            limits.Add(("MaxNotional", maxNotional / _entry));
        }

        foreach (var (name, quantity) in limits)
        {
            inputs.Add("QuantityBy" + name, quantity);
        }

        var (binding, unrounded) = limits.MinBy(limit => limit.Quantity);
        inputs.Add("BindingLimit", binding);

        if (unrounded <= 0m)
        {
            return Trace.Fail(rule, RiskReason.NoCapacity, inputs);
        }

        var (step, origin) = Grid(market: _intent.EntryType == EntryType.Market);
        _quantity = Math.Max(0m, Down(unrounded, step, origin));
        _moneyAtRisk = heldRisk + (_quantity * distance);
        inputs.Add("StepSize", step).Add("Quantity", _quantity).Add("MoneyAtRisk", _moneyAtRisk);

        return Trace.Pass(rule, inputs);
    }

    /// <summary>
    /// The share asked for, rounded down. On Spot only the base asset the agent's own fills bought can be sold
    /// (FR-AGT-09), and no more than the account holds. A remainder too small to sell later is sold now.
    /// </summary>
    private RuleResult ExitSize()
    {
        const RiskRule rule = RiskRule.Size;
        var fraction = _intent.CloseFraction ?? 1m;
        var inputs = new Inputs()
            .Add("PositionQuantity", Position.Quantity)
            .Add("BaseHeld", _inputs.BaseHeld)
            .Add("CloseFraction", fraction);

        if (fraction is <= 0m or > 1m)
        {
            return Trace.Fail(rule, RiskReason.CloseFractionInvalid, inputs);
        }

        var closable = Position.Quantity;

        if (!_futures && _inputs.BaseHeld is { } held)
        {
            if (held < 0m)
            {
                return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
            }

            closable = Math.Min(closable, held);
        }

        var (step, origin) = Grid(market: true);
        var maxQuantity = MaxQuantity();

        decimal Fit(decimal quantity) => Math.Max(0m, Math.Min(Down(quantity, step, origin), maxQuantity ?? decimal.MaxValue));

        _quantity = Fit(fraction * closable);
        var remainder = closable - _quantity;
        var closesAll = false;

        if (fraction < 1m && remainder > 0m && TooSmallToSell(remainder))
        {
            _quantity = Fit(closable);
            closesAll = true;
        }

        inputs.Add("Closable", closable).Add("StepSize", step).Add("Quantity", _quantity).Add("ClosesAll", closesAll);

        return Trace.Pass(rule, inputs);
    }

    private bool TooSmallToSell(decimal quantity)
    {
        var minQuantity = MinQuantity();
        var minNotional = _inputs.Filters.Notional?.Minimum ?? 0m;

        // Futures reduce-only orders are exempt from the minimum notional, and without a bid it can't be measured.
        return (minQuantity > 0m && quantity < minQuantity)
            || (!_futures && minNotional > 0m && Prices.Bid > 0m && quantity * Prices.Bid < minNotional);
    }

    private RuleResult QuantityRule()
    {
        const RiskRule rule = RiskRule.Quantity;

        if (_kind is not (ActionKind.Entry or ActionKind.Exit))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Size))
        {
            return Trace.NotEvaluated(rule);
        }

        var minimum = MinQuantity();
        var inputs = new Inputs().Add("Quantity", _quantity).Add("MinQuantity", minimum);

        if (_quantity > 0m && _quantity >= minimum)
        {
            return Trace.Pass(rule, inputs);
        }

        return Trace.Fail(rule, _kind == ActionKind.Exit && !_futures ? RiskReason.Dust : RiskReason.QuantityTooSmall, inputs);
    }

    /// <summary>
    /// Binance measures notional at its own price, which may differ from the one here, so the minimum is checked
    /// at the lower of the entry and the last price, and the maximum at the higher.
    /// </summary>
    private RuleResult Notional()
    {
        const RiskRule rule = RiskRule.Notional;

        if (_kind is not (ActionKind.Entry or ActionKind.Exit))
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Size, RiskRule.Quantity))
        {
            return Trace.NotEvaluated(rule);
        }

        var filter = _inputs.Filters.Notional;
        var inputs = new Inputs()
            .Add("Quantity", _quantity)
            .Add("MinNotional", filter?.Minimum)
            .Add("MaxNotional", filter?.Maximum);

        if (_kind == ActionKind.Exit)
        {
            if (_futures)
            {
                return Trace.Pass(rule, inputs.Add("ReduceOnly", true));
            }

            inputs.Add("Bid", Prices.Bid).Add("Notional", _quantity * Prices.Bid);

            // Without a bid the notional can't be measured, and an exit is never held back for want of data:
            // Binance measures it again anyway.
            return filter is { Minimum: > 0m } && Prices.Bid > 0m && _quantity * Prices.Bid < filter.Minimum
                ? Trace.Fail(rule, RiskReason.Dust, inputs)
                : Trace.Pass(rule, inputs);
        }

        var last = Prices.Last > 0m ? Prices.Last : _entry;
        var low = _quantity * Math.Min(_entry, last);
        var high = _quantity * Math.Max(_entry, last);
        inputs.Add("Entry", _entry).Add("Last", Prices.Last).Add("NotionalLow", low).Add("NotionalHigh", high);

        return filter is null ? Trace.Pass(rule, inputs)
            : filter.Minimum > 0m && low < filter.Minimum ? Trace.Fail(rule, RiskReason.NotionalTooSmall, inputs)
            : filter.Maximum is > 0m && high > filter.Maximum ? Trace.Fail(rule, RiskReason.NotionalTooLarge, inputs)
            : Trace.Pass(rule, inputs);
    }

    /// <summary>
    /// The liquidation price must be at least the agent's multiple of the stop distance beyond the entry. It's
    /// Binance's isolated one-way formula, with the bracket for the whole position, and the margin the position
    /// would post at the agent's leverage: long LP = (Q·EP − WB − cum) ÷ (Q·(1 − MMR)), short
    /// LP = (Q·EP + WB + cum) ÷ (Q·(1 + MMR)). On an add, Q and EP are the combined position's.
    /// </summary>
    private RuleResult LiquidationBuffer()
    {
        const RiskRule rule = RiskRule.LiquidationBuffer;

        if (!Entry || !_futures)
        {
            return Trace.NotApplicable(rule);
        }

        if (!_trace.Passed(RiskRule.Size, RiskRule.Quantity))
        {
            return Trace.NotEvaluated(rule);
        }

        var leverage = _effective.MaxLeverage;
        var multiple = _effective.LiquidationBufferMultiple;
        var (heldQuantity, heldEntry) = _add ? (Position.Quantity, Position.AverageEntry) : (0m, 0m);
        var quantity = heldQuantity + _quantity;
        var entry = ((heldQuantity * heldEntry) + (_quantity * _entry)) / quantity;
        var notional = quantity * Math.Max(entry, Prices.Mark ?? entry);
        var stop = _stop!.Value;
        var inputs = new Inputs()
            .Add("Leverage", leverage)
            .Add("PositionQuantity", quantity)
            .Add("PositionEntry", entry)
            .Add("PositionNotional", notional)
            .Add("Stop", stop)
            .Add("LiquidationBufferMultiple", multiple);

        if (_inputs.Brackets.Count == 0)
        {
            return Trace.Fail(rule, RiskReason.LiquidationUnknown, inputs.Add("Brackets", "none"));
        }

        // A bracket's cap is the most notional its leverage allows, which is what Size caps at, so a notional
        // exactly at a cap belongs to that bracket rather than the next.
        var bracket = _inputs.Brackets
            .Where(candidate => candidate.NotionalFloor <= notional && notional <= candidate.NotionalCap)
            .OrderBy(candidate => candidate.NotionalFloor)
            .FirstOrDefault();

        if (bracket is null)
        {
            return Trace.Fail(rule, RiskReason.LiquidationUnknown, inputs.Add("Bracket", "none"));
        }

        inputs.Add("MaintenanceMarginRate", bracket.MaintenanceMarginRate)
            .Add("MaintenanceAmount", bracket.MaintenanceAmount)
            .Add("BracketLeverage", bracket.InitialLeverage);

        if (bracket.MaintenanceMarginRate is < 0m or >= 1m || leverage < 1)
        {
            return Trace.Fail(rule, RiskReason.InputInvalid, inputs);
        }

        if (bracket.InitialLeverage < leverage)
        {
            return Trace.Fail(rule, RiskReason.LeverageAboveBracket, inputs);
        }

        var wallet = quantity * entry / leverage;
        var cum = bracket.MaintenanceAmount;
        var rate = bracket.MaintenanceMarginRate;
        var liquidation = _side == PositionSide.Long
            ? ((quantity * entry) - wallet - cum) / (quantity * (1m - rate))
            : ((quantity * entry) + wallet + cum) / (quantity * (1m + rate));

        inputs.Add("WalletBalance", wallet);

        // A long with enough margin can't be liquidated at any price.
        if (_side == PositionSide.Long && liquidation <= 0m)
        {
            _liquidation = null;

            return Trace.Pass(rule, inputs.Add("LiquidationPrice", "none"));
        }

        _liquidation = liquidation;
        var liquidationDistance = S * (entry - liquidation);
        var stopDistance = S * (entry - stop);
        inputs.Add("LiquidationPrice", liquidation)
            .Add("LiquidationDistance", liquidationDistance)
            .Add("StopDistance", stopDistance);

        return liquidationDistance >= multiple * stopDistance
            ? Trace.Pass(rule, inputs)
            : Trace.Fail(rule, RiskReason.LiquidationTooClose, inputs);
    }

    /// <summary>
    /// The step and its origin. A market order uses MARKET_LOT_SIZE's, unless its step is 0, which means
    /// LOT_SIZE's applies. A limit order uses LOT_SIZE's.
    /// </summary>
    private (decimal Step, decimal Origin) Grid(bool market)
    {
        var filters = _inputs.Filters;

        if (market && filters.MarketLot is { StepSize: > 0m } marketLot)
        {
            return (marketLot.StepSize, marketLot.Minimum);
        }

        return filters.Lot is { } lot ? (lot.StepSize, lot.Minimum) : (0m, 0m);
    }

    /// <summary>
    /// The smaller maximum of both lot filters, so that the position can always be closed with a market order.
    /// Null when neither sets one.
    /// </summary>
    private decimal? MaxQuantity()
    {
        var filters = _inputs.Filters;
        decimal? maximum = null;

        foreach (var lot in new[] { filters.Lot, filters.MarketLot })
        {
            if (lot is { Maximum: > 0m } && (maximum is null || lot.Maximum < maximum))
            {
                maximum = lot.Maximum;
            }
        }

        return maximum;
    }

    /// <summary>The larger minimum of both lot filters.</summary>
    private decimal MinQuantity() =>
        Math.Max(_inputs.Filters.Lot?.Minimum ?? 0m, _inputs.Filters.MarketLot?.Minimum ?? 0m);

    private SizedOrder Sized()
    {
        var closing = _side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        var market = _inputs.Market;

        return _kind switch
        {
            ActionKind.Entry => new SizedOrder(
                SizedOrderKind.Entry,
                _intent.Action,
                _intent.Origin,
                market,
                _side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell,
                reduceOnly: false,
                entryType: _intent.EntryType,
                quantity: _quantity,
                limitPrice: _intent.EntryType == EntryType.Limit ? _entry : null,
                stopPrice: _stop,
                targetPrice: _target,
                notional: _quantity * _entry,
                moneyAtRisk: _moneyAtRisk,
                rewardToRisk: _target is { } target ? Normalize((target - _entry) / (_entry - _stop!.Value)) : null,
                leverage: _futures ? _effective.MaxLeverage : null,
                estimatedLiquidationPrice: _liquidation),
            ActionKind.Exit => new SizedOrder(
                SizedOrderKind.Exit, _intent.Action, _intent.Origin, market, closing, reduceOnly: true, quantity: _quantity),
            ActionKind.AdjustStop => new SizedOrder(
                SizedOrderKind.StopAdjustment, _intent.Action, _intent.Origin, market, closing, reduceOnly: true, quantity: Position.Quantity, stopPrice: _stop),
            ActionKind.AdjustTarget => new SizedOrder(
                SizedOrderKind.TargetAdjustment, _intent.Action, _intent.Origin, market, closing, reduceOnly: true, quantity: Position.Quantity, targetPrice: _target),
            _ => new SizedOrder(SizedOrderKind.CancelEntry, _intent.Action, _intent.Origin, market, side: null, reduceOnly: false),
        };
    }
}
