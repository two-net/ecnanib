using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-05's stop side and ATR bounds, the protective stop that's always required (FR-RISK-02), and the
/// minimum reward-to-risk when a target is set.
/// </summary>
public sealed class StopTargetTests
{
    [Fact]
    public void An_entry_without_a_stop_fails()
    {
        Evaluate(SpotBuy() with { StopPrice = null }, Spot()).FailsOn(RiskRule.StopRequired, RiskReason.StopMissing);
    }

    [Fact]
    public void A_stop_on_the_wrong_side_fails_for_a_long_and_a_short()
    {
        Evaluate(SpotBuy() with { StopPrice = 101m, TargetPrice = null }, Spot()).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
        Evaluate(FuturesShort() with { StopPrice = 990m, TargetPrice = null }, Futures()).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
    }

    [Fact]
    public void A_stop_that_rounds_onto_the_entry_fails()
    {
        // With a tick of 1, a long's stop of 99.5 rounds up to 100, the entry.
        var inputs = Spot() with { Filters = Spot().Filters with { Price = new TickRule(1m, 1_000_000m, 1m) } };
        var intent = SpotBuy() with { EntryType = EntryType.Limit, LimitPrice = 100m, StopPrice = 99.5m, TargetPrice = null };

        Evaluate(intent, inputs).FailsOn(RiskRule.StopSide, RiskReason.StopWrongSide);
    }

    [Theory]
    [InlineData("99", null)]
    [InlineData("99.01", RiskReason.StopTooClose)]
    [InlineData("92", null)]
    [InlineData("91.99", RiskReason.StopTooFar)]
    public void A_stop_must_be_within_the_ATR_bounds_inclusive(string stop, RiskReason? reason)
    {
        // ATR 2, so 0.5 × ATR is 1 and 4 × ATR is 8 from the entry of 100.
        var intent = SpotBuy() with { StopPrice = decimal.Parse(stop, System.Globalization.CultureInfo.InvariantCulture), TargetPrice = null };

        var evaluation = Evaluate(intent, Spot());

        if (reason is { } expected)
        {
            evaluation.FailsOn(RiskRule.StopDistance, expected);
        }
        else
        {
            evaluation.Passes();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public void An_entry_without_an_ATR_fails(string? atr)
    {
        var inputs = Spot() with { Atr = atr is null ? null : decimal.Parse(atr, System.Globalization.CultureInfo.InvariantCulture) };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.StopDistance, RiskReason.AtrUnavailable);
    }

    [Fact]
    public void The_ATR_bounds_come_from_the_agent_settings()
    {
        var inputs = Spot() with { Settings = RiskSettings.Default with { MinStopDistanceAtr = 1.5m } };

        // 2 from the entry is 1 ATR, under the agent's minimum of 1.5.
        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.StopDistance, RiskReason.StopTooClose);
    }

    [Theory]
    [InlineData("103", true)]
    [InlineData("102.99", false)]
    public void A_target_must_give_at_least_the_minimum_reward_to_risk(string target, bool passes)
    {
        // A stop 2 below the entry needs a target at least 1.5 × 2 = 3 above it.
        var evaluation = Evaluate(SpotBuy() with { TargetPrice = decimal.Parse(target, System.Globalization.CultureInfo.InvariantCulture) }, Spot());

        if (passes)
        {
            evaluation.Passes();
        }
        else
        {
            evaluation.FailsOn(RiskRule.RewardToRisk, RiskReason.RewardToRiskTooLow);
        }
    }

    [Fact]
    public void The_global_minimum_reward_to_risk_raises_an_agent_set_below_it()
    {
        // The agent's 0.5 is below the cap's 1, so a target 1.5 above with a stop 2 below (0.75) fails.
        var inputs = Spot() with { Settings = RiskSettings.Default with { MinRewardToRisk = 0.5m } };

        var evaluation = Evaluate(SpotBuy() with { TargetPrice = 101.5m }, inputs);

        evaluation.FailsOn(RiskRule.RewardToRisk, RiskReason.RewardToRiskTooLow);
        Assert.Equal("1", evaluation.Rule(RiskRule.RewardToRisk).Input("MinRewardToRisk"));
    }

    [Fact]
    public void A_target_on_the_wrong_side_fails()
    {
        Evaluate(SpotBuy() with { TargetPrice = 99m }, Spot()).FailsOn(RiskRule.TargetSide, RiskReason.TargetWrongSide);
        Evaluate(FuturesShort() with { TargetPrice = 1001m }, Futures()).FailsOn(RiskRule.TargetSide, RiskReason.TargetWrongSide);
    }

    [Fact]
    public void Without_a_target_reward_to_risk_does_not_apply()
    {
        var evaluation = Evaluate(SpotBuy() with { TargetPrice = null }, Spot());

        Assert.Null(evaluation.Passes().TargetPrice);
        Assert.Null(evaluation.Order!.RewardToRisk);
        Assert.Equal(RuleOutcome.NotApplicable, evaluation.Rule(RiskRule.RewardToRisk).Outcome);
        Assert.Equal(RuleOutcome.NotApplicable, evaluation.Rule(RiskRule.TargetSide).Outcome);
    }

    [Fact]
    public void A_stop_failure_leaves_the_rules_that_need_it_not_evaluated()
    {
        var evaluation = Evaluate(SpotBuy() with { StopPrice = 101m }, Spot());

        Assert.Equal(RuleOutcome.NotEvaluated, evaluation.Rule(RiskRule.StopDistance).Outcome);
        Assert.Equal(RuleOutcome.NotEvaluated, evaluation.Rule(RiskRule.RewardToRisk).Outcome);
        Assert.Equal(RuleOutcome.NotEvaluated, evaluation.Rule(RiskRule.Size).Outcome);
        Assert.Equal(RuleOutcome.NotEvaluated, evaluation.Rule(RiskRule.Quantity).Outcome);
        Assert.Equal(RuleOutcome.NotEvaluated, evaluation.Rule(RiskRule.Notional).Outcome);
    }
}
