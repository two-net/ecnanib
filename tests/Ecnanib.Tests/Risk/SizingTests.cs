using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-03 and NFR-MNT-04's position-sizing math: quantity = (capital × risk %) ÷ |entry − stop|, capped by
/// maximum notional, available balance or margin, and leverage, then rounded down to the step.
/// </summary>
public sealed class SizingTests
{
    [Fact]
    public void A_spot_buy_risks_exactly_the_budget_at_the_stop()
    {
        var order = Evaluate(SpotBuy(), Spot()).Passes();

        Assert.Equal(SizedOrderKind.Entry, order.Kind);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(5m, order.Quantity);
        Assert.Equal(500m, order.Notional);
        Assert.Equal(10m, order.MoneyAtRisk);
        Assert.Equal(98m, order.StopPrice);
        Assert.Equal(104m, order.TargetPrice);
        Assert.Equal(2m, order.RewardToRisk);
        Assert.False(order.ReduceOnly);
        Assert.Null(order.Leverage);
        Assert.Null(order.LimitPrice);
    }

    [Fact]
    public void A_futures_long_and_short_size_from_the_ask_and_the_bid()
    {
        var longOrder = Evaluate(FuturesLong(), Futures()).Passes();
        var shortOrder = Evaluate(FuturesShort(), Futures()).Passes();

        Assert.Equal(0.5m, longOrder.Quantity);
        Assert.Equal(10m, longOrder.MoneyAtRisk);
        Assert.Equal(2, longOrder.Leverage);
        Assert.Equal(OrderSide.Buy, longOrder.Side);

        // Bid 999.9, stop 1020: 20.1 a unit, so 10 ÷ 20.1 = 0.4975…, rounded down to 0.497.
        Assert.Equal(0.497m, shortOrder.Quantity);
        Assert.Equal(OrderSide.Sell, shortOrder.Side);
        Assert.Equal(0.497m * 20.1m, shortOrder.MoneyAtRisk);
    }

