using Ecnanib.RiskGuard;

namespace Ecnanib.Tests.RiskGuard;

/// <summary>
/// When an agent's loss for the UTC day reaches the daily loss limit, the system closes its
/// position and halts it until the next UTC day. When its drawdown from peak equity reaches the
/// drawdown limit, the system closes its position and halts it until the user resumes it. Both
/// limits include unrealized profit and loss (FR-RSK-8). This is when a limit is reached, which
/// what halts an agent and the rule that rejects an entry (FR-RSK-4) both ask. See "The loss
/// limits" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// The equity that it is handed is the equity now, with unrealized profit and loss: what says it
/// is shown in <c>AgentResultTests</c>, and what closes a position and halts an agent in
/// <c>LossLimitHaltTests</c>, of <c>tests/Ecnanib.Tests/DecisionEngine</c>.
/// </remarks>
public class LossLimitTests
{
    [Theory]
    [InlineData(940, 1000, 6, true, "a loss of exactly the limit has reached it")]
    [InlineData(939.99, 1000, 6, true, "a loss beyond the limit")]
    [InlineData(940.01, 1000, 6, false, "a loss a little short of the limit")]
    [InlineData(1000, 1000, 6, false, "no loss")]
    [InlineData(1200, 1000, 6, false, "a gain")]
    [InlineData(-50, 1000, 6, true, "an equity that is lost, and more")]
    [InlineData(1500, 2000, 25, true, "a fall of exactly the limit from a peak")]
    [InlineData(1500.01, 2000, 25, false, "a fall a little short of the limit from a peak")]
    public void A_loss_limit_is_reached_at_the_limit(decimal equity, decimal from, decimal limitPercent, bool reached, string what)
    {
        Assert.True(reached == LossLimits.IsReached(equity, from, limitPercent), what);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_loss_that_is_counted_from_nothing_is_not_known_and_reaches_no_limit(decimal from)
    {
        // An equity that is not above zero is nothing that a share can be taken of. The rule
        // that rejects an entry says that it does not know, which opens nothing, and nothing is
        // halted for it.
        Assert.False(LossLimits.IsKnown(from));
        Assert.False(LossLimits.IsReached(equity: -100m, from, limitPercent: 6m));
        Assert.True(LossLimits.IsKnown(0.01m));
    }

    [Fact]
    public void The_limit_that_an_agent_has_reached_is_said_and_the_drawdown_limit_before_the_daily_loss_limit()
    {
        // 6 percent of the 1000 of the day's start, and 25 percent from the peak of 1000.
        Assert.Null(LossLimits.Reached(equity: 940.01m, equityAtDayStart: 1000m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));
        Assert.Equal(
            LossLimit.DailyLoss,
            LossLimits.Reached(equity: 940m, equityAtDayStart: 1000m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));

        // A drawdown on a day with a gain: the peak was 1000, and the day began at 690.
        Assert.Equal(
            LossLimit.Drawdown,
            LossLimits.Reached(equity: 700m, equityAtDayStart: 690m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));

        // Both: the drawdown limit halts the agent for longer, until its user resumes it, so
        // it is the one that is said.
        Assert.Equal(
            LossLimit.Drawdown,
            LossLimits.Reached(equity: 700m, equityAtDayStart: 1000m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));

        // A day's start that is not known reaches no daily loss limit, and says nothing about the drawdown.
        Assert.Null(LossLimits.Reached(equity: 990m, equityAtDayStart: 0m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));
        Assert.Equal(
            LossLimit.Drawdown,
            LossLimits.Reached(equity: 700m, equityAtDayStart: 0m, peakEquity: 1000m, dailyLossPercent: 6m, drawdownPercent: 25m));
    }

    [Fact]
    public void How_much_an_agent_has_lost_is_said_in_percent_of_what_the_loss_is_counted_from()
    {
        Assert.Equal(6m, LossLimits.LossPercent(equity: 940m, from: 1000m));
        Assert.Equal(25m, LossLimits.LossPercent(equity: 1500m, from: 2000m));

        // An agent that has gained has lost nothing.
        Assert.Equal(0m, LossLimits.LossPercent(equity: 1200m, from: 1000m));
    }

    [Fact]
    public void The_rule_that_rejects_an_entry_and_what_halts_an_agent_agree_on_every_equity()
    {
        // One definition: an agent whose limit is reached opens nothing, and an agent that
        // opens something has reached none. The limits of the cases are 6 and 25 percent.
        decimal[] equities = [1200m, 1000m, 940.01m, 940m, 900m, 750.01m, 750m, 700m, 1m];

        foreach (var equity in equities)
        {
            foreach (var peak in new[] { 1000m, 1300m })
            {
                var plan = GuardCases.OpenLong with { Equity = equity, PeakEquity = peak };

                Assert.Equal(
                    LossLimits.IsReached(equity, plan.EquityAtDayStart, plan.Limits.DailyLossPercent),
                    plan.OutcomeOf(GuardRule.DailyLossLimit) == RuleOutcome.Failed);
                Assert.Equal(
                    LossLimits.IsReached(equity, peak, plan.Limits.DrawdownPercent),
                    plan.OutcomeOf(GuardRule.DrawdownLimit) == RuleOutcome.Failed);
            }
        }
    }

    [Fact]
    public void Numbers_that_cannot_be_worked_out_with_are_an_error_for_whoever_asks()
    {
        // The check of a plan answers that the rule does not hold. What halts an agent does
        // not halt it on numbers that are none: it is told so, and says that it does not know.
        Assert.Throws<OverflowException>(() => LossLimits.IsReached(equity: decimal.MinValue, from: decimal.MaxValue, limitPercent: 6m));
    }
}
