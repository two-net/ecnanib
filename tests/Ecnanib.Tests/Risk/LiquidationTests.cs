using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-02's liquidation buffer: the entry-to-liquidation distance is at least the agent's multiple of the
/// entry-to-stop distance, with the liquidation price from Binance's isolated one-way formula.
/// </summary>
public sealed class LiquidationTests
{
    private static readonly LeverageBracket[] WithMaintenanceAmount = [new LeverageBracket(0m, 1_000_000m, 0.01m, 5m, 125)];

    private static readonly LeverageBracket[] NoMaintenance = [new LeverageBracket(0m, 1_000_000m, 0m, 0m, 125)];

    [Fact]
    public void A_long_liquidation_price_follows_the_isolated_formula()
    {
        // 0.5 at 1,000 with 2x posts 250. (500 − 250 − 0) ÷ (0.5 × (1 − 0.004)).
        var order = Evaluate(FuturesLong(), Futures()).Passes();

        Assert.Equal(250m / 0.498m, order.EstimatedLiquidationPrice);
    }

    [Fact]
    public void The_maintenance_amount_moves_the_liquidation_price()
    {
        var longOrder = Evaluate(FuturesLong(), Futures() with { Brackets = WithMaintenanceAmount }).Passes();
        var shortOrder = Evaluate(FuturesShort(), Futures() with { Brackets = WithMaintenanceAmount }).Passes();

        // Long: (500 − 250 − 5) ÷ (0.5 × 0.99). Short: 0.497 at 999.9, (Q·EP + Q·EP ÷ 2 + 5) ÷ (Q × 1.01).
        Assert.Equal(245m / 0.495m, longOrder.EstimatedLiquidationPrice);
        var notional = 0.497m * 999.9m;
        Assert.Equal((notional + (notional / 2m) + 5m) / (0.497m * 1.01m), shortOrder.EstimatedLiquidationPrice);
    }

    [Theory]
    [InlineData("950", true)]
    [InlineData("949.9", false)]
    public void The_buffer_passes_at_exactly_the_multiple(string stop, bool passes)
    {
        // At 5x with no maintenance margin, a long's liquidation is 1/5 below the entry: 200 below 1,000. With a
        // multiple of 4, the stop may be at most 50 below.
        var inputs = Futures() with
        {
            Settings = RiskSettings.Default with { MaxLeverage = 5, LiquidationBufferMultiple = 4m },
            Atr = 20m,
            Brackets = NoMaintenance,
        };
        var intent = FuturesLong() with { StopPrice = decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture), TargetPrice = null };

        var evaluation = Evaluate(intent, inputs);

        if (passes)
        {
            Assert.Equal(800m, evaluation.Passes().EstimatedLiquidationPrice);
        }
        else
        {
            evaluation.FailsOn(RiskRule.LiquidationBuffer, RiskReason.LiquidationTooClose);
        }
    }

    [Fact]
    public void The_global_minimum_buffer_raises_an_agent_set_below_it()
    {
        // An agent can't save a buffer of 1.5 under the cap of 2, but one saved before the cap is held to 2.
        var inputs = Futures() with { Settings = RiskSettings.Default with { LiquidationBufferMultiple = 1.5m } };

        var evaluation = Evaluate(FuturesLong(), inputs);

        Assert.Equal("2", evaluation.Rule(RiskRule.LiquidationBuffer).Input("LiquidationBufferMultiple"));
    }

    [Fact]
    public void Without_brackets_a_futures_entry_fails()
    {
        Evaluate(FuturesLong(), Futures() with { Brackets = [] }).FailsOn(RiskRule.LiquidationBuffer, RiskReason.LiquidationUnknown);
    }

    [Fact]
    public void The_bracket_is_chosen_by_the_position_notional()
    {
        // 500 of notional falls in the second bracket, with its 40% maintenance margin.
        var inputs = Futures() with
        {
            Brackets = [new LeverageBracket(0m, 400m, 0.004m, 0m, 125), new LeverageBracket(400m, 1_000_000m, 0.4m, 0m, 2)],
        };

        var evaluation = Evaluate(FuturesLong(), inputs);

        Assert.Equal("0.4", evaluation.Rule(RiskRule.LiquidationBuffer).Input("MaintenanceMarginRate"));
        Assert.Equal(250m / 0.3m, evaluation.Passes().EstimatedLiquidationPrice);
    }

    [Fact]
    public void A_leverage_above_the_bracket_fails()
    {
        var inputs = Futures() with { Brackets = [new LeverageBracket(0m, 1_000_000m, 0.004m, 0m, 1)] };

        Evaluate(FuturesLong(), inputs).FailsOn(RiskRule.LiquidationBuffer, RiskReason.LeverageAboveBracket);
    }

    [Fact]
    public void A_long_at_1x_cannot_be_liquidated()
    {
        var inputs = Futures() with { Settings = RiskSettings.Default with { MaxLeverage = 1 }, Brackets = NoMaintenance };

        var evaluation = Evaluate(FuturesLong(), inputs);

        Assert.Null(evaluation.Passes().EstimatedLiquidationPrice);
        Assert.Equal(1, evaluation.Order!.Leverage);
        Assert.Equal("none", evaluation.Rule(RiskRule.LiquidationBuffer).Input("LiquidationPrice"));
    }

    [Fact]
    public void The_global_leverage_cap_lowers_an_agent_set_above_it()
    {
        var inputs = Futures() with { Settings = RiskSettings.Default with { MaxLeverage = 20 } };

        Assert.Equal(5, Evaluate(FuturesLong(), inputs).Passes().Leverage);
    }

    [Fact]
    public void Spot_has_no_liquidation()
    {
        Assert.Equal(RuleOutcome.NotApplicable, Evaluate(SpotBuy(), Spot()).Rule(RiskRule.LiquidationBuffer).Outcome);
    }
}