    [Theory]
    [InlineData("0.5", "2.5")]
    [InlineData("1", "5")]
    [InlineData("5", "5")]
    public void A_requested_risk_is_used_up_to_the_maximum(string asked, string quantity)
    {
        var evaluation = Evaluate(SpotBuy() with { RiskPercent = decimal.Parse(asked, System.Globalization.CultureInfo.InvariantCulture) }, Spot());

        Assert.Equal(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), evaluation.Passes().Quantity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_requested_risk_of_zero_or_less_fails(string asked)
    {
        var evaluation = Evaluate(SpotBuy() with { RiskPercent = decimal.Parse(asked, System.Globalization.CultureInfo.InvariantCulture) }, Spot());

        evaluation.FailsOn(RiskRule.Size, RiskReason.RiskPercentInvalid);
    }

    [Fact]
    public void The_global_cap_on_risk_per_trade_limits_an_agent_set_above_it()
    {
        // 3% asked for by the settings, but the cap is 2%: a budget of 20, not 30.
        var inputs = Spot() with { Settings = RiskSettings.Default with { MaxRiskPerTradePercent = 3m } };

        var evaluation = Evaluate(SpotBuy(), inputs);

        Assert.Equal(10m, evaluation.Passes().Quantity);
        Assert.Equal(2m, evaluation.Effective.MaxRiskPerTradePercent);
        Assert.Equal("Risk", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void The_maximum_position_notional_caps_the_size()
    {
        var inputs = Spot() with { Settings = RiskSettings.Default with { MaxPositionNotional = 300m } };

        var evaluation = Evaluate(SpotBuy(), inputs);

        Assert.Equal(3m, evaluation.Passes().Quantity);
        Assert.Equal("MaxPositionNotional", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void Without_a_maximum_notional_the_capital_allocation_is_the_cap()
    {
        // 10% risk on a 1-unit stop wants 100 units, but the capital buys 10.
        var inputs = Spot() with { Settings = RiskSettings.Default with { MaxRiskPerTradePercent = 2m } };
        var intent = SpotBuy() with { StopPrice = 99.8m, TargetPrice = null };

        var evaluation = Evaluate(intent, inputs with { Atr = 0.4m });

        Assert.Equal(10m, evaluation.Passes().Quantity);
    }

    [Fact]
    public void The_available_balance_caps_a_spot_buy()
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { AvailableQuote = 250m });

        Assert.Equal(2.5m, evaluation.Passes().Quantity);
        Assert.Equal("AvailableBalance", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void The_available_margin_times_leverage_caps_a_futures_entry()
    {
        // 100 of margin at 2x buys 200 of notional, 0.2 units at 1,000.
        var evaluation = Evaluate(FuturesLong(), Futures() with { AvailableQuote = 100m });

        Assert.Equal(0.2m, evaluation.Passes().Quantity);
        Assert.Equal("AvailableMargin", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void Futures_capital_caps_margin_rather_than_notional()
    {
        // No notional cap of its own, so the capital at 2x leverage allows 2,000 of notional, 2 units.
        var inputs = Futures() with
        {
            Settings = RiskSettings.Default with { MaxPositionNotional = 1_000_000m, MaxRiskPerTradePercent = 2m },
        };
        var intent = FuturesLong() with { StopPrice = 995m, TargetPrice = null };

        var evaluation = Evaluate(intent, inputs with { Atr = 2m });

        Assert.Equal(2m, evaluation.Passes().Quantity);
        Assert.Equal("CapitalAllocation", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void A_leverage_bracket_caps_the_notional_it_allows_at_the_leverage()
    {
        // Only a bracket up to 300 of notional allows 2x.
        var inputs = Futures() with { Brackets = [new LeverageBracket(0m, 300m, 0.004m, 0m, 2), new LeverageBracket(300m, 10_000m, 0.01m, 2m, 1)] };

        var evaluation = Evaluate(FuturesLong(), inputs);

        Assert.Equal(0.3m, evaluation.Passes().Quantity);
        Assert.Equal("LeverageBracket", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void The_smaller_maximum_of_both_lot_filters_caps_the_quantity()
    {
        var inputs = Spot() with
        {
            CapitalAllocation = 1_000_000m,
            AvailableQuote = 1_000_000m,
            Filters = Spot().Filters with { MarketLot = new LotRule(0m, 40m, 0m) },
        };

        var evaluation = Evaluate(SpotBuy(), inputs);

        Assert.Equal(40m, evaluation.Passes().Quantity);
        Assert.Equal("MaxQuantity", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void No_room_under_a_cap_fails_without_a_quantity()
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { AvailableQuote = 0m });

        evaluation.FailsOn(RiskRule.Size, RiskReason.NoCapacity);
    }

    [Fact]
    public void A_quantity_is_rounded_down_never_up()
    {
        // Stop 97 risks 3 a unit: 3.3333… rounds down to 3.333.
        var evaluation = Evaluate(SpotBuy() with { StopPrice = 97m, TargetPrice = null }, Spot());
        var order = evaluation.Passes();

        Assert.Equal(3.333m, order.Quantity);
        Assert.True(order.MoneyAtRisk <= 10m);
        Assert.Equal(9.999m, order.MoneyAtRisk);
    }

    [Fact]
    public void An_add_spends_only_what_the_position_does_not_already_risk_at_the_new_stop()
    {
        // 2 units bought at 95, now 100. At a stop of 98 they're 3 in profit each, so they risk nothing: the add gets
        // the whole budget. At a stop of 94, they'd risk 2 units × 1 = 2, leaving 8 for 6 a unit.
        var position = new OpenPosition(PositionSide.Long, 2m, 95m, Stop: 93m);
        var inputs = Spot() with { Position = position, BaseHeld = 2m, Atr = 3m };

        var atNinetyEight = Evaluate(SpotBuy() with { TargetPrice = null }, inputs);
        var atNinetyFour = Evaluate(SpotBuy() with { StopPrice = 94m, TargetPrice = null }, inputs);

        Assert.Equal(5m, atNinetyEight.Passes().Quantity);
        Assert.Equal("0", atNinetyEight.Rule(RiskRule.Size).Input("HeldRisk"));
        Assert.Equal(1.333m, atNinetyFour.Passes().Quantity);
        Assert.Equal("2", atNinetyFour.Rule(RiskRule.Size).Input("HeldRisk"));
        Assert.Equal(2m + (1.333m * 6m), atNinetyFour.Order!.MoneyAtRisk);
    }

    [Fact]
    public void An_add_counts_the_position_against_the_capital_and_notional()
    {
        // 8 units held at 95 cost 760, so 240 of capital is left: 2.4 units at 100.
        var position = new OpenPosition(PositionSide.Long, 8m, 95m, Stop: 93m);
        var inputs = Spot() with { Position = position, BaseHeld = 8m, Settings = RiskSettings.Default with { MaxPositionNotional = 2000m } };

        var evaluation = Evaluate(SpotBuy() with { TargetPrice = null }, inputs);

        Assert.Equal(2.4m, evaluation.Passes().Quantity);
        Assert.Equal("CapitalAllocation", evaluation.Rule(RiskRule.Size).Input("BindingLimit"));
    }

    [Fact]
    public void A_position_that_already_risks_the_budget_leaves_no_room_to_add()
    {
        // 10 units at 99.5, a new stop of 98: 15 at risk, above the budget of 10.
        var position = new OpenPosition(PositionSide.Long, 10m, 99.5m, Stop: 97m);
        var inputs = Spot() with
        {
            Position = position,
            BaseHeld = 10m,
            Settings = RiskSettings.Default with { MaxPositionNotional = 5000m },
            CapitalAllocation = 1000m,
        };

        var evaluation = Evaluate(SpotBuy() with { TargetPrice = null }, inputs);

        evaluation.FailsOn(RiskRule.Size, RiskReason.BudgetUsed);
    }

    [Theory]
    [InlineData("97.13", "1.5")]
    [InlineData("98.5", "1")]
    [InlineData("96.01", "2")]
    [InlineData("98.47", "0.5")]
    [InlineData("92.5", "0.3")]
    public void Money_at_risk_never_exceeds_the_budget(string stop, string riskPercent)
    {
        var inputs = Spot() with { Atr = 3m, Settings = RiskSettings.Default with { MaxRiskPerTradePercent = 2m } };
        var intent = SpotBuy() with
        {
            StopPrice = decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture),
            TargetPrice = null,
            RiskPercent = decimal.Parse(riskPercent, System.Globalization.CultureInfo.InvariantCulture),
        };

        var order = Evaluate(intent, inputs).Passes();
        var budget = 1000m * intent.RiskPercent!.Value / 100m;

        Assert.True(order.MoneyAtRisk <= budget, $"{order.MoneyAtRisk} > {budget}");
        Assert.Equal(order.Quantity!.Value * (100m - intent.StopPrice!.Value), order.MoneyAtRisk);
    }
}
