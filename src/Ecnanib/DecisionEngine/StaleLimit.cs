namespace Ecnanib.DecisionEngine;

/// <summary>
/// What the stale limit does to a decision (FR-CYC-9): a decision that completes later than the
/// stale limit of its timeframe cannot open a position. It can still close a position or tighten
/// protection. See "The stale limit" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The limit counts from the close of the candle</b> that the cycle is for, and is that of the
/// agent's timeframe in the configuration file: <c>Trading:StaleSeconds</c> (SRS Appendix B).
/// </para>
/// <para>
/// <b>Only a plan that opens a position is held to it.</b> A plan that holds, closes a position,
/// or adjusts protection goes on as it would in time: an adjustment moves the stop-loss only in
/// the direction that reduces risk (FR-CYC-6), so nothing that is late adds risk.
/// </para>
/// <para>
/// <b>Of a reverse, the close goes on.</b> A reverse closes the open position, and then opens one
/// on the opposite side. The second half opens a position, and is dropped. The first reduces
/// risk, and goes on as a plan to close.
/// </para>
/// </remarks>
public static class StaleLimit
{
    /// <summary>
    /// Whether a decision is stale: it completed later than the limit after the close. One that
    /// completed at the limit itself is in time.
    /// </summary>
    /// <param name="closedAt">The close of the candle that the cycle is for.</param>
    /// <param name="completedAt">When the decision completed.</param>
    /// <param name="limit">The stale limit of the agent's timeframe.</param>
    public static bool IsStale(DateTimeOffset closedAt, DateTimeOffset completedAt, TimeSpan limit) =>
        completedAt - closedAt > limit;

    /// <summary>
    /// What becomes of a plan that opens a position when its decision is stale: no position is
    /// opened. The close of a reverse goes on, and of a plan that opens long or short nothing does.
    /// </summary>
    /// <param name="plan">The plan of the Strategist, as it would go on in time.</param>
    /// <exception cref="ArgumentException">The plan opens no position, so the stale limit does nothing to it.</exception>
    public static TeamDecision Of(TradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.OpensPosition)
        {
            throw new ArgumentException(
                "Only a plan that opens a position is held to the stale limit. A plan that holds, closes a position, " +
                "or adjusts protection goes on as it is (FR-CYC-9).",
                nameof(plan));
        }

        return new TeamDecision(CycleOutcome.Stale, plan.Action == PlanAction.Reverse ? plan.ClosingOnly() : null);
    }
}
