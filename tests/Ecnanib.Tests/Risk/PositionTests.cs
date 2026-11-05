using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// Actions on a position: adds (FR-RISK-07, FR-RISK-06), exits that are never blocked by policy (AC-01, including
/// the kill switch and the emergency close), target changes and cancelling an unfilled entry.
/// </summary>
public sealed class PositionTests
{
    private static OrderIntent Sell(decimal? fraction = null, IntentOrigin origin = IntentOrigin.Council) =>
        new() { Action = IntentAction.Sell, CloseFraction = fraction, Origin = origin };

    private static OrderIntent Close(decimal? fraction = null, IntentOrigin origin = IntentOrigin.Council) =>
        new() { Action = IntentAction.Close, CloseFraction = fraction, Origin = origin };

    private static RiskInputs SpotHolding(decimal quantity, decimal entry = 95m, decimal? baseHeld = null) =>
        Spot() with { Position = new OpenPosition(PositionSide.Long, quantity, entry, Stop: 93m), BaseHeld = baseHeld ?? quantity };

    private static RiskInputs FuturesHolding(PositionSide side, decimal quantity, decimal entry, decimal stop) =>
        Futures() with { Position = new OpenPosition(side, quantity, entry, Stop: stop) };

    [Fact]
    public void Spot_cannot_short_and_futures_cannot_buy()
    {
        Evaluate(FuturesShort(), Spot()).FailsOn(RiskRule.Action, RiskReason.ActionNotForMarket);
        Evaluate(SpotBuy(), Futures()).FailsOn(RiskRule.Action, RiskReason.ActionNotForMarket);
        Evaluate(Close(), SpotHolding(2m)).FailsOn(RiskRule.Action, RiskReason.ActionNotForMarket);
    }

    [Fact]
    public void An_entry_against_the_open_position_fails()
    {
        var inputs = FuturesHolding(PositionSide.Short, 0.5m, 1010m, 1030m);

        Evaluate(FuturesLong(), inputs).FailsOn(RiskRule.Action, RiskReason.OppositePosition);
    }

    [Theory]
    [InlineData(PositionSide.Long, "1010", null, false)]
    [InlineData(PositionSide.Long, "1000", null, false)]
    [InlineData(PositionSide.Long, "990", "1000.5", false)]
    [InlineData(PositionSide.Long, "990", null, true)]
    [InlineData(PositionSide.Short, "990", null, false)]
    [InlineData(PositionSide.Short, "1010", null, true)]
    public void Adding_needs_the_position_in_profit_at_the_mark(PositionSide side, string entry, string? breakEven, bool passes)
    {
        // The mark is 1,000. Break-even counts as a loss, and Binance's break-even price wins when known.
        var stop = side == PositionSide.Long ? 975m : 1025m;
        var position = new OpenPosition(
            side,
            0.2m,
            decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture),
            breakEven is null ? null : decimal.Parse(breakEven, System.Globalization.CultureInfo.InvariantCulture),
            Stop: stop);
        var intent = side == PositionSide.Long ? FuturesLong() : FuturesShort();

        var evaluation = Evaluate(intent, Futures() with { Position = position });

