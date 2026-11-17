using System.Net;
using System.Text.Json;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each role call records the provider, model, prompt template version, latency, token usage,
/// structured result, and validation outcome (FR-TEAM-8). A cycle is recorded once, when it
/// ends, with the calls of its roles. See "The record of a cycle" in docs/team.md.
/// </summary>
public class TeamRecordTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The team of the agents that are called through the gateway: each role at a provider of its own, as a new agent starts.</summary>
    private static readonly IReadOnlyDictionary<Role, string> Team = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    [Fact]
    public async Task Each_role_call_records_its_provider_model_template_versions_latency_token_usage_result_and_validation_outcome()
    {
        // The providers answer each role as its schema asks, after a moment. The first plan of
        // the Strategist risks half of the equity, which is more than the agent allows.
        var plans = 0;
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            var provider = ModelCalls.ProviderOf(request);
            var role = Team.Single(given => given.Value == provider).Key;
            var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);
            var answered = role == Role.Strategist && Interlocked.Increment(ref plans) == 1
                ? TeamAnswers.Plan(riskFraction: 0.5m)
                : TeamAnswers.Of(role);

            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellation);

            return StandInSystem.Json(HttpStatusCode.OK, offered ? ModelCalls.Searched(provider, answered) : ModelCalls.Finished(provider, answered));
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var (ada, agent) = await RunningAsync(application, Ada);

        await application.RunAsync(Close).Within();

        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // The cycle, under the id that everything logged for it carries.
        var cycle = await context.Set<DecisionCycle>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((agent, ada, TradingMode.Demo, Close, CycleOutcome.Approved), (cycle.AgentId, cycle.UserId, cycle.Mode, cycle.ClosedAt, cycle.Outcome));
        Assert.True(cycle.StartedAt <= cycle.EndedAt);
        Assert.Equal(
            cycle.Id.ToString(),
            Assert.Single(application.Written(TeamLog.Area, TeamLog.Decided)).Text("CycleId"));

        // Five calls: each role once, and the Strategist once more.
        // A role is stored by its name, so the calls are put in the order of the roles here.
        var calls = (await (
            from output in context.Set<RoleOutput>().AsNoTracking()
            join model in context.Set<AiModel>().AsNoTracking() on output.ModelId equals model.Id
            select new { Output = output, model.Provider, model.Identifier })
            .ToListAsync(TestContext.Current.CancellationToken))
            .OrderBy(call => call.Output.Role)
            .ThenBy(call => call.Output.Attempt)
            .Select(call => new { call.Output, Provider = call.Provider.Value, call.Identifier })
            .ToList();

        Assert.Equal(
            [
                (Role.MarketAnalyst, 1, "gpt", "gpt-test-model", RoleCallOutcome.Succeeded, RoleValidation.Valid),
                (Role.SentimentAnalyst, 1, "grok", "grok-test-model", RoleCallOutcome.Succeeded, RoleValidation.Valid),
                (Role.Strategist, 1, "gemini", "gemini-test-model", RoleCallOutcome.Succeeded, RoleValidation.Rejected),
                (Role.Strategist, 2, "gemini", "gemini-test-model", RoleCallOutcome.Succeeded, RoleValidation.Valid),
                (Role.RiskManager, 1, "claude", "claude-test-model", RoleCallOutcome.Succeeded, RoleValidation.Valid),
            ],
            calls.Select(call => (call.Output.Role, call.Output.Attempt, call.Provider, call.Identifier, call.Output.Outcome, call.Output.Validation)));

        var inUse = await application.Application.PromptTemplates.InUseAsync(Market.Spot, Timeframe.FifteenMinutes, TestContext.Current.CancellationToken);

        Assert.All(calls.Select(call => call.Output), output =>
        {
            Assert.Equal((cycle.Id, ada), (output.CycleId, output.UserId));

            // The versions of the four templates that its prompt was composed from (FR-PRM-6).
            Assert.Equal(
                inUse.For(output.Role).Select(template => template.Id),
                [output.RoleTemplateId, output.MarketTemplateId, output.TimeframeTemplateId, output.ProfileTemplateId]);

            // How long the provider took, and the tokens that it counted.
            Assert.InRange(output.LatencyMs, 50, 30000);
            Assert.True(output.InputTokens > 0 && output.OutputTokens > 0);
            Assert.InRange(output.CalledAt, cycle.StartedAt, cycle.EndedAt);
        });
        Assert.All(
            calls.Where(call => call.Output.Role != Role.SentimentAnalyst),
            call => Assert.Equal((100, 50, (int?)null), (call.Output.InputTokens, call.Output.OutputTokens, call.Output.LiveSearches)));

        // The searches of the one call that offered a search, which its provider charges for.
        Assert.True(calls.Single(call => call.Output.Role == Role.SentimentAnalyst).Output.LiveSearches > 0);

        // The structured result of each call, on one line, and why the rejected one was rejected.
        Assert.All(calls.Where(call => call.Output.Validation == RoleValidation.Valid), call =>
        {
            Assert.Equal(TeamAnswers.Of(call.Output.Role), call.Output.Result);
            Assert.Null(call.Output.Rejection);
        });

        var rejected = calls.Single(call => call.Output.Validation == RoleValidation.Rejected).Output;

        Assert.Equal(("riskFraction: OutOfRange", TeamAnswers.Plan(riskFraction: 0.5m)), (rejected.Rejection, rejected.Result));

        // What the gateway logs of each call says which role it was for, with the user, the
        // agent, and the cycle (NFR-LOG-3).
        var logged = application.Application.Events().Where(written => written.Template?.StartsWith("AI call ", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(5, logged.Count);
        Assert.All(logged, written =>
        {
            Assert.Equal((ada.ToString(), agent.ToString(), cycle.Id.ToString()), (written.Text("UserId"), written.Text("AgentId"), written.Text("CycleId")));
            Assert.Equal(Team[Enum.Parse<Role>(written.Text("Role")!)], written.Text("Provider"));
        });
    }

    [Fact]
    public async Task A_model_is_called_with_the_key_of_its_own_user_which_is_kept_nowhere_and_is_offered_nothing_to_call()
    {
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            var provider = ModelCalls.ProviderOf(request);
            var role = Team.Single(given => given.Value == provider).Key;
            var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);

            return StandInSystem.Json(
                HttpStatusCode.OK,
                offered ? ModelCalls.Searched(provider, TeamAnswers.Of(role)) : ModelCalls.Finished(provider, TeamAnswers.Of(role)));
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var (ada, _) = await RunningAsync(application, Ada);
        var (grace, _) = await RunningAsync(application, Grace, symbol: "SOLUSDT");

        await application.RunAsync(Close).Within();

        // Four calls for each agent, each to the provider of its role.
        Assert.Equal(8, providers.Calls.Count);

        foreach (var (role, provider) in Team)
        {
            var sent = providers.Calls.Where(call => call.Address.Host == $"{provider}.example").ToList();
            var adasKey = Keys.Of(ada.ToString("N"), provider);
            var gracesKey = Keys.Of(grace.ToString("N"), provider);

            // One call with the stored key of each user, in a header, and with no other key (FR-KEY-1).
            Assert.Equal(2, sent.Count);
            Assert.Single(sent, call => call.Headers.Values.Any(header => header.Contains(adasKey, StringComparison.Ordinal)));
            Assert.Single(sent, call => call.Headers.Values.Any(header => header.Contains(gracesKey, StringComparison.Ordinal)));

            // The key is handed over for the call alone (FR-KEY-9): it is in no row, in no
            // address, and in no log event, in any form that its bytes could be written in.
            Assert.All(Keys.FormsOf(adasKey).Concat(Keys.FormsOf(gracesKey)), form =>
            {
                Assert.DoesNotContain(form, application.Application.EverythingStored(), StringComparison.Ordinal);
                Assert.DoesNotContain(form, application.Application.LogText(), StringComparison.Ordinal);
                Assert.All(sent, call => Assert.DoesNotContain(form, call.Address.ToString(), StringComparison.Ordinal));
            });

            // A model is offered nothing that it could call (NFR-SEC-6): no function and no
            // tool, but the live search of its own provider, for the Sentiment Analyst alone.
            Assert.All(sent, call =>
            {
                using var body = JsonDocument.Parse(call.Body!);

                Assert.Equal(role == Role.SentimentAnalyst, body.RootElement.TryGetProperty("tools", out var tools));
                Assert.False(body.RootElement.TryGetProperty("functions", out _));
                Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));

                if (role == Role.SentimentAnalyst)
                {
                    Assert.All(
                        tools.EnumerateArray(),
                        tool => Assert.Contains(tool.GetProperty("type").GetString(), new[] { "web_search", "x_search" }));
                }
            });
        }
    }

    [Fact]
    public async Task An_answer_that_asks_for_a_function_to_be_called_is_no_result_and_nothing_is_called()
    {
        // The model of the Risk Manager answers with a call of a tool that would place an
        // order, as a model does that was manipulated, and not with its review.
        const string CallsATool =
            """
            {
              "id": "msg_01", "type": "message", "role": "assistant", "model": "claude-test-model",
              "content": [ { "type": "tool_use", "id": "toolu_01", "name": "place_order", "input": { "side": "buy", "quantity": 100 } } ],
              "stop_reason": "tool_use", "stop_sequence": null,
              "usage": { "input_tokens": 100, "output_tokens": 50 }
            }
            """;

        var providers = new StandInSystem(request =>
        {
            var provider = ModelCalls.ProviderOf(request);

            return StandInSystem.Json(
                HttpStatusCode.OK,
                provider == "claude" ? CallsATool : ModelCalls.Finished(provider, TeamAnswers.Of(Team.Single(given => given.Value == provider).Key)));
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var (_, agent) = await RunningAsync(application, Ada, searches: false);

        await application.RunAsync(Close).Within();

        // What it asked for is read by nothing: the call gave no result, was made once more
        // at most, and the cycle ended with no decision and no new entry.
        var outputs = application.Application.Database.Column(
            "SELECT outcome || '/' || validation FROM role_outputs WHERE role = 'RiskManager' ORDER BY attempt");

        Assert.Equal(["InvalidResult/NoResult", "InvalidResult/NoResult"], outputs);
        Assert.Equal(["NoDecision"], application.Application.Database.Column("SELECT outcome FROM decision_cycles"));
        Assert.Equal(
            agent.ToString(),
            Assert.Single(application.Written(TeamLog.Area, TeamLog.NoDecision)).Text("AgentId"));

        // Nothing but the providers of the team was called, and each with a model call alone.
        Assert.All(providers.Calls, call => Assert.Contains(call.Address.Host.Split('.')[0], Team.Values));
        Assert.DoesNotContain("place_order", application.Application.EverythingStored(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cycle_is_stored_with_the_calls_of_its_roles_and_read_back_as_it_was()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close, mode: TradingMode.Demo);
        var cycleId = await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Reduced);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // The cycle: whose it is, which close it was for, when it ran, in which mode, and how it ended.
        var cycle = await context.Set<DecisionCycle>().AsNoTracking().OwnedBy(ada.Id).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((cycleId, agent, ada.Id), (cycle.Id, cycle.AgentId, cycle.UserId));
        Assert.Equal((TradingMode.Demo, CycleOutcome.Reduced), (cycle.Mode, cycle.Outcome));
        Assert.Equal((Close, Close.AddSeconds(2), Close.AddSeconds(40)), (cycle.ClosedAt, cycle.StartedAt, cycle.EndedAt));

        // A call of each role, each with the model of its role, the four templates that are in
        // use for the agent, and what was answered.
        var outputs = await context.Set<RoleOutput>()
            .AsNoTracking()
            .OwnedBy(ada.Id)
            .OrderBy(output => output.Role)
            .ToListAsync(TestContext.Current.CancellationToken);
        var models = StoredAgentRole.Of(application.Database, agent).ToDictionary(given => given.Role, given => given.ModelId);
        var inUse = await application.PromptTemplates.InUseAsync(Market.Spot, Timeframe.OneHour, TestContext.Current.CancellationToken);

        Assert.Equal(Enum.GetValues<Role>(), outputs.Select(output => output.Role).Order());
        Assert.All(outputs, output =>
        {
            Assert.Equal((cycleId, ada.Id, 1), (output.CycleId, output.UserId, output.Attempt));
            Assert.Equal(models[output.Role.ToString()], output.ModelId);
            Assert.Equal(
                inUse.For(output.Role).Select(template => template.Id),
                [output.RoleTemplateId, output.MarketTemplateId, output.TimeframeTemplateId, output.ProfileTemplateId]);
            Assert.Equal((1500, 1200, 150), (output.LatencyMs, output.InputTokens, output.OutputTokens));
            Assert.Equal((RoleCallOutcome.Succeeded, RoleValidation.Valid), (output.Outcome, output.Validation));
            Assert.Null(output.Rejection);
            Assert.Equal(TeamAnswers.Of(output.Role), output.Result);
        });

        // What a member of a fixed set is stored as: its name.
        Assert.Equal(["Reduced"], application.Database.Column("SELECT outcome FROM decision_cycles"));
        Assert.Equal(["Succeeded/Valid"], application.Database.Column("SELECT DISTINCT outcome || '/' || validation FROM role_outputs"));
    }

    [Fact]
    public async Task A_cycle_and_its_calls_are_stored_together_or_not_at_all()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        var stored = await application.GiveCycleAsync(ada.Id, agent, Close);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var given = await context.Set<RoleOutput>().AsNoTracking().FirstAsync(TestContext.Current.CancellationToken);

        // A second call of a role with the number of the first: the database refuses it, and
        // nothing of the cycle that it came with is stored.
        var cycle = new DecisionCycle(Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, Close.AddHours(1), Close.AddHours(1), Close.AddHours(1), CycleOutcome.Hold);
        RoleOutput[] twice = [Copy(given, cycle.Id, ada.Id, attempt: 1), Copy(given, cycle.Id, ada.Id, attempt: 1)];

        await Assert.ThrowsAsync<DbUpdateException>(() => application.CycleRecords.StoreAsync(cycle, twice, [], TestContext.Current.CancellationToken));

        Assert.Equal([stored.ToString()], application.Database.Column("SELECT id::text FROM decision_cycles"));
        Assert.Equal(4, application.Database.Scalar<long>("SELECT count(*) FROM role_outputs"));

        // A call that was made once more is a row of its own.
        RoleOutput[] again = [Copy(given, cycle.Id, ada.Id, attempt: 1), Copy(given, cycle.Id, ada.Id, attempt: 2)];

        await application.CycleRecords.StoreAsync(cycle, again, [], TestContext.Current.CancellationToken);

        Assert.Equal(6, application.Database.Scalar<long>("SELECT count(*) FROM role_outputs"));
    }

    [Fact]
    [ProvesOwnershipOf("decision_cycles", "role_outputs")]
    public async Task The_record_of_a_cycle_is_of_the_user_of_its_agent_and_of_no_other()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Close);
        var graces = await application.GiveAgentAsync(grace.Id, Close);

        var gracesCycle = await application.GiveCycleAsync(grace.Id, graces, Close);
        var before = application.RowsOf(grace.Id);

        // A cycle of Ada's agent is stored as Ada's, and changes nothing of Grace's.
        var adasCycle = await application.GiveCycleAsync(ada.Id, adas, Close);

        Assert.Equal(before, application.RowsOf(grace.Id));

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // Whoever asks for the rows of a user finds those of that user, and no others.
        Assert.Equal(
            [adasCycle],
            await context.Set<DecisionCycle>().OwnedBy(ada.Id).Select(cycle => cycle.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [gracesCycle],
            await context.Set<RoleOutput>().OwnedBy(grace.Id).Select(output => output.CycleId).Distinct().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(
            await context.Set<DecisionCycle>().OwnedBy(ada.Id).Where(cycle => cycle.Id == gracesCycle).ToListAsync(TestContext.Current.CancellationToken));

        // The owner of a cycle is the owner of its agent, and the owner of a call the owner
        // of its cycle: the database holds to both, whatever wrote the row.
        var given = await context.Set<RoleOutput>().AsNoTracking().FirstAsync(output => output.CycleId == gracesCycle, TestContext.Current.CancellationToken);
        var asAda = new DecisionCycle(Guid.CreateVersion7(), graces, ada.Id, TradingMode.Demo, Close.AddHours(1), Close.AddHours(1), Close.AddHours(1), CycleOutcome.Hold);

        await Assert.ThrowsAsync<DbUpdateException>(() => application.CycleRecords.StoreAsync(asAda, [], [], TestContext.Current.CancellationToken));

        var gracesNext = new DecisionCycle(Guid.CreateVersion7(), graces, grace.Id, TradingMode.Demo, Close.AddHours(1), Close.AddHours(1), Close.AddHours(1), CycleOutcome.Hold);

        await Assert.ThrowsAsync<DbUpdateException>(() => application.CycleRecords.StoreAsync(
            gracesNext, [Copy(given, gracesNext.Id, ada.Id, attempt: 1)], [], TestContext.Current.CancellationToken));

        Assert.Equal(before, application.RowsOf(grace.Id));
        Assert.Single(application.RowsOf(ada.Id)["decision_cycles"]);
    }

    [Fact]
    public async Task The_record_of_a_cycle_goes_with_its_agent_and_the_templates_and_the_models_stay()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(ada.Id, Close, name: "Kept");
        var deleted = await application.GiveAgentAsync(ada.Id, Close, name: "Deleted", symbol: "SOLUSDT");
        var keptCycle = await application.GiveCycleAsync(ada.Id, kept, Close);
        await application.GiveCycleAsync(ada.Id, deleted, Close);

        var templates = application.Database.Scalar<long>("SELECT count(*) FROM prompt_templates");
        var models = application.Database.Scalar<long>("SELECT count(*) FROM ai_models");

        // The agent is deleted, as its user deletes one: its cycles and their calls go with it.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Agent>().Where(agent => agent.Id == deleted).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([keptCycle.ToString()], application.Database.Column("SELECT id::text FROM decision_cycles"));
        Assert.Equal([keptCycle.ToString()], application.Database.Column("SELECT DISTINCT cycle_id::text FROM role_outputs"));
        Assert.DoesNotContain(deleted.ToString(), application.EverythingStored(), StringComparison.Ordinal);

        // What a call points at stays: a version of a template and a model of the catalog are
        // never deleted, and a call that points at one keeps it from being deleted.
        Assert.Equal(templates, application.Database.Scalar<long>("SELECT count(*) FROM prompt_templates"));
        Assert.Equal(models, application.Database.Scalar<long>("SELECT count(*) FROM ai_models"));
        Assert.ThrowsAny<Exception>(() => application.Database.Execute("DELETE FROM ai_models"));
        Assert.ThrowsAny<Exception>(() => application.Database.Execute("DELETE FROM roles"));
    }

    /// <summary>
    /// Registers a user and gives them an agent that runs on 15 minutes, with a team of four
    /// providers and a stored key for each, as the application stores them.
    /// </summary>
    /// <returns>The ids of the user and of the agent.</returns>
    private static async Task<(Guid UserId, Guid AgentId)> RunningAsync(
        CyclesApplication application, string email, string symbol = "BTCUSDT", bool searches = true)
    {
        var user = await application.Application.RegisterVerifiedAsync(email);
        var agent = await application.Application.GiveAgentAsync(
            user.Id,
            Close.AddHours(-1),
            state: AgentState.Running,
            symbol: symbol,
            cause: AgentStateCause.UserStarted,
            team: false,
            timeframe: Timeframe.FifteenMinutes);

        foreach (var (role, provider) in Team)
        {
            // The file of the tests says that the models of Grok search live.
            var model = role == Role.SentimentAnalyst && !searches ? "sentiment-test-model" : $"{provider}-test-model";

            await application.Application.GiveRoleAsync(user.Id, agent, role, Close.AddHours(-1), provider, model);
        }

        return (user.Id, agent);
    }

    /// <summary>A call as another one, in a cycle and of a user that the test names.</summary>
    private static RoleOutput Copy(RoleOutput output, Guid cycleId, Guid userId, int attempt) =>
        new(
            Guid.CreateVersion7(),
            cycleId,
            userId,
            output.Role,
            attempt,
            output.ModelId,
            output.RoleTemplateId,
            output.MarketTemplateId,
            output.TimeframeTemplateId,
            output.ProfileTemplateId,
            output.CalledAt,
            output.LatencyMs,
            output.InputTokens,
            output.OutputTokens,
            output.LiveSearches,
            output.Outcome,
            output.Validation,
            output.Rejection,
            output.Result);
}
