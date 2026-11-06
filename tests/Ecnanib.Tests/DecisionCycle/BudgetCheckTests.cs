using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-AGT-02's AI budget against an agent's spend: a limit runs out once the spend is at it, for the rest of its UTC
/// day or month (FR-CYC-07). Dollars and tokens are counted apart, because tokens are only what unpriced models used.
/// </summary>
public sealed class BudgetCheckTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMonth = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_limit_never_runs_out()
    {
        var check = new AiSpend(At, 1_000m, 1_000_000, 10_000m, 10_000_000).Check(AiBudget.None);

        Assert.False(check.RanOut);
        Assert.Null(check.Reached);
        Assert.Null(check.Until);
    }

    [Theory]
    [InlineData(BudgetLimit.DailyUsd)]
    [InlineData(BudgetLimit.MonthlyUsd)]
    [InlineData(BudgetLimit.DailyTokens)]
    [InlineData(BudgetLimit.MonthlyTokens)]
    public void Each_limit_runs_out_at_its_value_until_its_period_ends(BudgetLimit limit)
    {
        var spend = new AiSpend(At, 5m, 5_000, 50m, 50_000);
        var budget = limit switch
        {
            BudgetLimit.DailyUsd => new AiBudget(5m, null, null, null),
            BudgetLimit.MonthlyUsd => new AiBudget(null, 50m, null, null),
            BudgetLimit.DailyTokens => new AiBudget(null, null, 5_000, null),
            _ => new AiBudget(null, null, null, 50_000),
        };

        var check = spend.Check(budget);

        Assert.True(check.RanOut);
        Assert.Equal(limit, check.Reached);
        Assert.Equal(limit is BudgetLimit.DailyUsd or BudgetLimit.DailyTokens ? NextDay : NextMonth, check.Until);
        Assert.Equal((budget, spend), (check.Budget, check.Spend));
    }

    [Fact]
    public void Spend_just_under_every_limit_has_not_run_out()
    {
        var check = new AiSpend(At, 4.99m, 4_999, 49.99m, 49_999).Check(new AiBudget(5m, 50m, 5_000, 50_000));

        Assert.False(check.RanOut);
    }

    [Fact]
    public void When_a_day_and_a_month_have_both_run_out_the_month_does_because_it_lasts_longer()
    {
        var check = new AiSpend(At, 5m, 0, 50m, 0).Check(new AiBudget(5m, 50m, null, null));

        Assert.Equal((BudgetLimit.MonthlyUsd, NextMonth), (check.Reached, check.Until));
    }

    [Fact]
    public void Dollars_and_tokens_are_counted_apart()
    {
        // Lots of dollars on priced models, and few tokens on unpriced ones.
        var check = new AiSpend(At, 100m, 10, 100m, 10).Check(new AiBudget(null, null, 1_000, 1_000));

        Assert.False(check.RanOut);
    }

    [Fact]
    public void December_runs_out_until_January()
    {
        var check = new AiSpend(new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero), 0m, 0, 10m, 0).Check(new AiBudget(null, 10m, null, null));

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), check.Until);
    }

    [Fact]
    public void Periods_are_UTC_whatever_the_offset()
    {
        // 00:30 on the 1st at UTC+2 is still the last day of the month before in UTC.
        var now = new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), BudgetPeriods.MonthStart(now));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), BudgetPeriods.NextMonth(now));
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero), BudgetPeriods.DayStart(now));
    }
}
