using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using static Ecnanib.Tests.DecisionEngine.ComposedPrompts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// If a role call times out or returns an invalid result, it is retried once. If the Sentiment
/// Analyst still fails, the cycle continues with sentiment marked unavailable. If any other role
/// still fails, the cycle ends with no decision and no new entry (FR-TEAM-9). See "When a role
/// gives no valid result" in docs/team.md.
/// </summary>
public class TeamRetryTests
{
    private const string TimedOut = "TimedOut";
    private const string InvalidResult = "InvalidResult";
    private const string Rejected = "Rejected";

    private static readonly DateTimeOffset Start = new(2026, 11, 18, 10, 7, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextQuarter = new(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);

    /// <summary>Every role, with each way a call of it is made once more.</summary>
    public static TheoryData<Role, string> CallsThatAreMadeAgain => new MatrixTheoryData<Role, string>(
        Enum.GetValues<Role>(), [TimedOut, InvalidResult, Rejected]);

    [Theory]
    [MemberData(nameof(CallsThatAreMadeAgain))]
    public async Task A_call_that_times_out_or_returns_an_invalid_result_is_made_once_more(Role role, string failure)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());

        // The first call of the role fails, and the second is answered as it should be.
        models.Says(role, FailureOf(role, failure));

        await application.RunAsync(Quarter).Within();

