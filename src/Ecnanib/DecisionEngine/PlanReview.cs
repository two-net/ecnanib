namespace Ecnanib.DecisionEngine;

/// <summary>
/// How a cycle ended: what the team of the agent decided, or why it decided nothing. A member is
/// stored by its name, so renaming one changes stored data and needs a migration. See
/// "The outcome of a cycle" in docs/team.md.
/// </summary>
public enum CycleOutcome
{
    /// <summary>The plan is to hold: nothing goes on.</summary>
    Hold,

    /// <summary>
    /// The plan closes a position or adjusts protection, and goes on as planned. The Risk Manager
    /// is not asked about such a plan, so it cannot block one (FR-TEAM-7).
    /// </summary>
    Unreviewed,

    /// <summary>The plan opens a position, the Risk Manager approved it, and it goes on as planned.</summary>
    Approved,

    /// <summary>The plan opens a position, the Risk Manager reduced its risk fraction, and it goes on with less risk.</summary>
    Reduced,

    /// <summary>The plan opens a position, and the Risk Manager vetoed it: nothing goes on.</summary>
    Vetoed,

    /// <summary>
    /// A role other than the Sentiment Analyst gave no valid result, also when it was called once
    /// more: the cycle ended with no decision and no new entry (FR-TEAM-9).
    /// </summary>
    NoDecision,

    /// <summary>An error ended the cycle, with no decision.</summary>
    Failed,

    /// <summary>The cycle was stopped: with the application, or because the next close was near.</summary>
    Stopped,
}

/// <summary>What the team of an agent decided in a cycle.</summary>
/// <param name="Outcome">What became of the plan.</param>
/// <param name="GoesOn">
/// The plan that goes on to the Risk Guard, which checks every plan before any order (FR-RSK-3),
/// or null when nothing goes on. It is the plan of the Strategist, with a lower risk fraction
/// when the Risk Manager reduced it, and with nothing else of it changed.
/// </param>
public sealed record TeamDecision(CycleOutcome Outcome, TradePlan? GoesOn);

/// <summary>
/// What the review of the Risk Manager does to a plan (FR-TEAM-7): the Risk Manager may approve a
/// plan, reduce its risk fraction, or veto it. It cannot increase risk, change direction, or
/// widen the stop-loss, and it cannot block a plan that closes a position or tightens protection.
/// See "The review" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reviews a plan that opens a position, and no other.</b> A plan that holds, closes a
/// position, or adjusts protection is not handed to the Risk Manager at all, so nothing that its
/// model answers, and no call of it that fails, stands in the way of reducing risk.
/// </para>
/// <para>
/// <b>What goes on is the plan of the Strategist.</b> A review has a verdict and a lower risk
/// fraction, and nothing else, so the direction, the entry, the stop-loss, and the take-profit of
/// what goes on are those that were planned.
/// </para>
/// </remarks>
public static class PlanReview
{
    /// <summary>Whether a plan is one that the Risk Manager reviews: a plan that opens a position.</summary>
    public static bool Reviews(TradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.OpensPosition;
    }

    /// <summary>What the team decided: the plan, and what the Risk Manager made of it.</summary>
    /// <param name="plan">The plan of the Strategist.</param>
    /// <param name="review">
    /// The review of the Risk Manager, or null when there is none: the plan is not one that it
    /// reviews, or it gave no valid review.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The review reduces the plan to a risk fraction that is not below the one that was planned.
    /// A review that was validated against the plan never does.
    /// </exception>
    public static TeamDecision Of(TradePlan plan, RiskReview? review)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Action == PlanAction.Hold)
        {
            return new TeamDecision(CycleOutcome.Hold, GoesOn: null);
        }

        if (!plan.OpensPosition)
        {
            // It closes a position or adjusts protection: whatever was made of it, it goes on.
            return new TeamDecision(CycleOutcome.Unreviewed, plan);
        }

        switch (review?.Verdict)
        {
            case null:
                // No new entry without a review (FR-TEAM-9).
                return new TeamDecision(CycleOutcome.NoDecision, GoesOn: null);

            case ReviewVerdict.Approve:
                return new TeamDecision(CycleOutcome.Approved, plan);

            case ReviewVerdict.Veto:
                return new TeamDecision(CycleOutcome.Vetoed, GoesOn: null);

            case ReviewVerdict.Reduce:
                if (review.RiskFraction is not { } reduced || plan.RiskFraction is not { } planned || reduced <= 0m || reduced >= planned)
                {
                    throw new ArgumentException(
                        "A review reduces the risk fraction of a plan to less than it is, and never raises it (FR-TEAM-7).",
                        nameof(review));
                }

                return new TeamDecision(CycleOutcome.Reduced, plan.WithRisk(reduced));

            default:
                throw new ArgumentOutOfRangeException(nameof(review), review.Verdict, "No such verdict.");
        }
    }
}