        if (passes)
        {
            evaluation.Passes();
            Assert.Equal(RuleOutcome.Pass, evaluation.Rule(RiskRule.NoAveragingDown).Outcome);
        }
        else
        {
            evaluation.FailsOn(RiskRule.NoAveragingDown, RiskReason.AveragingDown);
        }
    }

    [Fact]
    public void A_spot_add_is_judged_at_the_bid()
    {
        // Bought at 99.95: the ask of 100 is above it, but the bid of 99.9 it would sell at isn't.
        Evaluate(SpotBuy(), SpotHolding(1m, entry: 99.95m)).FailsOn(RiskRule.NoAveragingDown, RiskReason.AveragingDown);
    }

    [Fact]
    public void An_add_may_keep_the_stop_but_not_widen_it()
    {
        var inputs = FuturesHolding(PositionSide.Long, 0.2m, 990m, 985m);

        var same = Evaluate(FuturesLong() with { StopPrice = 985m }, inputs);
        var wider = Evaluate(FuturesLong() with { StopPrice = 980m }, inputs);

        // 0.2 held at 990 risks 1 at 985, leaving 9 for 15 a unit.
        Assert.Equal(0.6m, same.Passes().Quantity);
        Assert.Equal(10m, same.Order!.MoneyAtRisk);
        wider.FailsOn(RiskRule.StopNotWidened, RiskReason.StopWidened);
    }

    [Fact]
    public void A_spot_sell_closes_the_position_by_default()
    {
        var order = Evaluate(Sell(), SpotHolding(2m)).Passes();

        Assert.Equal(SizedOrderKind.Exit, order.Kind);
        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.Equal(2m, order.Quantity);
        Assert.True(order.ReduceOnly);
    }

    [Fact]
    public void A_partial_sell_rounds_down_to_the_step()
    {
        Assert.Equal(1m, Evaluate(Sell(0.5m), SpotHolding(2m)).Passes().Quantity);
        Assert.Equal(0.666m, Evaluate(Sell(0.333m), SpotHolding(2m)).Passes().Quantity);
    }

    [Fact]
    public void A_remainder_too_small_to_sell_later_is_sold_now()
    {
        // 0.99 of 2 leaves 0.02, worth 2 at the bid, under the minimum notional of 5.
        var evaluation = Evaluate(Sell(0.99m), SpotHolding(2m));

        Assert.Equal(2m, evaluation.Passes().Quantity);
        Assert.Equal("true", evaluation.Rule(RiskRule.Size).Input("ClosesAll"));
    }

    [Fact]
    public void Spot_sells_only_what_the_account_still_holds()
    {
        Assert.Equal(1.5m, Evaluate(Sell(), SpotHolding(2m, baseHeld: 1.5m)).Passes().Quantity);
    }

    [Fact]
    public void A_spot_position_too_small_to_sell_is_dust()
    {
        // 0.04 is worth 3.996 at the bid, under the minimum notional of 5.
        Evaluate(Sell(), SpotHolding(0.04m)).FailsOn(RiskRule.Notional, RiskReason.Dust);

        var bigLot = SpotHolding(0.005m) with { Filters = Spot().Filters with { Lot = new LotRule(0.01m, 9000m, 0.001m) } };
        Evaluate(Sell(), bigLot).FailsOn(RiskRule.Quantity, RiskReason.Dust);
    }

    [Fact]
    public void A_futures_close_is_reduce_only_and_exempt_from_the_minimum_notional()
    {
        var full = Evaluate(Close(), FuturesHolding(PositionSide.Long, 0.5m, 990m, 980m)).Passes();
        var partial = Evaluate(Close(0.3m), FuturesHolding(PositionSide.Long, 0.5m, 990m, 980m)).Passes();
        var small = Evaluate(Close(), FuturesHolding(PositionSide.Short, 0.05m, 1010m, 1020m)).Passes();

        Assert.Equal(0.5m, full.Quantity);
        Assert.Equal(OrderSide.Sell, full.Side);
        Assert.True(full.ReduceOnly);
        Assert.Equal(0.15m, partial.Quantity);
        Assert.Equal(0.05m, small.Quantity);
        Assert.Equal(OrderSide.Buy, small.Side);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-0.5")]
    public void A_close_fraction_outside_zero_to_one_fails(string fraction)
    {
        var evaluation = Evaluate(Close(decimal.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture)), FuturesHolding(PositionSide.Long, 0.5m, 990m, 980m));

        evaluation.FailsOn(RiskRule.Size, RiskReason.CloseFractionInvalid);
    }

    [Fact]
    public void An_exit_without_a_position_fails()
    {
        Evaluate(Close(), Futures()).FailsOn(RiskRule.Action, RiskReason.NoPosition);
        Evaluate(Sell(), Spot()).FailsOn(RiskRule.Action, RiskReason.NoPosition);
    }

    [Fact]
    public void The_kill_switch_close_passes_whatever_the_limits_say()
    {
        // Everything that blocks an entry is set: losses, a streak, the cooldown, a pending entry, no ATR, a symbol
        // that stopped trading, invalid settings and no capital.
        var inputs = FuturesHolding(PositionSide.Long, 0.5m, 1100m, 1050m) with
        {
            Settings = RiskSettings.Default with { MaxNewEntriesPerDay = 0 },
            CapitalAllocation = 0m,
            Atr = null,
            HasPendingEntry = true,
            Funding = new FundingInfo(0.01m),
            Filters = Futures().Filters with { Trading = false },
            Record = new TradingRecord(-1000m, -1000m, 20, 20, 0),
        };

        var evaluation = Evaluate(Close(origin: IntentOrigin.System), inputs);
        var order = evaluation.Passes();

        Assert.Equal(IntentOrigin.System, order.Origin);
        Assert.Equal(0.5m, order.Quantity);
        Assert.Null(evaluation.Breach);
        Assert.Equal(RuleOutcome.NotApplicable, evaluation.Rule(RiskRule.Settings).Outcome);
    }

    [Fact]
    public void A_spot_sell_without_a_quote_is_not_held_back()
    {
        var inputs = SpotHolding(2m) with { Prices = new MarketPrices(0m, 0m, 0m) };

        Assert.Equal(2m, Evaluate(Sell(origin: IntentOrigin.System), inputs).Passes().Quantity);
    }

    [Fact]
    public void A_target_can_be_moved_above_the_price_or_removed()
    {
        var inputs = SpotHolding(2m);

        var moved = Evaluate(new OrderIntent { Action = IntentAction.AdjustTarget, TargetPrice = 103.005m }, inputs).Passes();
        var removed = Evaluate(new OrderIntent { Action = IntentAction.AdjustTarget }, inputs).Passes();

        Assert.Equal(SizedOrderKind.TargetAdjustment, moved.Kind);
        Assert.Equal(103m, moved.TargetPrice);
        Assert.Equal(2m, moved.Quantity);
        Assert.Null(removed.TargetPrice);
        Evaluate(new OrderIntent { Action = IntentAction.AdjustTarget, TargetPrice = 99m }, inputs)
            .FailsOn(RiskRule.TargetSide, RiskReason.TargetWrongSide);
    }

    [Fact]
    public void Cancelling_needs_an_unfilled_entry()
    {
        var cancel = new OrderIntent { Action = IntentAction.CancelPending };

        var order = Evaluate(cancel, Spot() with { HasPendingEntry = true }).Passes();

        Assert.Equal(SizedOrderKind.CancelEntry, order.Kind);
        Assert.Null(order.Side);
        Evaluate(cancel, Spot()).FailsOn(RiskRule.Action, RiskReason.NoPendingEntry);
    }

    [Fact]
    public void A_position_that_cannot_be_right_fails()
    {
        var empty = Futures() with { Position = new OpenPosition(PositionSide.Long, 0m, 1000m) };
        var spotShort = Spot() with { Position = new OpenPosition(PositionSide.Short, 1m, 100m) };

        Evaluate(Close(), empty).FailsOn(RiskRule.Action, RiskReason.InputInvalid);
        Evaluate(Sell(), spotShort).FailsOn(RiskRule.Action, RiskReason.InputInvalid);
    }
}
