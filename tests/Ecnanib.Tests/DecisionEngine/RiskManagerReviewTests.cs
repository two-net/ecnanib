using System.Reflection;
using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The Risk Manager may approve a plan, reduce its risk fraction, or veto it. It cannot increase
/// risk, change direction, or widen the stop-loss, and it cannot block a plan that closes a
/// position or tightens protection (FR-TEAM-7). See "The review" in docs/team.md.
/// </summary>
public class RiskManagerReviewTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    private static readonly string[] Opening = ["open_long", "open_short", "reverse"];

    private IRoleResults Results => application.Services.GetRequiredService<IRoleResults>();

    [Fact]
    public void The_Risk_Manager_reviews_a_plan_that_opens_a_position_and_no_other()
    {
        Assert.All(Opening, action => Assert.True(PlanReview.Reviews(PlanOf(TeamAnswers.Plan(action)))));

        // A hold changes nothing. A close and an adjustment of the protection reduce risk, and
        // are not held up by a review.
        Assert.All(
            new[] { TeamAnswers.Hold, TeamAnswers.Close, TeamAnswers.Adjust },
            answered => Assert.False(PlanReview.Reviews(PlanOf(answered))));
    }

    [Fact]
    public void An_approved_plan_goes_on_as_it_was_planned()
    {
        Assert.All(Opening, action =>
        {
            var plan = PlanOf(TeamAnswers.Plan(action));
            var decision = PlanReview.Of(plan, ReviewOf(TeamAnswers.Review("approve"), plan));

            Assert.Equal(CycleOutcome.Approved, decision.Outcome);
            Assert.Same(plan, decision.GoesOn);
        });
    }

    [Fact]
    public void A_reduced_plan_goes_on_with_a_lower_risk_fraction_and_with_nothing_else_of_it_changed()
    {
        Assert.All(Opening, action =>
        {
            var plan = PlanOf(TeamAnswers.Plan(action, "limit", entryPrice: 61250m, riskFraction: 0.012m));
            var decision = PlanReview.Of(plan, ReviewOf(TeamAnswers.Review("reduce", 0.004m), plan));

            Assert.Equal(CycleOutcome.Reduced, decision.Outcome);
            Assert.Equal(0.004m, decision.GoesOn!.RiskFraction);

            // The direction, the entry, the stop-loss, and the take-profit are those of the plan.
            Assert.Equal(plan.ButForItsRisk(), decision.GoesOn.ButForItsRisk());
            Assert.Equal(0.012m, plan.RiskFraction);
        });
    }

    [Fact]
    public void A_vetoed_plan_does_not_go_on()
    {
        Assert.All(Opening, action =>
        {
            var plan = PlanOf(TeamAnswers.Plan(action));

            Assert.Equal(
                new TeamDecision(CycleOutcome.Vetoed, GoesOn: null),
                PlanReview.Of(plan, ReviewOf(TeamAnswers.Review("veto"), plan)));
        });
    }

    [Fact]
    public void A_plan_that_opens_a_position_does_not_go_on_without_a_review()
    {
        // The Risk Manager gave no valid review, also when it was called once more: the cycle
        // ends with no decision and no new entry (FR-TEAM-9).
        Assert.All(Opening, action =>
            Assert.Equal(new TeamDecision(CycleOutcome.NoDecision, GoesOn: null), PlanReview.Of(PlanOf(TeamAnswers.Plan(action)), review: null)));
    }

    [Fact]
    public void A_plan_that_closes_a_position_or_adjusts_protection_goes_on_whatever_is_made_of_it()
    {
        var opening = PlanOf(TeamAnswers.Plan());

        Assert.All(new[] { TeamAnswers.Close, TeamAnswers.Adjust }, answered =>
        {
            var plan = PlanOf(answered);

            // It is not reviewed. And a veto, were one handed over, blocks nothing of it.
            RiskReview?[] reviews = [null, ReviewOf(TeamAnswers.Review("veto"), plan), ReviewOf(TeamAnswers.Review("approve"), plan), ReviewOf(TeamAnswers.Review("reduce", 0.001m), opening)];

            Assert.All(reviews, review =>
            {
                var decision = PlanReview.Of(plan, review);

                Assert.Equal(CycleOutcome.Unreviewed, decision.Outcome);
                Assert.Same(plan, decision.GoesOn);
            });
        });

        // A hold has nothing that goes on, and nothing that a review could change.
        var hold = PlanOf(TeamAnswers.Hold);

        Assert.All(
            new RiskReview?[] { null, ReviewOf(TeamAnswers.Review("veto"), hold), ReviewOf(TeamAnswers.Review("approve"), hold) },
            review => Assert.Equal(new TeamDecision(CycleOutcome.Hold, GoesOn: null), PlanReview.Of(hold, review)));
    }

    [Theory]

    // As much as was planned, and more.
    [InlineData(0.01, RejectionRule.OutOfRange)]
    [InlineData(0.0101, RejectionRule.OutOfRange)]
    [InlineData(0.015, RejectionRule.OutOfRange)]
    [InlineData(1.0, RejectionRule.OutOfRange)]

    // Nothing, and less.
    [InlineData(0.0, RejectionRule.OutOfRange)]
    [InlineData(-0.005, RejectionRule.OutOfRange)]

    // No fraction at all.
    [InlineData(null, RejectionRule.Required)]
    public void A_review_cannot_increase_the_risk_of_a_plan(double? reduced, RejectionRule rule)
    {
        // The plan risks 1 percent of the equity.
        var plan = PlanOf(TeamAnswers.Plan(riskFraction: 0.01m));
        var review = Results.ReadReview(TeamAnswers.Review("reduce", (decimal?)reduced), plan);

        Assert.False(review.IsValid);
        Assert.Equal(new RoleRejection(rule, "riskFraction"), review.Rejection);
    }

    [Fact]
    public void A_fraction_is_named_only_to_reduce_a_plan_that_risks_something()
    {
        var plan = PlanOf(TeamAnswers.Plan(riskFraction: 0.01m));

        // An approval and a veto reduce nothing, and name no fraction.
        Assert.Equal(
            new RoleRejection(RejectionRule.NotExpected, "riskFraction"),
            Results.ReadReview(TeamAnswers.Review("approve", 0.015m), plan).Rejection);
        Assert.Equal(
            new RoleRejection(RejectionRule.NotExpected, "riskFraction"),
            Results.ReadReview(TeamAnswers.Review("veto", 0.005m), plan).Rejection);

        // A plan that risks nothing has nothing to reduce.
        Assert.Equal(
            new RoleRejection(RejectionRule.NotExpected, "verdict"),
            Results.ReadReview(TeamAnswers.Review("reduce", 0.005m), PlanOf(TeamAnswers.Close)).Rejection);

        // Just below what was planned is a reduction.
        Assert.True(Results.ReadReview(TeamAnswers.Review("reduce", 0.0099m), plan).IsValid);
    }

    [Fact]
    public void A_review_that_was_read_for_another_plan_raises_no_risk_either()
    {
        // A review that reduces a bold plan to 1.2 percent is no review of a plan that risks 1 percent.
        var bold = PlanOf(TeamAnswers.Plan(riskFraction: 0.015m));
        var careful = PlanOf(TeamAnswers.Plan(riskFraction: 0.01m));
        var review = ReviewOf(TeamAnswers.Review("reduce", 0.012m), bold);

        Assert.Throws<ArgumentException>(() => PlanReview.Of(careful, review));
    }

    [Theory]
    [InlineData("\"action\":\"open_short\"")]
    [InlineData("\"direction\":\"short\"")]
    [InlineData("\"stopLoss\":50000")]
    [InlineData("\"takeProfit\":70000")]
    [InlineData("\"entryType\":\"market\"")]
    [InlineData("\"entryPrice\":60000")]
    [InlineData("\"quantity\":3")]
    [InlineData("\"leverage\":10")]
    public void A_review_that_names_a_direction_an_entry_or_a_stop_loss_is_rejected(string field)
    {
        var plan = PlanOf(TeamAnswers.Plan());
        var review = Results.ReadReview($"{TeamAnswers.Review()[..^1]},{field}}}", plan);

        Assert.False(review.IsValid);
        Assert.Equal(new RoleRejection(RejectionRule.UnknownField, "answer"), review.Rejection);
    }

    [Fact]
    public void A_review_is_a_verdict_a_lower_risk_fraction_and_reasons_and_nothing_else()
    {
        // What a review cannot say, it cannot change: it has no direction, no entry, and no
        // stop-loss. Pinned, so that a field that is added shows as what it is.
        Assert.Equal(
            ["Rationale", "RiskFraction", "Verdict"],
            typeof(RiskReview).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Where(name => name != "EqualityContract")
                .Order(StringComparer.Ordinal));
        Assert.Equal(["Approve", "Reduce", "Veto"], Enum.GetNames<ReviewVerdict>());

        // And whatever the verdict, what goes on of a plan is that plan, with no more risk.
        Assert.All(Opening, action =>
        {
            var plan = PlanOf(TeamAnswers.Plan(action, riskFraction: 0.0125m));
            RiskReview?[] reviews =
            [
                null,
                ReviewOf(TeamAnswers.Review("approve"), plan),
                ReviewOf(TeamAnswers.Review("reduce", 0.0001m), plan),
                ReviewOf(TeamAnswers.Review("veto"), plan),
            ];

            Assert.All(reviews.Select(review => PlanReview.Of(plan, review).GoesOn).OfType<TradePlan>(), goesOn =>
            {
                Assert.Equal(plan.ButForItsRisk(), goesOn.ButForItsRisk());
                Assert.True(goesOn.RiskFraction <= plan.RiskFraction);
            });
        });
    }

    private TradePlan PlanOf(string answered)
    {
        var plan = Results.ReadPlan(answered);

        Assert.True(plan.IsValid, $"The plan of the test was rejected: {plan.Rejection}.");

        return plan.Value!;
    }

    private RiskReview ReviewOf(string answered, TradePlan plan)
    {
        var review = Results.ReadReview(answered, plan);

        Assert.True(review.IsValid, $"The review of the test was rejected: {review.Rejection}.");

        return review.Value!;
    }
}