        // The role was called twice, with the same prompt, and every other role once.
        var calls = models.CallsOf(role);

        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Request, calls[1].Request);
        Assert.Equal(5, models.Calls.Count);

        // The cycle went on with what the second call gave, as if the first had given it.
        var record = application.Records.Of(agent);

        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(
            [
                (1, OutcomeOf(failure), failure == Rejected ? RoleValidation.Rejected : RoleValidation.NoResult),
                (2, RoleCallOutcome.Succeeded, RoleValidation.Valid),
            ],
            record.Of(role).Select(output => (output.Attempt, output.Outcome, output.Validation)));
        Assert.Equal(TeamAnswers.Of(role), record.Of(role)[1].Result);
        Assert.Empty(application.Written(TeamLog.Area, TeamLog.NoDecision));
        Assert.Empty(application.Written(TeamLog.Area, TeamLog.SentimentUnavailable));
    }

    public static TheoryData<string> Failures => [TimedOut, InvalidResult, Rejected];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_cycle_whose_Sentiment_Analyst_still_fails_continues_with_sentiment_marked_unavailable(string failure)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());

        models.Says(Role.SentimentAnalyst, FailureOf(Role.SentimentAnalyst, failure), FailureOf(Role.SentimentAnalyst, failure));

        await application.RunAsync(Quarter).Within();

        // It was called once more, and no third time.
        Assert.Equal(2, models.CallsOf(Role.SentimentAnalyst).Count);

        // The Strategist reads that there is no sentiment report, with the report of the
        // Market Analyst as it is, and the cycle ends with the decision of the team.
        var read = PartOf(Assert.Single(models.CallsOf(Role.Strategist)).Request.Prompt, "Data package");

        Assert.EndsWith($"\n\nMarket report:\n{TeamAnswers.MarketReport}\n\nSentiment report: unavailable", read, StringComparison.Ordinal);
        Assert.Single(models.CallsOf(Role.RiskManager));

        var record = application.Records.Of(agent);

        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(2, record.Of(Role.SentimentAnalyst).Count);
        Assert.DoesNotContain(record.Of(Role.SentimentAnalyst), output => output.Validation == RoleValidation.Valid);

        // It is said once, as a warning, with the agent and the cycle.
        var unavailable = Assert.Single(application.Written(TeamLog.Area, TeamLog.SentimentUnavailable));

        Assert.Equal(("Warning", agent.ToString()), (unavailable.Level, unavailable.Text("AgentId")));
        Assert.True(unavailable.Has("CycleId"));
        Assert.Equal((1, 0), (application.SummaryOf(Quarter)["Ended"].GetInt32(), application.SummaryOf(Quarter)["Failed"].GetInt32()));
    }

    /// <summary>Every other role, with each way its two calls fail.</summary>
    public static TheoryData<Role, string> RolesThatAreNeeded => new MatrixTheoryData<Role, string>(
        [Role.MarketAnalyst, Role.Strategist, Role.RiskManager], [TimedOut, InvalidResult, Rejected]);

    [Theory]
    [MemberData(nameof(RolesThatAreNeeded))]
    public async Task A_cycle_whose_other_role_still_fails_ends_with_no_decision_and_no_new_entry(Role role, string failure)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());

        models.Says(role, FailureOf(role, failure), FailureOf(role, failure));

        await application.RunAsync(Quarter).Within();

        // It was called once more, and no third time, and no role after it was called at all.
        Assert.Equal(2, models.CallsOf(role).Count);
        Assert.All(
            Enum.GetValues<Role>().Where(later => later > role && later != Role.SentimentAnalyst),
            later => Assert.Empty(models.CallsOf(later)));

        // The cycle ended, with no decision: no plan goes on, so nothing is entered.
        var record = application.Records.Of(agent);

        Assert.Equal(CycleOutcome.NoDecision, record.Cycle.Outcome);
        Assert.Equal(2, record.Of(role).Count);
        Assert.Empty(application.Written(TeamLog.Area, TeamLog.Decided));

        var none = Assert.Single(application.Written(TeamLog.Area, TeamLog.NoDecision));

        Assert.Equal(("Warning", agent.ToString(), role.ToString()), (none.Level, none.Text("AgentId"), none.Text("Role")));

        // It is no error of the service, and no failure of the cycle: it ended.
        var summary = application.SummaryOf(Quarter);

        Assert.Equal((1, 0), (summary["Ended"].GetInt32(), summary["Failed"].GetInt32()));
        Assert.DoesNotContain(
            application.Written().Concat(application.Written(TeamLog.Area)).Concat(application.Written(TeamLog.RolesArea)),
            written => written.Level is "Error" or "Fatal");
    }

    [Fact]
    public async Task A_result_that_is_rejected_says_which_field_broke_which_rule_and_nothing_of_the_answer()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        // A plan that risks half of the equity, with a rationale that tries to be read in the log.
        var answered = TeamAnswers.Plan(riskFraction: 0.5m, rationale: "ALL-IN-NOW");

        application.Models.Says(Role.Strategist, answered);

        await application.RunAsync(Quarter).Within();

        var rejected = Assert.Single(application.Written(TeamLog.RolesArea, TeamLog.Rejected));

        Assert.Equal("Warning", rejected.Level);
        Assert.Equal(
            ("Strategist", 1, "riskFraction", "OutOfRange"),
            (rejected.Text("Role"), rejected["Attempt"].GetInt32(), rejected.Text("RejectedField"), rejected.Text("RejectionRule")));
        Assert.Equal(agent.ToString(), rejected.Text("AgentId"));
        Assert.DoesNotContain("ALL-IN-NOW", application.Application.LogText(), StringComparison.Ordinal);

        // The record keeps why, and what was answered.
        var first = application.Records.Of(agent).Of(Role.Strategist)[0];

        Assert.Equal((RoleValidation.Rejected, "riskFraction: OutOfRange", answered), (first.Validation, first.Rejection, first.Result));
    }

    public static TheoryData<Role, AiCallOutcome> CallsThatAreNotMadeAgain => new MatrixTheoryData<Role, AiCallOutcome>(
        Enum.GetValues<Role>(), [AiCallOutcome.Failed, AiCallOutcome.AuthenticationFailed, AiCallOutcome.ModelNotFound]);

    [Theory]
    [MemberData(nameof(CallsThatAreNotMadeAgain))]
    public async Task A_call_that_fails_otherwise_is_not_made_again(Role role, AiCallOutcome outcome)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());

        models.Says(role, new AiCallResult(outcome, null, null, TimeSpan.FromSeconds(0.25)));

        await application.RunAsync(Quarter).Within();

        // The same key and the same model would be refused again, and a provider that is down
        // is not asked twice: the role gave no result.
        var call = Assert.Single(application.Records.Of(agent).Of(role));

        Assert.Single(models.CallsOf(role));
        Assert.Equal((Enum.Parse<RoleCallOutcome>(outcome.ToString()), RoleValidation.NoResult, 250), (call.Outcome, call.Validation, call.LatencyMs));
        Assert.Null(call.Result);

        // The cycle then goes on as the role says: without sentiment, or with no decision.
        Assert.Equal(
            role == Role.SentimentAnalyst ? CycleOutcome.Approved : CycleOutcome.NoDecision,
            application.Records.Of(agent).Cycle.Outcome);
    }

    [Fact]
    public async Task A_key_that_a_provider_refuses_is_checked_again_once_in_the_cycle()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var ada = Guid.NewGuid();

        // One provider holds both analysts, and refuses the key in both calls.
        application.Agents!.Give(ada, providers: ["grok", "grok", "gemini", "claude"]);
        application.Models.Says(Role.MarketAnalyst, StandInModels.Refused);
        application.Models.Says(Role.SentimentAnalyst, StandInModels.Refused);

        await application.RunAsync(Quarter).Within();

        // The provider is asked about the key, which marks a key that it refuses again and
        // tells what depends on it (FR-KEY-3, FR-KEY-10). The cycle is over when that is.
        Assert.Equal([(ada, "grok")], application.Team!.KeyChecks.Checked);

        var said = Assert.Single(application.Written(TeamLog.RolesArea, TeamLog.CheckedAgain));

        Assert.Equal((ada.ToString(), "grok", "Invalid"), (said.Text("UserId"), said.Text("Provider"), said.Text("KeyCheckOutcome")));
    }

    [Theory]
    [InlineData(KeyHandOverOutcome.NoKey)]
    [InlineData(KeyHandOverOutcome.Invalid)]
    [InlineData(KeyHandOverOutcome.Unreadable)]
    public async Task A_role_whose_user_has_no_key_that_can_be_used_is_not_called_and_gives_no_result(KeyHandOverOutcome why)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var models = application.Models;
        var ada = Guid.NewGuid();
        var agent = application.Agents!.Give(ada, providers: ["gpt", "grok", "gemini", "claude"]);

        // The key of the Strategist's provider went while the cycle was under way.
        application.Team!.Keys.Outcome = (_, provider) => provider.Value == "gemini" ? why : KeyHandOverOutcome.Handed;

        await application.RunAsync(Quarter).Within();

        // No call was made without a key, and none with another: the cycle ends with no decision.
        Assert.Empty(models.CallsOf(Role.Strategist));
        Assert.Empty(models.CallsOf(Role.RiskManager));
        Assert.Single(application.Team.Keys.Asked, asked => asked.Provider == "gemini");

        var record = application.Records.Of(agent);
        var call = Assert.Single(record.Of(Role.Strategist));

        Assert.Equal(CycleOutcome.NoDecision, record.Cycle.Outcome);
        Assert.Equal((RoleCallOutcome.NoKey, RoleValidation.NoResult, 0), (call.Outcome, call.Validation, call.LatencyMs));

        var said = Assert.Single(application.Written(TeamLog.RolesArea, TeamLog.NoKey));

        Assert.Equal(("Warning", ada.ToString(), "gemini", why.ToString()), (said.Level, said.Text("UserId"), said.Text("Provider"), said.Text("KeyHandOverOutcome")));
        Assert.Equal((1, 0), (application.SummaryOf(Quarter)["Ended"].GetInt32(), application.SummaryOf(Quarter)["Failed"].GetInt32()));
    }

    [Fact]
    public async Task A_cycle_that_is_stopped_is_recorded_as_stopped_with_the_calls_that_it_made()
    {
        using var application = new CyclesApplication(Start, buildsPackages: true);
        var models = application.Models;
        var agent = application.Agents!.Give(Guid.NewGuid());
        await application.SchedulerStartedAsync();

        // The scheduler starts the cycle at the close, and its Strategist does not answer.
        models.Hold(Role.Strategist);
        application.Clock.Set(Quarter);
        await models.StartedAsync(Role.Strategist);

        // 45 seconds before the next close the cycle is stopped, through its token.
        application.Clock.Set(NextQuarter.AddSeconds(-45));
        await Wait.UntilAsync(() => application.Records.Stored.Count == 1, "the cycle to be recorded");

        // The record says so, with the two calls that were answered and the one that was under way.
        var record = application.Records.Of(agent);

        Assert.Equal(CycleOutcome.Stopped, record.Cycle.Outcome);
        Assert.Equal(
            [
                (Role.MarketAnalyst, 1, RoleCallOutcome.Succeeded, RoleValidation.Valid),
                (Role.SentimentAnalyst, 1, RoleCallOutcome.Succeeded, RoleValidation.Valid),
                (Role.Strategist, 1, RoleCallOutcome.Canceled, RoleValidation.NoResult),
            ],
            record.Calls);
        Assert.Empty(models.CallsOf(Role.RiskManager));

        // What started the cycle counts it as stopped, as it does, and not as failed.
        await Wait.UntilAsync(
            () => application.Written(CyclesLog.Area, CyclesLog.Over).Count == 1, "the cycles of the close to be over");

        var stopped = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.StoppedForTheNextClose));
        var summary = application.SummaryOf(Quarter);

        Assert.Equal(agent.ToString(), stopped.Text("AgentId"));
        Assert.Equal((1, 0), (summary["OutOfTime"].GetInt32(), summary["Failed"].GetInt32()));
        Assert.DoesNotContain(application.Written(TeamLog.Area), written => written.Level is "Error" or "Fatal");
    }

    [Fact]
    public async Task A_cycle_that_fails_is_recorded_as_failed_and_fails_with_its_own_error()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        // The exchange does not give the candles of an hour.
        application.Market!.Candles = asked => asked.Interval == CandleInterval.OneHour
            ? StandInMarket.NoCandles(ExchangeCallOutcome.TimedOut)
            : null;

        // And the record of the cycle cannot be stored either.
        application.Records.Fails = new InvalidOperationException("The database is away.");

        await application.RunAsync(Quarter).Within();

        // The cycle failed with what ended it, and not with what came after.
        var failed = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.Failed));

        Assert.Contains("the exchange did not give the 1H candles (TimedOut)", failed.Exception, StringComparison.Ordinal);

        // That its record could not be stored is said beside it.
        var lost = Assert.Single(application.Written(TeamLog.Area, TeamLog.NotRecorded));

        Assert.Equal(("Error", agent.ToString(), "Failed"), (lost.Level, lost.Text("AgentId"), lost.Text("CycleOutcome")));
        Assert.Contains("The database is away.", lost.Exception, StringComparison.Ordinal);
        Assert.Empty(application.Models.Calls);
    }

    [Fact]
    public async Task A_cycle_whose_record_cannot_be_stored_has_failed()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        application.Records.Fails = new InvalidOperationException("The database is away.");

        await application.RunAsync(Quarter).Within();

        // The team decided, and nothing says so for good: the cycle is one that failed.
        Assert.Equal(4, application.Models.Calls.Count);

        var failed = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.Failed));

        Assert.Equal(agent.ToString(), failed.Text("AgentId"));
        Assert.Contains("The database is away.", failed.Exception, StringComparison.Ordinal);
        Assert.Equal((0, 1), (application.SummaryOf(Quarter)["Ended"].GetInt32(), application.SummaryOf(Quarter)["Failed"].GetInt32()));
    }

    /// <summary>A first call of a role that fails in one of the ways that it is made once more for.</summary>
    private static AiCallResult FailureOf(Role role, string failure) => failure switch
    {
        TimedOut => StandInModels.TimedOut,
        InvalidResult => StandInModels.Invalid,

        // An answer that is a JSON object, and that is outside the schema or a range of its role.
        _ => StandInModels.Answered(role switch
        {
            Role.MarketAnalyst => TeamAnswers.MarketReport.Replace("bullish", "sideways", StringComparison.Ordinal),
            Role.SentimentAnalyst => TeamAnswers.Sentiment.Replace("0.6", "7", StringComparison.Ordinal),
            Role.Strategist => TeamAnswers.Plan(riskFraction: 0.5m),

            // A reduction to more than the plan risks.
            _ => TeamAnswers.Review("reduce", 0.5m),
        }),
    };

    private static RoleCallOutcome OutcomeOf(string failure) => failure switch
    {
        TimedOut => RoleCallOutcome.TimedOut,
        InvalidResult => RoleCallOutcome.InvalidResult,
        _ => RoleCallOutcome.Succeeded,
    };
}
