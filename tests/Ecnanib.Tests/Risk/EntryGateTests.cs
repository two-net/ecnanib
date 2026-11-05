using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// The FR-RISK-02 settings with no requirement of their own: maximum new entries per day, the cooldown after a
/// loss and the funding filter, plus a symbol that isn't trading and an entry that hasn't filled.
/// </summary>
public sealed class EntryGateTests
{
    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Entries_stop_at_the_daily_maximum(int entriesToday, bool passes)
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { Record = TradingRecord.None with { EntriesToday = entriesToday } });

        if (passes)
        {
            evaluation.Passes();
        }
        else
        {
            evaluation.FailsOn(RiskRule.EntriesPerDay, RiskReason.EntriesPerDayReached);
        }
    }

    [Theory]
    [InlineData(null, 1, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 0, true)]
    public void An_entry_waits_the_cooldown_after_a_loss(int? cyclesSinceLoss, int cooldown, bool passes)
    {
        var inputs = Spot() with
        {
            Settings = RiskSettings.Default with { CooldownCyclesAfterLoss = cooldown },
            Record = TradingRecord.None with { CyclesSinceLastLoss = cyclesSinceLoss },
        };

        var evaluation = Evaluate(SpotBuy(), inputs);

        if (passes)
        {
            evaluation.Passes();
        }
        else
        {
            evaluation.FailsOn(RiskRule.Cooldown, RiskReason.CoolingDown);
        }
    }

    [Fact]
    public void An_entry_waits_while_another_is_unfilled()
    {
        Evaluate(SpotBuy(), Spot() with { HasPendingEntry = true }).FailsOn(RiskRule.PendingEntry, RiskReason.EntryPending);
    }

    [Fact]
    public void An_entry_on_a_symbol_that_is_not_trading_fails()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Trading = false } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.SymbolTrading, RiskReason.SymbolNotTrading);
    }

    [Theory]
    // 0.05% per 8 hours is a rate of 0.0005. Exactly at the threshold passes.
    [InlineData("0.0005", 8, true, true)]
    [InlineData("0.00051", 8, true, false)]
    [InlineData("0.00051", 8, false, true)]
    [InlineData("-0.00051", 8, false, false)]
    [InlineData("-0.00051", 8, true, true)]
    // Every 4 hours, 0.0003 is 0.06% per 8 hours.
    [InlineData("0.0003", 4, true, false)]
    [InlineData("0.00025", 4, true, true)]
    public void Funding_against_the_position_above_the_threshold_skips_the_entry(string rate, int intervalHours, bool isLong, bool passes)
    {
        var inputs = Futures() with { Funding = new FundingInfo(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), intervalHours) };

        var evaluation = Evaluate(isLong ? FuturesLong() : FuturesShort(), inputs);

        if (passes)
        {
            evaluation.Passes();
        }
        else
        {
            evaluation.FailsOn(RiskRule.Funding, RiskReason.FundingAgainstPosition);
        }
    }

    [Fact]
    public void Without_a_funding_rate_the_filter_passes()
    {
        var evaluation = Evaluate(FuturesLong(), Futures() with { Funding = null });

        evaluation.Passes();
        Assert.Equal("none", evaluation.Rule(RiskRule.Funding).Input("FundingRate"));
    }

    [Fact]
    public void Spot_has_no_funding()
    {
        Assert.Equal(RuleOutcome.NotApplicable, Evaluate(SpotBuy(), Spot()).Rule(RiskRule.Funding).Outcome);
    }

    [Fact]
    public void An_entry_without_an_entry_type_fails()
    {
        Evaluate(SpotBuy() with { EntryType = null }, Spot()).FailsOn(RiskRule.Action, RiskReason.EntryTypeMissing);
    }

    [Fact]
    public void An_agent_with_no_capital_or_invalid_settings_enters_nothing()
    {
        var noCapital = Evaluate(SpotBuy(), Spot() with { CapitalAllocation = 0m });
        var invalid = Evaluate(SpotBuy(), Spot() with { Settings = RiskSettings.Default with { MaxNewEntriesPerDay = 0 } });

        noCapital.FailsOn(RiskRule.Settings, RiskReason.NoCapital);
        invalid.FailsOn(RiskRule.Settings, RiskReason.SettingsOutOfRange);
        Assert.All(invalid.Rules.Skip(1), rule => Assert.Equal(RuleOutcome.NotEvaluated, rule.Outcome));
    }
}
