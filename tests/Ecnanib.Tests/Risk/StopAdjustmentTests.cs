using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>FR-RISK-06: a stop may only move toward breakeven or profit. Widening it is rejected.</summary>
public sealed class StopAdjustmentTests
{
    private static OrderIntent MoveStop(decimal? stop) => new() { Action = IntentAction.AdjustStop, StopPrice = stop };

    private static RiskInputs Holding(PositionSide side, decimal stop) =>
        Futures() with { Position = new OpenPosition(side, 0.5m, 1000m, Stop: stop) };

    [Fact]
    public void Tightening_a_long_stop_passes()
    {
        var order = Evaluate(MoveStop(990m), Holding(PositionSide.Long, 980m)).Passes();

        Assert.Equal(SizedOrderKind.StopAdjustment, order.Kind);
        Assert.Equal(990m, order.StopPrice);
        Assert.Equal(0.5m, order.Quantity);
        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.True(order.ReduceOnly);
    }

    [Theory]
    [InlineData(PositionSide.Long, "980", "975", RiskReason.StopWidened)]
    [InlineData(PositionSide.Long, "980", "980", RiskReason.StopUnchanged)]
    [InlineData(PositionSide.Short, "1020", "1025", RiskReason.StopWidened)]
    [InlineData(PositionSide.Short, "1020", "1020", RiskReason.StopUnchanged)]
    public void Widening_or_keeping_a_stop_fails(PositionSide side, string current, string moved, RiskReason reason)
    {
        var inputs = Holding(side, decimal.Parse(current, System.Globalization.CultureInfo.InvariantCulture));

        Evaluate(MoveStop(decimal.Parse(moved, System.Globalization.CultureInfo.InvariantCulture)), inputs)
            .FailsOn(RiskRule.StopNotWidened, reason);
    }

    [Fact]
    public void Tightening_a_short_stop_passes()
    {
        var order = Evaluate(MoveStop(1010m), Holding(PositionSide.Short, 1020m)).Passes();

        Assert.Equal(1010m, order.StopPrice);
        Assert.Equal(OrderSide.Buy, order.Side);
    }

    [Fact]
    public void A_stop_past_the_mark_price_would_fire_at_once_and_fails()
    {
        Evaluate(MoveStop(1001m), Holding(PositionSide.Long, 980m)).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
        Evaluate(MoveStop(995m), Holding(PositionSide.Short, 1020m)).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
    }

    [Fact]
    public void A_spot_stop_must_be_below_both_the_last_price_and_the_bid()
    {
        var inputs = Spot() with { Position = new OpenPosition(PositionSide.Long, 2m, 95m, Stop: 93m), BaseHeld = 2m };

        Evaluate(MoveStop(99.95m), inputs).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
        Evaluate(MoveStop(99.89m), inputs).Passes();
    }

    [Fact]
    public void A_moved_stop_rounds_toward_the_price_so_it_can_only_tighten()
    {
        var longOrder = Evaluate(MoveStop(990.04m), Holding(PositionSide.Long, 980m)).Passes();
        var shortOrder = Evaluate(MoveStop(1010.06m), Holding(PositionSide.Short, 1020m)).Passes();

        Assert.Equal(990.1m, longOrder.StopPrice);
        Assert.Equal(1010m, shortOrder.StopPrice);
    }

    [Fact]
    public void A_stop_that_rounds_back_onto_the_current_one_is_unchanged_not_widened()
    {
        // 990.05 rounds up to 990.1, where the stop already is.
        Evaluate(MoveStop(990.05m), Holding(PositionSide.Long, 990.1m)).FailsOn(RiskRule.StopNotWidened, RiskReason.StopUnchanged);
    }

    [Fact]
    public void The_ATR_bounds_do_not_apply_to_a_trailing_stop()
    {
        // 1 below the mark is 0.1 ATR, far under an entry's minimum of 0.5.
        var evaluation = Evaluate(MoveStop(999m), Holding(PositionSide.Long, 980m));

        evaluation.Passes();
        Assert.Equal(RuleOutcome.NotApplicable, evaluation.Rule(RiskRule.StopDistance).Outcome);
    }

    [Fact]
    public void Moving_a_stop_needs_a_stop_and_a_position()
    {
        Evaluate(MoveStop(null), Holding(PositionSide.Long, 980m)).FailsOn(RiskRule.StopRequired, RiskReason.StopMissing);
        Evaluate(MoveStop(990m), Futures()).FailsOn(RiskRule.Action, RiskReason.NoPosition);
    }

    [Fact]
    public void A_futures_stop_needs_the_mark_price()
    {
        var inputs = Holding(PositionSide.Long, 980m) with { Prices = Futures().Prices with { Mark = null } };

        Evaluate(MoveStop(990m), inputs).FailsOn(RiskRule.StopSide, RiskReason.MarkPriceMissing);
    }

    [Fact]
    public void A_position_without_a_stop_may_gain_one()
    {
        var inputs = Futures() with { Position = new OpenPosition(PositionSide.Long, 0.5m, 1000m) };

        Evaluate(MoveStop(990m), inputs).Passes();
    }
}
