using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// An agent enters Attention required when calls fail repeatedly (SRS 3.3), and that is when a
/// provider keeps refusing the key of its user: the calls with the key were paused and made
/// again, and the provider took none (NFR-REL-4). A provider that answers nobody moves no
/// agent: its calls are paused and made again, and its agents go on. See "Calls that fail
/// repeatedly" in docs/reliability.md.
/// </summary>
public class CallTroubleTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string TroubleArea = "Ecnanib.DecisionEngine.CallTrouble";

    private const string NotOfOneKey =
        "The AI provider {Provider} keeps refusing the key of user {UserId} for the {Role} of agent {AgentId}, as it refuses {KeysInTrouble} keys at the same time. That is no trouble of one key, so the agent is not moved";

    private const string NotDone =
        "What follows from the AI provider {Provider} refusing the key of user {UserId} could not be done for agent {AgentId}";

    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    /// <summary>
    /// The time between two closes of a test. The scheduler of the application starts the job
    /// by itself when the clock passes one quarter hour, beside the run of the test, and skips
    /// a quarter hour that it finds passed together with the one after it: so a test that runs
    /// the job itself moves its clock by two quarter hours, once the scheduler works.
    /// </summary>
    private static readonly TimeSpan Half = TimeSpan.FromMinutes(30);

    /// <summary>Each role at a provider of its own, as a new agent starts: the Market Analyst is called at GPT.</summary>
    private static readonly IReadOnlyDictionary<Role, string> Team = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    /// <summary>
    /// The file of the tests, in which one refusal pauses the calls with a key, so that each
    /// close is one pause more: the file says that the second pause in a row moves the agent.
    /// </summary>
    private static SettingsJson OneRefusalPauses(SettingsJson file) => file.With("Breakers:AiKey:Failures", 1);

    [Fact]
    public async Task An_agent_whose_key_a_provider_keeps_refusing_waits_for_its_user_who_is_told()
    {
        // GPT refuses every call with the key of Ada, as a provider does whose limit on the
        // requests of a key is used up, and answers everyone else.
        Guid? refused = null;
        var providers = Providers(request => refused is { } user && HasKeyOf(request, user, "gpt") ? HttpStatusCode.TooManyRequests : null);

        using var application = new CyclesApplication(Close, providers: providers, settings: OneRefusalPauses);
        var (ada, adas) = await RunningAsync(application, Ada);
        var (grace, graces) = await RunningAsync(application, Grace, symbol: "SOLUSDT");
        refused = ada;
        await application.SchedulerStartedAsync();

        // The first close: the call is refused, the calls with the key are paused, and the cycle
        // ends with no decision. One pause is none in a row: the agent goes on.
        await application.RunAsync(Close).Within();

        Assert.Equal("Running", StateOf(application, adas).State);
        Assert.Empty(await application.Application.ToldAsync(ada));

        // The second close: the pause is over, the call is made again, and refused again. That
        // is the second pause in a row, with no answer between them.
        application.Clock.Set(Close + Half);
        await application.RunAsync(Close + Half).Within();

        Assert.Equal(("AttentionRequired", "CallsFailRepeatedly"), (StateOf(application, adas).State, StateOf(application, adas).StateCause));
        Assert.Equal(
            ["Running: UserStarted", "AttentionRequired: CallsFailRepeatedly"],
            StoredAgentStateChange.Told(application.Application.Database, adas).TakeLast(2));

        // Its user is told once: which agent, which role, what a provider refuses a key for, and
        // what to do. Nothing of the key is said, and no provider is named.
        var (type, about, says) = Assert.Single(await application.Application.ToldAsync(ada));

        Assert.Equal((NotificationType.AttentionRequired, adas), (type, about));
        Assert.Equal(
            "The agent in BTCUSDT (Demo) waits for you: the AI provider of its Market Analyst keeps refusing the calls with your " +
            "key. That is a limit on the requests of the key, an account at the provider that has run out, or a model that does " +
            "not take the call. See to it at the provider, or give the role another model, and resume the agent.",
            says);

        // Neither cycle decided, and neither opened a position: each ended with the call that failed.
        var cycles = await CyclesOfAsync(application, adas);

        Assert.Equal([CycleOutcome.NoDecision, CycleOutcome.NoDecision], cycles.Select(cycle => cycle.Outcome));
        Assert.Equal(
            [RoleCallOutcome.Failed, RoleCallOutcome.Failed],
            (await CallsOfAsync(application, adas, Role.MarketAnalyst)).Select(call => call.Outcome));

        // The agent of another user, whose key the same provider takes, decided at both closes.
        Assert.Equal("Running", StateOf(application, graces).State);
        Assert.Empty(await application.Application.ToldAsync(grace));
        Assert.Equal(
            [CycleOutcome.Approved, CycleOutcome.Approved], (await CyclesOfAsync(application, graces)).Select(cycle => cycle.Outcome));

        // An agent that waits runs no cycle: the provider is not called with the key again.
        var before = providers.Calls.Count(call => HasKeyOf(call, ada, "gpt"));

        application.Clock.Set(Close + (2 * Half));
        await application.RunAsync(Close + (2 * Half)).Within();

        Assert.Equal(2, before);
        Assert.Equal(before, providers.Calls.Count(call => HasKeyOf(call, ada, "gpt")));
    }

    [Fact]
    public async Task A_provider_that_answers_nobody_moves_no_agent_and_is_called_again_when_its_pause_is_over()
    {
        // GPT is down for everyone. One failure pauses its calls, for forty minutes: longer
        // than the time between two closes of this test.
        var down = true;
        var providers = Providers(request => down && ModelCalls.ProviderOf(request) == "gpt" ? HttpStatusCode.ServiceUnavailable : null);

        using var application = new CyclesApplication(
            Close,
            providers: providers,
            settings: file => file
                .With("Breakers:AiProvider:Failures", 1)
                .With("Breakers:AiProvider:PauseSeconds", 2400)
                .With("Breakers:AiProvider:LongestPauseSeconds", 2400));
        var (ada, agent) = await RunningAsync(application, Ada);
        await application.SchedulerStartedAsync();

        // The first close finds the provider down, and the second finds its calls paused: no
        // call is made, and the cycle ends as one whose call failed.
        await application.RunAsync(Close).Within();

        application.Clock.Set(Close + Half);
        await application.RunAsync(Close + Half).Within();

        Assert.Equal(1, providers.Calls.Count(call => call.Address.Host == "gpt.example"));

        // The third is after the pause, and the provider answers again.
        down = false;
        application.Clock.Set(Close + (2 * Half));
        await application.RunAsync(Close + (2 * Half)).Within();

        Assert.Equal(
            [CycleOutcome.NoDecision, CycleOutcome.NoDecision, CycleOutcome.Approved],
            (await CyclesOfAsync(application, agent)).Select(cycle => cycle.Outcome));

        // The call that was not made is of the record, as what it is, with no time and no tokens.
        var calls = await CallsOfAsync(application, agent, Role.MarketAnalyst);

        Assert.Equal(
            [RoleCallOutcome.Failed, RoleCallOutcome.ProviderPaused, RoleCallOutcome.Succeeded], calls.Select(call => call.Outcome));
        Assert.Equal((0, null, null), (calls[1].LatencyMs, calls[1].InputTokens, calls[1].OutputTokens));

        // The agent ran all along, and its user was told nothing: there is nothing for them to do.
        Assert.Equal("Running", StateOf(application, agent).State);
        Assert.DoesNotContain("AttentionRequired: CallsFailRepeatedly", StoredAgentStateChange.Told(application.Application.Database, agent));
        Assert.Empty(await application.Application.ToldAsync(ada));
        Assert.DoesNotContain(application.Application.Events(), written => written.Area == TroubleArea);
    }

    [Fact]
    public async Task When_many_keys_of_a_provider_are_refused_at_once_no_agent_is_moved()
    {
        // GPT refuses every key, as it does when it blocks the address that the service calls
        // from. The file of this test says that two keys at once are no trouble of one key.
        var providers = Providers(request => ModelCalls.ProviderOf(request) == "gpt" ? HttpStatusCode.Forbidden : null);

        using var application = new CyclesApplication(
            Close, providers: providers, settings: file => OneRefusalPauses(file).With("Breakers:AiKey:ProviderWideKeys", 2));
        var (ada, adas) = await RunningAsync(application, Ada);
        var (grace, graces) = await RunningAsync(application, Grace, symbol: "SOLUSDT");
        await application.SchedulerStartedAsync();

        await application.RunAsync(Close).Within();

        application.Clock.Set(Close + Half);
        await application.RunAsync(Close + Half).Within();

        // Each key has had its second pause in a row, and neither agent is moved or told of.
        Assert.All(new[] { adas, graces }, agent => Assert.Equal("Running", StateOf(application, agent).State));
        Assert.Empty(await application.Application.ToldAsync(ada));
        Assert.Empty(await application.Application.ToldAsync(grace));

        // It is said in the log, for whoever runs the service, once for each agent.
        var said = application.Application.Events().Where(written => written.Area == TroubleArea).ToList();

        Assert.Equal(2, said.Count);
        Assert.All(said, written =>
        {
            Assert.Equal((NotOfOneKey, "Warning"), (written.Template, written.Level));
            Assert.Equal(("gpt", "MarketAnalyst", "2"), (written.Text("Provider"), written.Text("Role"), written["KeysInTrouble"].ToString()));
        });
        Assert.Equal(
            new[] { adas, graces }.Select(agent => agent.ToString()).Order(), said.Select(written => written.Text("AgentId")).Order());
    }

    [Fact]
    public async Task A_test_cycle_moves_no_agent_whatever_the_gateway_says_of_its_key()
    {
        // An agent of the database that runs, with stand-ins for the models. The gateway says of
        // the call of its Market Analyst that it began the third pause in a row of the calls
        // with the key, which is one more than the file of the tests asks for.
        using var application = new CyclesApplication(Close, storedAgents: true, buildsPackages: true);
        var (ada, agent) = await RunningAsync(application, Ada, team: true);
        var refused = StandInModels.Failed with { KeyTrips = 3 };

        application.Models.Says(Role.MarketAnalyst, refused, refused);

        var tested = await application.Application.TestCycles
            .RunAsync(ada, agent, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The cycle ran, and its call was refused. A test cycle places no order and changes
        // nothing of its agent (FR-CYC-12): the agent runs on, and nothing is said.
        Assert.Equal(TestCycleOutcome.Recorded, tested.Outcome);
        Assert.Single(application.Models.CallsOf(Role.MarketAnalyst));
        Assert.Equal("Running", StateOf(application, agent).State);
        Assert.Empty(await application.Application.ToldAsync(ada));

        // The cycle of a close is one of the agent's own: the same answer moves the agent.
        await application.RunAsync(Close).Within();

        Assert.Equal(2, application.Models.CallsOf(Role.MarketAnalyst).Count);
        Assert.Equal(("AttentionRequired", "CallsFailRepeatedly"), (StateOf(application, agent).State, StateOf(application, agent).StateCause));
        Assert.Single(await application.Application.ToldAsync(ada));
    }

    [Theory]

    // One pause is none in a row, and a call that began no pause says nothing of the key.
    [InlineData(1)]
    [InlineData(null)]
    public async Task Fewer_pauses_in_a_row_than_the_file_says_move_no_agent(int? keyTrips)
    {
        using var application = new CyclesApplication(Close, storedAgents: true, buildsPackages: true);
        var (ada, agent) = await RunningAsync(application, Ada, team: true);

        application.Models.Says(Role.MarketAnalyst, StandInModels.Failed with { KeyTrips = keyTrips });

        await application.RunAsync(Close).Within();

        Assert.Equal(CycleOutcome.NoDecision, application.Records.Of(agent).Cycle.Outcome);
        Assert.Equal("Running", StateOf(application, agent).State);
        Assert.Empty(await application.Application.ToldAsync(ada));
    }

    [Fact]
    public async Task A_key_that_is_refused_for_the_Sentiment_Analyst_moves_the_agent_whose_cycle_goes_on_without_it()
    {
        using var application = new CyclesApplication(Close, storedAgents: true, buildsPackages: true);
        var (ada, agent) = await RunningAsync(application, Ada, team: true);

        application.Models.Says(Role.SentimentAnalyst, StandInModels.Failed with { KeyTrips = 2 });

        await application.RunAsync(Close).Within();

        // The cycle went on with sentiment marked unavailable, as it does (FR-TEAM-9), and the
        // agent waits for its user from then on: the role is one that its user sees to.
        Assert.Equal(CycleOutcome.Approved, application.Records.Of(agent).Cycle.Outcome);
        Assert.Equal(("AttentionRequired", "CallsFailRepeatedly"), (StateOf(application, agent).State, StateOf(application, agent).StateCause));
        Assert.Contains("its Sentiment Analyst keeps refusing", Assert.Single(await application.Application.ToldAsync(ada)).Content);
    }

    [Fact]
    public async Task An_agent_that_no_longer_runs_is_not_moved_and_its_user_is_not_told()
    {
        // The call that begins the second pause is held until the user of the agent has paused it.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holds = false;
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            var provider = ModelCalls.ProviderOf(request);

            if (provider != "gpt")
            {
                return await AnsweredAsync(request, cancellation);
            }

            if (holds)
            {
                reached.TrySetResult();
                await held.Task.WaitAsync(cancellation);
            }

            return StandInSystem.Json(HttpStatusCode.TooManyRequests, "{}");
        });

        using var application = new CyclesApplication(Close, providers: providers, settings: OneRefusalPauses);
        var (ada, agent) = await RunningAsync(application, Ada);
        await application.SchedulerStartedAsync();

        await application.RunAsync(Close).Within();

        holds = true;
        application.Clock.Set(Close + Half);
        var run = application.RunAsync(Close + Half);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await PauseAsync(application, agent);

        held.SetResult();
        await run.Within();

        // It stays as its user left it: an agent that was paused is not made to look as if it
        // waited for something, and nothing is said of it.
        Assert.Equal(("Paused", "UserPaused"), (StateOf(application, agent).State, StateOf(application, agent).StateCause));
        Assert.Empty(await application.Application.ToldAsync(ada));
    }

    [Fact]
    public async Task A_cycle_ends_as_it_would_have_when_what_follows_from_a_refusal_fails()
    {
        // What moves an agent fails, as it does when the database cannot be written to.
        using var application = new CyclesApplication(
            Close,
            storedAgents: true,
            buildsPackages: true,
            further: services =>
            {
                var real = services.Last(service => service.ServiceType == typeof(IAgentTrading)).ImplementationType!;

                services.AddSingleton<IAgentTrading>(provider =>
                    new TradingThatMovesNoAgent((IAgentTrading)ActivatorUtilities.CreateInstance(provider, real)));
            });
        var (ada, agent) = await RunningAsync(application, Ada, team: true);

        application.Models.Says(Role.MarketAnalyst, StandInModels.Failed with { KeyTrips = 2 });

        await application.RunAsync(Close).Within();

        // The cycle ended with no decision, as it would have, and was recorded: it did not fail.
        Assert.Equal(CycleOutcome.NoDecision, application.Records.Of(agent).Cycle.Outcome);
        Assert.Equal(0, application.SummaryOf(Close)["Failed"].GetInt32());
        Assert.Empty(application.Orders.Placed);

        // It is an error in the log, once, with whose agent it was.
        var said = Assert.Single(application.Application.Events(), written => written.Area == TroubleArea);

        Assert.Equal((NotDone, "Error"), (said.Template, said.Level));
        Assert.Equal(("claude", ada.ToString(), agent.ToString()), (said.Text("Provider"), said.Text("UserId"), said.Text("AgentId")));
        Assert.Equal("Running", StateOf(application, agent).State);
        Assert.Empty(await application.Application.ToldAsync(ada));
    }

    /// <summary>
    /// Providers that answer each role as its schema asks, but where the test says a status: a
    /// call that it gives one is answered with that status, and with nothing else.
    /// </summary>
    private static StandInSystem Providers(Func<HttpRequestMessage, HttpStatusCode?> refuses) =>
        new(async (request, cancellation) => refuses(request) is { } status
            ? StandInSystem.Json(status, "{}")
            : await AnsweredAsync(request, cancellation));

    private static async Task<HttpResponseMessage> AnsweredAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var provider = ModelCalls.ProviderOf(request);
        var role = Team.Single(given => given.Value == provider).Key;
        var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);

        return StandInSystem.Json(
            HttpStatusCode.OK,
            offered ? ModelCalls.Searched(provider, TeamAnswers.Of(role)) : ModelCalls.Finished(provider, TeamAnswers.Of(role)));
    }

    /// <summary>Whether a call was made with the stored key of a user for a provider.</summary>
    private static bool HasKeyOf(HttpRequestMessage request, Guid userId, string provider) =>
        ModelCalls.ProviderOf(request) == provider
        && request.Headers.Any(header => header.Value.Any(value => value.Contains(Keys.Of(userId.ToString("N"), provider), StringComparison.Ordinal)));

    private static bool HasKeyOf(ReceivedCall call, Guid userId, string provider) =>
        call.Address.Host == $"{provider}.example"
        && call.Headers.Values.Any(header => header.Contains(Keys.Of(userId.ToString("N"), provider), StringComparison.Ordinal));

    private static StoredAgent StateOf(CyclesApplication application, Guid agentId) =>
        StoredAgent.All(application.Application.Database).Single(agent => agent.Id == agentId);

    /// <summary>Leaves an agent as a user who pauses it does: paused, by them.</summary>
    private static async Task PauseAsync(CyclesApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<Agent>()
            .Where(agent => agent.Id == agentId)
            .ExecuteUpdateAsync(
                agent => agent
                    .SetProperty(paused => paused.State, AgentState.Paused)
                    .SetProperty(paused => paused.StateCause, AgentStateCause.UserPaused),
                TestContext.Current.CancellationToken);
    }

    /// <summary>The recorded cycles of an agent, in the order of their closes.</summary>
    private static async Task<IReadOnlyList<DecisionCycle>> CyclesOfAsync(CyclesApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var cycles = await context.Set<DecisionCycle>()
            .AsNoTracking()
            .Where(cycle => cycle.AgentId == agentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return [.. cycles.OrderBy(cycle => cycle.ClosedAt)];
    }

    /// <summary>The recorded calls of a role of an agent, in the order they were made in.</summary>
    private static async Task<IReadOnlyList<RoleOutput>> CallsOfAsync(CyclesApplication application, Guid agentId, Role role)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var calls = await (
            from output in context.Set<RoleOutput>().AsNoTracking()
            join cycle in context.Set<DecisionCycle>().AsNoTracking() on output.CycleId equals cycle.Id
            where cycle.AgentId == agentId
            select output)
            .ToListAsync(TestContext.Current.CancellationToken);

        return [.. calls.Where(call => call.Role == role).OrderBy(call => call.CalledAt).ThenBy(call => call.Attempt)];
    }

    /// <summary>
    /// A user with an agent that runs. Its team is called through the gateway with the stored
    /// keys of the user, each role at the provider of <see cref="Team"/>, unless the test asks
    /// for the team that an agent is given with stand-ins for the models.
    /// </summary>
    private static async Task<(Guid UserId, Guid AgentId)> RunningAsync(
        CyclesApplication application, string email, string symbol = "BTCUSDT", bool team = false)
    {
        var user = await application.Application.RegisterVerifiedAsync(email);
        var agent = await application.Application.GiveAgentAsync(
            user.Id,
            Close.AddHours(-1),
            state: AgentState.Running,
            symbol: symbol,
            cause: AgentStateCause.UserStarted,
            team: team,
            timeframe: Timeframe.FifteenMinutes);

        if (!team)
        {
            foreach (var (role, provider) in Team)
            {
                await application.Application.GiveRoleAsync(user.Id, agent, role, Close.AddHours(-1), provider, $"{provider}-test-model");
            }
        }

        return (user.Id, agent);
    }

    /// <summary>Stands for Agents where an agent cannot be moved: everything else is asked of Agents itself.</summary>
    private sealed class TradingThatMovesNoAgent(IAgentTrading agents) : IAgentTrading
    {
        public Task<AgentState?> StateUnderLockAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken) =>
            agents.StateUnderLockAsync(context, userId, agentId, cancellationToken);

        public Task<bool> RequireAttentionAsync(Guid userId, Guid agentId, AgentStateCause cause, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The agent could not be moved.");
    }
}
