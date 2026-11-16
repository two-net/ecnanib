using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// What starts a cycle asks Agents which agents run, of every user, when a candle closes, and
/// whether an agent still runs when the turn of its cycle comes: only a running agent runs
/// cycles (SRS 3.3, FR-CYC-1). See "What other components ask of Agents" in docs/agents.md.
/// </summary>
public class RunningAgentsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 16, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_running_agents_of_every_user_are_listed_with_their_roles()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Agents of two users that run on the hour, one of them with a team of two providers.
        var first = await GiveAsync(application, ada.Id, "Hour", AgentState.Running, Timeframe.OneHour);
        var second = await GiveAsync(application, grace.Id, "Hour", AgentState.Running, Timeframe.OneHour);
        await application.GiveRoleAsync(grace.Id, second, Role.RiskManager, Morning, provider: "gpt", model: "gpt-test-model");

        // Agents that are not asked for: one on another timeframe, and one that does not run.
        var quarter = await GiveAsync(application, ada.Id, "Quarter", AgentState.Running, Timeframe.FifteenMinutes, symbol: "SOLUSDT");
        await GiveAsync(application, grace.Id, "Paused", AgentState.Paused, Timeframe.OneHour, symbol: "SOLUSDT");

        var running = await application.RunningAgents.OfTimeframesAsync([Timeframe.OneHour], TestContext.Current.CancellationToken);

        // Of every user, in the order of their ids: background work has no user who is signed
        // in, and each agent says whose it is.
        Assert.Equal([first, second], running.Select(agent => agent.Agent.Id));
        Assert.Equal([ada.Id, grace.Id], running.Select(agent => agent.Agent.UserId));
        Assert.All(running, agent => Assert.Equal((AgentState.Running, Timeframe.OneHour), (agent.Agent.State, agent.Agent.Timeframe)));

        // Each with the roles of its own team, in the order of the roles, and their providers.
        Assert.All(running, agent => Assert.Equal(Enum.GetValues<Role>(), agent.Roles.Select(role => role.Role)));
        Assert.Equal(["claude", "claude", "claude", "claude"], running[0].Roles.Select(role => role.Provider.Value));
        Assert.Equal(["claude", "claude", "claude", "gpt"], running[1].Roles.Select(role => role.Provider.Value));
        Assert.Equal("gpt-test-model", running[1].Roles[3].Identifier);

        // The agents of several timeframes at once, as at a close of each of them.
        var both = await application.RunningAgents.OfTimeframesAsync(
            [Timeframe.FifteenMinutes, Timeframe.OneHour], TestContext.Current.CancellationToken);
        Assert.Equal(new[] { first, second, quarter }.Order(), both.Select(agent => agent.Agent.Id).Order());

        // No timeframe, and a timeframe that no agent runs on, give no agent.
        Assert.Empty(await application.RunningAgents.OfTimeframesAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(await application.RunningAgents.OfTimeframesAsync([Timeframe.OneDay], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_an_agent_in_a_state_that_runs_cycles_is_listed_and_found()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);

        // An agent in each state. A user has one active agent for a market, a symbol, and an
        // environment, so each has its own.
        (Market Market, string Symbol, TradingMode Mode)[] places =
        [
            (Market.Spot, "BTCUSDT", TradingMode.Demo),
            (Market.Spot, "SOLUSDT", TradingMode.Demo),
            (Market.Futures, "BTCUSDT", TradingMode.Demo),
            (Market.Spot, "BTCUSDT", TradingMode.Live),
            (Market.Spot, "SOLUSDT", TradingMode.Live),
            (Market.Futures, "BTCUSDT", TradingMode.Live),
        ];

        var agents = new Dictionary<AgentState, Guid>();
        foreach (var (state, place) in Enum.GetValues<AgentState>().Zip(places))
        {
            agents[state] = await application.GiveAgentAsync(
                ada.Id, Morning, state.ToString(), state, place.Market, place.Symbol, place.Mode, timeframe: Timeframe.OneHour);
        }

        var listed = await application.RunningAgents.OfTimeframesAsync([Timeframe.OneHour], TestContext.Current.CancellationToken);

        // What the table of the states says runs cycles, and nothing else (SRS 3.3).
        Guid[] running = [.. agents.Where(agent => AgentStates.RunsCycles(agent.Key)).Select(agent => agent.Value)];
        Assert.Equal([agents[AgentState.Running]], running);
        Assert.Equal(running, listed.Select(agent => agent.Agent.Id));

        foreach (var (state, agent) in agents)
        {
            var found = await application.RunningAgents.FindAsync(agent, TestContext.Current.CancellationToken);

            Assert.Equal(AgentStates.RunsCycles(state), found is not null);
        }

        // An agent that does not exist is not found either.
        Assert.Null(await application.RunningAgents.FindAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_is_found_as_it_is_stored_when_it_is_asked_for()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await GiveAsync(application, ada.Id, "Hour", AgentState.Running, Timeframe.OneHour);

        var found = await application.RunningAgents.FindAsync(agent, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal((agent, ada.Id, "BTCUSDT"), (found.Agent.Id, found.Agent.UserId, found.Agent.Symbol));
        Assert.Equal(4, found.Roles.Count);

        // Its user gives a role another model, and pauses it: the next ask says what is stored
        // then. A change applies from the agent's next cycle (FR-AGT-8).
        await application.GiveRoleAsync(ada.Id, agent, Role.Strategist, Morning, provider: "gemini", model: "gemini-test-model");

        found = await application.RunningAgents.FindAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal("gemini", found!.Roles.Single(role => role.Role == Role.Strategist).Provider.Value);

        application.Database.Execute($"UPDATE agents SET state = 'Paused' WHERE id = '{agent}'");

        Assert.Null(await application.RunningAgents.FindAsync(agent, TestContext.Current.CancellationToken));
    }

    private static Task<Guid> GiveAsync(
        AccountsApplication application, Guid userId, string name, AgentState state, Timeframe timeframe, string symbol = "BTCUSDT") =>
        application.GiveAgentAsync(userId, Morning, name, state, symbol: symbol, timeframe: timeframe);
}
