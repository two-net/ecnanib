using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using static Ecnanib.Tests.DecisionEngine.ComposedPrompts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Roles run in the order of the decision cycle (FR-TEAM-6, SRS 3.2): the Market Analyst and the
/// Sentiment Analyst in parallel, then the Strategist, then the Risk Manager, which reviews a
/// plan that opens a position (FR-TEAM-7). See "The order" in docs/team.md.
/// </summary>
public class TeamOrderTests
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    private static readonly string[] FourProviders = ["gpt", "grok", "gemini", "claude"];

    [Fact]
    public async Task The_two_analysts_run_at_the_same_time_then_the_Strategist_then_the_Risk_Manager()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());

        // No call ends until the test lets it.
        models.Hold(Enum.GetValues<Role>());
        var run = application.RunAsync(Quarter);

        // Step 3: both analysts are called, and both calls are under way at once.
        await models.StartedAsync(Role.MarketAnalyst);
        await models.StartedAsync(Role.SentimentAnalyst);

        Assert.Equal([Role.MarketAnalyst, Role.SentimentAnalyst], models.Roles.Order());
        Assert.Equal(2, models.MostAtOnce);

        // Step 4: the Strategist is called when both are over, with what both answered.
        models.Release(Role.SentimentAnalyst);
        models.Release(Role.MarketAnalyst);
        await models.StartedAsync(Role.Strategist);

        Assert.Equal(Role.Strategist, models.Roles[2]);

        var read = PartOf(models.CallsOf(Role.Strategist)[0].Request.Prompt, "Data package");

        Assert.Contains($"Market report:\n{TeamAnswers.MarketReport}", read, StringComparison.Ordinal);
        Assert.Contains($"Sentiment report: no live data\n{TeamAnswers.Sentiment}", read, StringComparison.Ordinal);

        // Step 5: the Risk Manager is called when the Strategist is over, with its plan.
        models.Release(Role.Strategist);
        await models.StartedAsync(Role.RiskManager);

        Assert.Equal(Role.RiskManager, models.Roles[3]);
        Assert.EndsWith(
            $"Trade plan:\n{TeamAnswers.Plan()}",
            PartOf(models.CallsOf(Role.RiskManager)[0].Request.Prompt, "Data package"),
            StringComparison.Ordinal);

        models.Release(Role.RiskManager);
        await run.Within();

        // Each role once, the analysts first and together, and never more than the two at once.
        Assert.Equal(4, models.Calls.Count);
        Assert.Equal([Role.Strategist, Role.RiskManager], models.Roles.Skip(2));
        Assert.Equal(2, models.MostAtOnce);

        // The cycle ended with the decision of the team, and is recorded with its four calls.
        var record = application.Records.Of(agent);

        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(
            Enum.GetValues<Role>().Select(role => (role, 1, RoleCallOutcome.Succeeded, RoleValidation.Valid)),
            record.Calls);

        var decided = Assert.Single(application.Written(TeamLog.Area, TeamLog.Decided));

        Assert.Equal(("Information", "Approved", "OpenLong"), (decided.Level, decided.Text("CycleOutcome"), decided.Text("PlanAction")));
        Assert.Equal(agent.ToString(), decided.Text("AgentId"));
        Assert.True(decided.Has("CycleId"));
        Assert.Equal((1, 0), (application.SummaryOf(Quarter)["Ended"].GetInt32(), application.SummaryOf(Quarter)["Failed"].GetInt32()));
    }

    public static TheoryData<string, string, bool, CycleOutcome> Plans => new()
    {
        // A plan that opens a position is reviewed.
        { "open long", TeamAnswers.Plan("open_long"), true, CycleOutcome.Approved },
        { "open short", TeamAnswers.Plan("open_short"), true, CycleOutcome.Approved },
        { "reverse", TeamAnswers.Plan("reverse"), true, CycleOutcome.Approved },

        // A plan that holds, closes a position, or adjusts protection is not: nothing stands
        // in the way of reducing risk.
        { "hold", TeamAnswers.Hold, false, CycleOutcome.Hold },
        { "close", TeamAnswers.Close, false, CycleOutcome.Unreviewed },
        { "adjust protection", TeamAnswers.Adjust, false, CycleOutcome.Unreviewed },
    };

    [Theory]
    [MemberData(nameof(Plans))]
    public async Task The_Risk_Manager_is_called_for_a_plan_that_opens_a_position_and_for_no_other(
        string what, string plan, bool reviewed, CycleOutcome outcome)
    {
        // The agent is on Futures, which allows every action, and holds a position where the
        // plan is one that closes, reverses, or adjusts one (FR-CYC-4, FR-CYC-5).
        using var application = new CyclesApplication(Quarter, buildsPackages: true, holdings: PlanAgents.HoldingFor(plan));
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        models.Says(Role.Strategist, plan);

        // A Risk Manager that would veto whatever it is asked about.
        models.Says(Role.RiskManager, _ => StandInModels.Answered(TeamAnswers.Review(reviewed ? "approve" : "veto")));

        await application.RunAsync(Quarter).Within();

        Assert.True(
            reviewed == (models.CallsOf(Role.RiskManager).Count == 1),
            $"A plan to {what} was {(reviewed ? "not reviewed" : "handed to the Risk Manager")}.");
        Assert.Equal(reviewed ? 4 : 3, models.Calls.Count);

        // A plan that is not reviewed is not blocked: the cycle ends with it.
        var record = application.Records.Of(agent);

        Assert.Equal(outcome, record.Cycle.Outcome);
        Assert.Equal(reviewed ? 1 : 0, record.Of(Role.RiskManager).Count);
        Assert.Equal(outcome.ToString(), Assert.Single(application.Written(TeamLog.Area, TeamLog.Decided)).Text("CycleOutcome"));
    }

    [Theory]
    [InlineData("approve", null, CycleOutcome.Approved)]
    [InlineData("reduce", 0.004, CycleOutcome.Reduced)]
    [InlineData("veto", null, CycleOutcome.Vetoed)]
    public async Task What_the_Risk_Manager_makes_of_a_plan_is_how_the_cycle_ends(string verdict, double? reduced, CycleOutcome outcome)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        application.Models.Says(Role.RiskManager, TeamAnswers.Review(verdict, (decimal?)reduced));

        await application.RunAsync(Quarter).Within();

        var record = application.Records.Of(agent);

        Assert.Equal(outcome, record.Cycle.Outcome);
        Assert.Equal(TeamAnswers.Review(verdict, (decimal?)reduced), Assert.Single(record.Of(Role.RiskManager)).Result);

        // The cycle ended, whatever was made of its plan: a veto is a decision, and no failure.
        var summary = application.SummaryOf(Quarter);

        Assert.Equal((1, 0), (summary["Ended"].GetInt32(), summary["Failed"].GetInt32()));
        Assert.DoesNotContain(application.Written(TeamLog.Area), written => written.Level is "Warning" or "Error");
    }

    [Fact]
    public async Task Each_role_is_called_with_the_model_that_its_user_gave_it_and_with_the_key_of_that_user()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        // Ada's team is of four providers, and Grace's of one.
        application.Agents!.Give(ada, providers: FourProviders);
        application.Agents.Give(grace, symbol: "SOLUSDT", providers: ["gemini"]);

        await application.RunAsync(Quarter).Within();

        Assert.Equal(8, models.Calls.Count);

        // Ada's roles, in their order: each at the provider of its model, with that model.
        var adas = models.Calls.Where(call => call.ApiKey.Contains(ada.ToString("N"), StringComparison.Ordinal)).OrderBy(call => call.Role).ToList();

        Assert.Equal(Enum.GetValues<Role>(), adas.Select(call => call.Role));
        Assert.Equal(FourProviders, adas.Select(call => call.Provider.Value));
        Assert.Equal(FourProviders.Select(provider => $"{provider}-test-model"), adas.Select(call => call.Request.Model));

        // Every call is made with the key of the agent's own user for that provider (FR-KEY-1).
        Assert.All(adas, call => Assert.Equal(StandInKeys.KeyOf(ada, call.Provider), call.ApiKey));
        Assert.All(
            models.Calls.Except(adas),
            call => Assert.Equal((StandInKeys.KeyOf(grace, new AiProviderCode("gemini")), "gemini"), (call.ApiKey, call.Provider.Value)));

        // The key is asked for as each call is made, and for no other user or provider.
        Assert.Equal(
            adas.Select(call => (ada, call.Provider.Value)).Concat(Enumerable.Repeat((grace, "gemini"), 4)).Order(),
            application.Team!.Keys.Asked.Order());

        // Each role is sent the schema of its own result, and only the Sentiment Analyst may
        // search, with a model that can: the file of the tests says that those of Grok do.
        Assert.All(models.Calls, call => Assert.Equal(call.Role == Role.SentimentAnalyst && call.Provider.Value == "grok", call.Request.LiveSearch));
        Assert.Equal(4, models.Calls.Select(call => call.Request.ResultSchema).Distinct().Count());

        // What the Strategist reads of the report says whether it rests on a search.
        Assert.Contains(
            "Sentiment report: live data\n",
            adas.Single(call => call.Role == Role.Strategist).Request.Prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cycle_of_an_agent_whose_team_is_not_whole_fails_before_any_model_is_called()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);

        // A team of three: the Risk Manager has no model.
        var agent = application.Agents!.Give(Guid.NewGuid(), providers: ["gpt", "grok", "gemini"]);

        await application.RunAsync(Quarter).Within();

        Assert.Empty(application.Models.Calls);
        Assert.DoesNotContain(application.Market!.Asked, asked => asked.Interval != CandleInterval.FifteenMinutes);

        var failed = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.Failed));

        Assert.Equal(agent.ToString(), failed.Text("AgentId"));
        Assert.Contains("The RiskManager of the agent has no model", failed.Exception, StringComparison.Ordinal);
        Assert.Equal(CycleOutcome.Failed, application.Records.Of(agent).Cycle.Outcome);
    }

    [Fact]
    public async Task Each_role_of_a_cycle_is_sent_the_inputs_that_are_defined_for_it_and_no_others()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;

        application.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        await application.RunAsync(Quarter).Within();

        string DataOf(Role role) => PartOf(Assert.Single(models.CallsOf(role)).Request.Prompt, "Data package");

        // The Market Analyst reads the data package of the cycle: price, volume, indicators,
        // and on Futures the funding and the open interest.
        var package = DataOf(Role.MarketAnalyst);

        Assert.StartsWith("Closed 15m candles: ", package, StringComparison.Ordinal);
        Assert.Contains("\nFutures: mark price ", package, StringComparison.Ordinal);
        Assert.DoesNotContain("report", package, StringComparison.OrdinalIgnoreCase);

        // The Sentiment Analyst reads which close the cycle is for, and no candle.
        Assert.Equal("Candle close: 2026-11-18T10:15Z", DataOf(Role.SentimentAnalyst));

        // The Strategist reads the package and both reports, and the Risk Manager the package
        // and the plan.
        Assert.Equal(
            $"{package}\n\nMarket report:\n{TeamAnswers.MarketReport}\n\nSentiment report: no live data\n{TeamAnswers.Sentiment}",
            DataOf(Role.Strategist));
        Assert.Equal($"{package}\n\nTrade plan:\n{TeamAnswers.Plan()}", DataOf(Role.RiskManager));

        // Every prompt has the seven parts, and no role is given a lesson: the agent has none
        // to recall. CycleLessonTests has an agent that has.
        Assert.All(models.Calls, call =>
        {
            Assert.Equal(Headings, HeadingsOf(call.Request.Prompt));
            Assert.Equal("None.", PartOf(call.Request.Prompt, "Recalled lessons"));
        });
    }
}
