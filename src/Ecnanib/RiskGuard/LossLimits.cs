namespace Ecnanib.RiskGuard;

/// <summary>A loss limit of an agent (SRS Appendix B).</summary>
public enum LossLimit
{
    /// <summary>The most that an agent loses in one UTC day, in percent of its equity as the day began.</summary>
    DailyLoss,

    /// <summary>The furthest that the equity of an agent falls from its peak, in percent of the peak.</summary>
    Drawdown,
}

/// <summary>
/// When a loss limit of an agent is reached (FR-RSK-4, FR-RSK-8): the one definition of it, for
/// the rule that rejects a plan which opens a position, and for what closes the position of an
/// agent and halts it. See "The loss limits" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// It is rules only, as everything of the Risk Guard is: it is handed the equity of the agent
/// now, with unrealized profit and loss, and what the loss is counted from, reads nothing, and
/// stores nothing.
/// </para>
/// <para>
/// <b>A loss limit is reached at the limit:</b> a loss of exactly the limit has reached it.
/// Products decide, as they do for every rule: nothing is divided, so no rounding decides.
/// </para>
/// </remarks>
public static class LossLimits
{
    /// <summary>
    /// Whether a loss has reached a limit: the equity is below what the loss is counted from by
    /// the limit, in percent of that, or by more.
    /// </summary>
    /// <param name="equity">The equity of the agent now, with unrealized profit and loss.</param>
    /// <param name="from">What the loss is counted from: the equity as the UTC day began, or the peak equity.</param>
    /// <param name="limitPercent">The limit of the agent, in percent of what the loss is counted from.</param>
    /// <returns>
    /// Whether the limit is reached. It is not where the loss is counted from nothing: an equity
    /// that is not above zero is nothing that a share can be taken of. See <see cref="IsKnown"/>.
    /// </returns>
    /// <exception cref="OverflowException">The numbers are beyond what can be worked out with.</exception>
    public static bool IsReached(decimal equity, decimal from, decimal limitPercent) =>
        IsKnown(from) && (from - equity) * 100m >= limitPercent * from;

    /// <summary>
    /// Whether a loss can be counted from an equity: from one that is above zero. A loss that
    /// is counted from nothing is not known, and neither holds a plan back as a loss that was
    /// reached does, nor halts an agent.
    /// </summary>
    /// <param name="from">What the loss is counted from.</param>
    public static bool IsKnown(decimal from) => from > 0m;

    /// <summary>
    /// How much an agent has lost, in percent of what the loss is counted from: what a check
    /// says that it measured, and what its user is told. An agent that has gained has lost
    /// nothing. Whether a limit is reached is not decided from it: see <see cref="IsReached"/>.
    /// </summary>
    /// <param name="equity">The equity of the agent now, with unrealized profit and loss.</param>
    /// <param name="from">What the loss is counted from, which is above zero.</param>
    /// <exception cref="OverflowException">The numbers are beyond what can be worked out with.</exception>
    public static decimal LossPercent(decimal equity, decimal from) => Math.Max(from - equity, 0m) * 100m / from;

    /// <summary>
    /// The loss limit that an agent has reached, or null when it has reached none. The drawdown
    /// limit is said before the daily loss limit where both are reached: it halts the agent for
    /// longer, until its user resumes it (FR-RSK-8).
    /// </summary>
    /// <param name="equity">The equity of the agent now, with unrealized profit and loss.</param>
    /// <param name="equityAtDayStart">Its equity as the UTC day began.</param>
    /// <param name="peakEquity">The highest that its equity has been.</param>
    /// <param name="dailyLossPercent">Its daily loss limit, in percent of its equity as the day began.</param>
    /// <param name="drawdownPercent">Its drawdown limit, in percent of its peak equity.</param>
    /// <exception cref="OverflowException">The numbers are beyond what can be worked out with.</exception>
    public static LossLimit? Reached(
        decimal equity, decimal equityAtDayStart, decimal peakEquity, decimal dailyLossPercent, decimal drawdownPercent)
    {
        if (IsReached(equity, peakEquity, drawdownPercent))
        {
            return LossLimit.Drawdown;
        }

        return IsReached(equity, equityAtDayStart, dailyLossPercent) ? LossLimit.DailyLoss : null;
    }
}
