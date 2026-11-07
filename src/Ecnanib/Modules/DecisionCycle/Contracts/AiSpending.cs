using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// What a user's agents have spent on AI this UTC day and month, which their budgets limit (FR-AGT-02, FR-CYC-07).
/// Every call takes the user, so another user's agent has spent nothing (NFR-SEC-04). See Budgets in
/// docs/architecture.md.
/// </summary>
public interface IAiSpending
{
    /// <summary>The spend of each of the user's agents that has spent anything this month.</summary>
    Task<IReadOnlyDictionary<Guid, AiSpend>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The agent's spend, which is nothing for an agent that has none, or isn't the user's.</summary>
    Task<AiSpend> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>What each of the user's agents that has spent anything has spent on AI since it was created (FR-AGT-11).</summary>
    Task<IReadOnlyDictionary<Guid, AiCostToDate>> ListToDateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>What the agent has spent on AI since it was created, which is nothing for one that isn't the user's.</summary>
    Task<AiCostToDate> FindToDateAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>
/// An agent's AI cost to date (FR-AGT-11): every billed call of every cycle it has kept, as <see cref="AiSpend"/> counts
/// them. It's real money in either environment, so the total counts both, and the split says how much each took.
/// </summary>
/// <param name="Usd">The estimated cost of every call to a model with a price, in <see cref="AiBudget.Currency"/>.</param>
/// <param name="UnpricedTokens">Input and output tokens of calls to a model without a price, which have no cost to show.</param>
/// <param name="DemoUsd">The part of <paramref name="Usd"/> spent while the agent was in Demo.</param>
/// <param name="LiveUsd">And in Live.</param>
public sealed record AiCostToDate(decimal Usd, long UnpricedTokens, decimal DemoUsd, decimal LiveUsd)
{
    public static AiCostToDate None { get; } = new(0m, 0, 0m, 0m);
}

/// <summary>
/// An agent's AI spend in the UTC day and month that <paramref name="At"/> falls in. Every call that was billed counts,
/// including a failed one or a retry, in the period its cycle started in. A call to a model with a price counts
/// its estimated cost. A call to a model without one counts its input and output tokens, which token caps limit.
/// </summary>
/// <param name="At">When it was summed, which says which day and month it's for.</param>
/// <param name="DayUsd">In <see cref="AiBudget.Currency"/>.</param>
/// <param name="DayTokens">Only calls to a model with no price.</param>
/// <param name="MonthUsd">In <see cref="AiBudget.Currency"/>.</param>
/// <param name="MonthTokens">Only calls to a model with no price.</param>
public sealed record AiSpend(DateTimeOffset At, decimal DayUsd, long DayTokens, decimal MonthUsd, long MonthTokens)
{
    public static AiSpend None(DateTimeOffset at) => new(at, 0m, 0, 0m, 0);

    /// <summary>
    /// Whether <paramref name="budget"/> has run out: a limit is reached once the spend is at it. When several are,
    /// it's a monthly one, because that lasts longer.
    /// </summary>
    public BudgetCheck Check(AiBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        BudgetLimit? reached =
            Reaches(MonthUsd, budget.MonthlyUsd) ? BudgetLimit.MonthlyUsd
            : Reaches(MonthTokens, budget.MonthlyTokens) ? BudgetLimit.MonthlyTokens
            : Reaches(DayUsd, budget.DailyUsd) ? BudgetLimit.DailyUsd
            : Reaches(DayTokens, budget.DailyTokens) ? BudgetLimit.DailyTokens
            : null;

        DateTimeOffset? until = reached switch
        {
            BudgetLimit.MonthlyUsd or BudgetLimit.MonthlyTokens => BudgetPeriods.NextMonth(At),
            BudgetLimit.DailyUsd or BudgetLimit.DailyTokens => BudgetPeriods.NextDay(At),
            _ => null,
        };

        return new BudgetCheck(budget, this, reached, until);
    }

    private static bool Reaches(decimal spent, decimal? limit) => limit is { } most && spent >= most;

    private static bool Reaches(long spent, long? limit) => limit is { } most && spent >= most;
}

/// <summary>An agent's budget against its spend at the start of a cycle, as the trace keeps it.</summary>
/// <param name="Reached">The limit that ran out, or null when none has.</param>
/// <param name="Until">
/// When protective-only mode ends, unless the limit is raised first: the end of the reached limit's day or month.
/// </param>
public sealed record BudgetCheck(AiBudget Budget, AiSpend Spend, BudgetLimit? Reached, DateTimeOffset? Until)
{
    /// <summary>The agent asks no AI and makes no new entry (FR-CYC-07).</summary>
    public bool RanOut => Reached is not null;
}

/// <summary>One of <see cref="AiBudget"/>'s limits. The trace stores the name, so a value is never renamed.</summary>
public enum BudgetLimit
{
    DailyUsd,
    MonthlyUsd,
    DailyTokens,
    MonthlyTokens,
}

/// <summary>The UTC day and month that budgets count over (FR-AGT-02). The day is the loss limits' day.</summary>
public static class BudgetPeriods
{
    public static DateTimeOffset DayStart(DateTimeOffset now) => RiskPeriods.DayStart(now);

    public static DateTimeOffset NextDay(DateTimeOffset now) => RiskPeriods.NextDay(now);

    /// <summary>00:00 UTC on the first of the month <paramref name="now"/> falls in.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>00:00 UTC on the first of the next month.</summary>
    public static DateTimeOffset NextMonth(DateTimeOffset now) => MonthStart(now).AddMonths(1);
}
