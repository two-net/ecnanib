using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// FR-RISK-08's limits, which an entry's evaluation and the check at the start of each cycle share. A limit is
/// reached when the loss is at least the limit, so a loss of exactly 3% of capital pauses the agent.
/// </summary>
internal static class LossLimits
{
    public static LossLimitCheck Check(RiskCaps caps, RiskSettings settings, decimal capital, TradingRecord record, DateTimeOffset now)
    {
        var trace = new Trace();
        trace.Add(Rules.Settings(caps, settings, capital));

        if (trace.Failed)
        {
            trace.Add(Trace.NotEvaluated(RiskRule.DailyLoss));
            trace.Add(Trace.NotEvaluated(RiskRule.WeeklyLoss));
            trace.Add(Trace.NotEvaluated(RiskRule.ConsecutiveLosses));

            return new LossLimitCheck(trace.Results, null);
        }

        var (daily, dailyBreach) = Daily(settings, capital, record, now);
        var (weekly, weeklyBreach) = Weekly(settings, capital, record, now);
        var (consecutive, consecutiveBreach) = Consecutive(settings, record);

        trace.Add(daily);
        trace.Add(weekly);
        trace.Add(consecutive);

        return new LossLimitCheck(trace.Results, MostRestrictive(dailyBreach, weeklyBreach, consecutiveBreach));
    }

    public static (RuleResult Result, LossLimitBreach? Breach) Daily(RiskSettings settings, decimal capital, TradingRecord record, DateTimeOffset now) =>
        Limit(RiskRule.DailyLoss, LossLimitKind.Daily, record.RealizedToday, settings.DailyLossLimitPercent, capital, RiskPeriods.NextDay(now));

    public static (RuleResult Result, LossLimitBreach? Breach) Weekly(RiskSettings settings, decimal capital, TradingRecord record, DateTimeOffset now) =>
        Limit(RiskRule.WeeklyLoss, LossLimitKind.Weekly, record.RealizedThisWeek, settings.WeeklyLossLimitPercent, capital, RiskPeriods.NextWeek(now));

    public static (RuleResult Result, LossLimitBreach? Breach) Consecutive(RiskSettings settings, TradingRecord record)
    {
        var limit = settings.ConsecutiveLossesBeforePause;
        var inputs = new Inputs()
            .Add("ConsecutiveLosses", record.ConsecutiveLosses)
            .Add("ConsecutiveLossesBeforePause", limit);

        if (record.ConsecutiveLosses < 0)
        {
            return (Trace.Fail(RiskRule.ConsecutiveLosses, RiskReason.InputInvalid, inputs), null);
        }

        // 0 turns the limit off.
        if (limit == 0 || record.ConsecutiveLosses < limit)
        {
            return (Trace.Pass(RiskRule.ConsecutiveLosses, inputs), null);
        }

        // It has no period: the agent stays paused until its user resumes it.
        inputs.Add("PausedUntil", (DateTimeOffset?)null);

        return (Trace.Fail(RiskRule.ConsecutiveLosses, RiskReason.ConsecutiveLossesReached, inputs), new LossLimitBreach(LossLimitKind.ConsecutiveLosses, null));
    }

    /// <summary>The one that pauses for longest. A pause that waits for the user is longer than any date.</summary>
    public static LossLimitBreach? MostRestrictive(params LossLimitBreach?[] breaches)
    {
        LossLimitBreach? longest = null;

        foreach (var breach in breaches)
        {
            if (breach is null)
            {
                continue;
            }

            if (longest is null
                || (longest.Until is not null && (breach.Until is null || breach.Until > longest.Until)))
            {
                longest = breach;
            }
        }

        return longest;
    }

    private static (RuleResult Result, LossLimitBreach? Breach) Limit(
        RiskRule rule,
        LossLimitKind kind,
        decimal realized,
        decimal percent,
        decimal capital,
        DateTimeOffset until)
    {
        var inputs = new Inputs()
            .Add("Realized", realized)
            .Add("CapitalAllocation", capital)
            .Add("LimitPercent", percent)
            .Add("Limit", capital * percent / 100m);

        // Compared without dividing, so no rounding can move the boundary.
        if (-realized * 100m < capital * percent)
        {
            return (Trace.Pass(rule, inputs), null);
        }

        inputs.Add("PausedUntil", until);
        var reason = kind == LossLimitKind.Daily ? RiskReason.DailyLossLimitReached : RiskReason.WeeklyLossLimitReached;

        return (Trace.Fail(rule, reason, inputs), new LossLimitBreach(kind, until));
    }
}
