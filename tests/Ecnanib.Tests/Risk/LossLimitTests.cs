using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-08: a daily or weekly loss limit, or the consecutive-loss count, stops entries and pauses the agent for
/// its period. The engine reports the breach, and the caller pauses (see AgentLossLimitTests).
/// </summary>
public sealed class LossLimitTests
{
    private static readonly DateTimeOffset Thursday = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMonday = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_daily_loss_of_exactly_the_limit_fails_entries_until_the_next_UTC_day()
    {
        // 3% of 1,000 is 30.
        var evaluation = Evaluate(SpotBuy(), Spot() with { Record = TradingRecord.None with { RealizedToday = -30m } });

        evaluation.FailsOn(RiskRule.DailyLoss, RiskReason.DailyLossLimitReached);
        Assert.Equal(new LossLimitBreach(LossLimitKind.Daily, Thursday), evaluation.Breach);
        Assert.Equal("30", evaluation.Rule(RiskRule.DailyLoss).Input("Limit"));
    }

    [Fact]
    public void A_daily_loss_just_under_the_limit_passes()
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { Record = TradingRecord.None with { RealizedToday = -29.99m } });

        evaluation.Passes();
        Assert.Null(evaluation.Breach);
    }

    [Fact]
    public void A_weekly_loss_of_the_limit_fails_entries_until_next_Monday()
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { Record = TradingRecord.None with { RealizedThisWeek = -60m } });

        evaluation.FailsOn(RiskRule.WeeklyLoss, RiskReason.WeeklyLossLimitReached);
        Assert.Equal(new LossLimitBreach(LossLimitKind.Weekly, NextMonday), evaluation.Breach);
    }

    [Fact]
    public void The_consecutive_loss_count_pauses_until_the_user_resumes()
    {
        var evaluation = Evaluate(SpotBuy(), Spot() with { Record = TradingRecord.None with { ConsecutiveLosses = 3 } });

        evaluation.FailsOn(RiskRule.ConsecutiveLosses, RiskReason.ConsecutiveLossesReached);
        Assert.Equal(new LossLimitBreach(LossLimitKind.ConsecutiveLosses, null), evaluation.Breach);
    }

    [Fact]
    public void A_consecutive_loss_count_of_zero_turns_the_limit_off()
    {
        var inputs = Spot() with
        {
            Settings = RiskSettings.Default with { ConsecutiveLossesBeforePause = 0 },
            Record = TradingRecord.None with { ConsecutiveLosses = 50 },
        };

        Evaluate(SpotBuy(), inputs).Passes();
    }

    [Fact]
    public void The_longest_pause_wins()
    {
        var both = Spot() with { Record = TradingRecord.None with { RealizedToday = -40m, RealizedThisWeek = -60m } };
        var withStreak = Spot() with { Record = TradingRecord.None with { RealizedToday = -40m, RealizedThisWeek = -60m, ConsecutiveLosses = 3 } };

        Assert.Equal(new LossLimitBreach(LossLimitKind.Weekly, NextMonday), Evaluate(SpotBuy(), both).Breach);
        Assert.Equal(new LossLimitBreach(LossLimitKind.ConsecutiveLosses, null), Evaluate(SpotBuy(), withStreak).Breach);
    }

    [Fact]
    public void Exits_and_adjustments_still_pass_when_a_limit_is_reached()
    {
        var position = new OpenPosition(PositionSide.Long, 2m, 105m, Stop: 98m);
        var inputs = Spot() with
        {
            Position = position,
            BaseHeld = 2m,
            Record = new TradingRecord(-100m, -100m, 9, 99, 0),
        };

        Evaluate(new OrderIntent { Action = IntentAction.Sell }, inputs).Passes();
        Evaluate(new OrderIntent { Action = IntentAction.AdjustStop, StopPrice = 99m }, inputs).Passes();
        Assert.Null(Evaluate(new OrderIntent { Action = IntentAction.Sell }, inputs).Breach);
    }

    [Fact]
    public void The_check_at_the_start_of_a_cycle_records_each_limit_and_the_breach()
    {
        var check = Engine().CheckLossLimits(RiskSettings.Default, 1000m, TradingRecord.None with { RealizedToday = -31m }, Now);

        Assert.Equal([RiskRule.Settings, RiskRule.DailyLoss, RiskRule.WeeklyLoss, RiskRule.ConsecutiveLosses], check.Rules.Select(rule => rule.Rule));
        Assert.Equal(RuleOutcome.Fail, check.Rule(RiskRule.DailyLoss).Outcome);
        Assert.Equal(RuleOutcome.Pass, check.Rule(RiskRule.WeeklyLoss).Outcome);
        Assert.Equal(new LossLimitBreach(LossLimitKind.Daily, Thursday), check.Breach);
    }

    [Fact]
    public void The_check_finds_nothing_for_an_agent_without_losses()
    {
        var check = Engine().CheckLossLimits(RiskSettings.Default, 1000m, TradingRecord.None with { RealizedToday = 500m }, Now);

        Assert.Null(check.Breach);
        Assert.All(check.Rules, rule => Assert.Equal(RuleOutcome.Pass, rule.Outcome));
    }

    [Fact]
    public void The_check_evaluates_nothing_with_invalid_settings_or_no_capital()
    {
        var invalid = Engine().CheckLossLimits(RiskSettings.Default with { DailyLossLimitPercent = 0m }, 1000m, TradingRecord.None with { RealizedToday = -500m }, Now);
        var noCapital = Engine().CheckLossLimits(RiskSettings.Default, 0m, TradingRecord.None with { RealizedToday = -500m }, Now);

        Assert.Equal(RiskReason.SettingsOutOfRange, invalid.Rule(RiskRule.Settings).Reason);
        Assert.Equal(RiskReason.NoCapital, noCapital.Rule(RiskRule.Settings).Reason);
        Assert.Null(invalid.Breach);
        Assert.Equal(RuleOutcome.NotEvaluated, invalid.Rule(RiskRule.DailyLoss).Outcome);
    }

    [Theory]
    [InlineData("2026-11-08T23:59:59Z", "2026-11-02T00:00:00Z", "2026-11-09T00:00:00Z", "2026-11-09T00:00:00Z")]
    [InlineData("2026-11-09T00:00:00Z", "2026-11-09T00:00:00Z", "2026-11-10T00:00:00Z", "2026-11-16T00:00:00Z")]
    [InlineData("2026-11-09T01:00:00+03:00", "2026-11-02T00:00:00Z", "2026-11-09T00:00:00Z", "2026-11-09T00:00:00Z")]
    public void Days_and_weeks_are_UTC_and_weeks_start_on_Monday(string now, string weekStart, string nextDay, string nextWeek)
    {
        var time = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DateTimeOffset.Parse(weekStart, System.Globalization.CultureInfo.InvariantCulture), RiskPeriods.WeekStart(time));
        Assert.Equal(DateTimeOffset.Parse(nextDay, System.Globalization.CultureInfo.InvariantCulture), RiskPeriods.NextDay(time));
        Assert.Equal(DateTimeOffset.Parse(nextWeek, System.Globalization.CultureInfo.InvariantCulture), RiskPeriods.NextWeek(time));
        Assert.Equal(TimeSpan.Zero, RiskPeriods.DayStart(time).Offset);
    }

    [Fact]
    public void A_limit_reached_on_Sunday_night_pauses_until_Monday()
    {
        var sunday = new DateTimeOffset(2026, 11, 8, 23, 59, 0, TimeSpan.Zero);

        var evaluation = Evaluate(SpotBuy(), Spot() with { Now = sunday, Record = TradingRecord.None with { RealizedThisWeek = -60m } });

        Assert.Equal(NextMonday, evaluation.Breach?.Until);
    }
}
