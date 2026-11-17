using System.Reflection;
using Ecnanib.AiProviderGateway;
using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Architecture;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Models cannot place orders or call internal functions. The only path from a model's output to
/// an order is schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6).
/// These hold the first step of that path to the code: what a model is called with, what is
/// made of what it answers, and who is handed the key that it is called with (FR-KEY-9). See
/// "What a model cannot do" in docs/team.md.
/// </summary>
public class TeamRulesTests
{
    /// <summary>What validates the answer of a role, and so the one type that makes a result.</summary>
    private const string Validation = "Ecnanib.DecisionEngine.RoleResults";

    /// <summary>The types of the application that run the team of an agent, and read what its models answer.</summary>
    private static readonly string[] RunsTheTeam =
    [
        "Ecnanib.DecisionEngine.TeamCycle",
        "Ecnanib.DecisionEngine.RoleRuns",
        "Ecnanib.DecisionEngine.RoleResults",
        "Ecnanib.DecisionEngine.SchemaCheck",
        "Ecnanib.DecisionEngine.PlanReview",
        "Ecnanib.DecisionEngine.StaleLimit",
    ];

    private static readonly Assembly Application = typeof(Program).Assembly;

    [Fact]
    public void A_model_is_called_with_a_prompt_and_a_schema_and_with_nothing_that_it_could_call()
    {
        // What a call is made with has no place for a function, a tool, or an order: a model,
        // a prompt, the schema of the result, and whether the model may search live, which is
        // the provider's own search and reads only (FR-CYC-3). Pinned, so that a place that is
        // added shows in a review as what it is.
        Assert.Equal(
            ["LiveSearch", "Model", "Prompt", "ResultSchema"],
            typeof(AiCallRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [typeof(bool), typeof(string), typeof(string), typeof(string)],
            typeof(AiCallRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => property.PropertyType));

        // And what a call gives back is text, counts, and a time: nothing that is carried out.
        Assert.Equal(
            ["Latency", "LiveSearches", "Outcome", "ResultJson", "Usage"],
            typeof(AiCallResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_what_validates_an_answer_makes_a_result()
    {
        // A report, a plan, and a review are made from an answer that was validated, and from
        // nothing else: so whatever holds one holds an answer that passed its schema and its
        // ranges, and no plan reaches the Risk Guard that did not (NFR-SEC-6, NFR-SEC-7).
        Type[] results = [typeof(MarketReport), typeof(KeyLevel), typeof(SentimentRead), typeof(TradePlan), typeof(RiskReview)];

        Assert.All(results, result =>
        {
            Assert.Empty(result.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            var makers = DependencyChecker.FindCallersOf(Application, $"{result.FullName}::.ctor");

            // The rule finds what validates an answer, so it would find another that makes a result.
            Assert.Contains(makers, maker => maker.StartsWith(Validation, StringComparison.Ordinal));

            // A result copies itself: a plan with a lower risk fraction is the same plan.
            var others = makers
                .Where(maker => !maker.StartsWith(Validation, StringComparison.Ordinal) && maker != result.FullName)
                .ToList();

            Assert.True(
                others.Count == 0,
                $"These make a {result.Name}, and are not what validates the answer of a role: {string.Join(", ", others)}. " +
                "A result is made by IRoleResults alone, from an answer that is inside its schema and its ranges " +
                "(FR-TEAM-6, NFR-SEC-7). See docs/team.md.");
        });

        // What says whether an answer is valid is made there too.
        Assert.Empty(typeof(RoleResult<TradePlan>).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void What_runs_a_team_uses_nothing_of_the_exchange()
    {
        // The path from what a model answered to an order is schema validation, then the Risk
        // Guard, then the Exchange Gateway (NFR-SEC-6). What runs the team is the first of the
        // three, and ends with a plan: it places nothing, and reads nothing of the exchange.
        // The data package, which reads the market, is built by another type, and is handed over.
        Assert.All(RunsTheTeam, type =>
        {
            var uses = DependencyChecker.FindDependenciesOf(Application, type);

            // The rule finds what a type uses, so it would find the gateway.
            Assert.Contains(uses, used => used.StartsWith("Ecnanib.DecisionEngine.", StringComparison.Ordinal));

            var ofTheExchange = uses.Where(used => used.StartsWith("Ecnanib.ExchangeGateway.", StringComparison.Ordinal)).ToList();

            Assert.True(
                ofTheExchange.Count == 0,
                $"{type} uses {string.Join(", ", ofTheExchange)}. What runs the team of an agent ends with a plan, and uses " +
                "nothing of the exchange: a plan reaches an order only through the Risk Guard (NFR-SEC-6, FR-RSK-3). " +
                "See docs/team.md.");
        });
    }

    [Fact]
    public void A_plan_and_a_review_have_no_place_for_what_the_system_decides()
    {
        // Models do not choose quantity or leverage (FR-RSK-5): a plan names an action, an
        // entry, a stop-loss, a take-profit, and a risk fraction. Pinned, as the review is.
        Assert.Equal(
            ["Action", "EntryPrice", "EntryType", "OpensPosition", "Rationale", "RiskFraction", "StopLoss", "TakeProfit"],
            typeof(TradePlan).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["OpenLong", "OpenShort", "Hold", "Close", "Reverse", "AdjustProtection"],
            Enum.GetNames<PlanAction>());
    }

    [Fact]
    public void Only_the_Decision_Engine_is_handed_a_stored_key()
    {
        // A stored key is handed to the call of an agent that needs it, and to nothing else
        // (FR-KEY-9): the Decision Engine calls the models, with prompts that it composed.
        var asking = DependencyChecker.FindCallersOf(Application, "Ecnanib.Credentials.IKeyHandOver::AiKeyAsync");

        // The rule finds what runs a role, so it would find another that asks for a key.
        Assert.Equal(["Ecnanib.DecisionEngine.RoleRuns"], asking.Select(caller => caller.Split('+')[0]).Distinct());

        // And what is handed over is read there alone.
        var reading = DependencyChecker.FindCallersOf(Application, "Ecnanib.Credentials.HandedKey::get_Key")
            .Select(caller => caller.Split('+')[0])
            .Distinct()
            .ToList();

        Assert.Equal(["Ecnanib.DecisionEngine.RoleRuns"], reading);
    }
}
