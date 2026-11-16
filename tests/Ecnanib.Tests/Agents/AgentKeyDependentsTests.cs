using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;
using KeyPages = Ecnanib.WebUi.Pages.Account.KeyPages;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// An agent that depends on a missing or invalid key moves to Attention required (FR-KEY-10).
/// An agent runs with the Binance credential of its user for its mode, so the agents that run
/// in an environment move when the credential of that environment is deleted or refused. The
/// change records its cause and its time like any other (FR-AGT-10). An agent that does not run
/// stays as its user left it, and is asked about its key when it is resumed.
/// </summary>
public class AgentKeyDependentsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_running_agent_requires_attention_when_the_binance_key_of_its_mode_is_deleted()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);

        // Ada has two agents that run, one that she paused, a draft, and one that she stopped.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Runs on Spot"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Runs on Futures", market: "Futures"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Paused", symbol: "SOLUSDT"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Draft"));
        var onSpot = StoredAgent.Named(application.Database, "Runs on Spot").Id;
        var onFutures = StoredAgent.Named(application.Database, "Runs on Futures").Id;
        var paused = StoredAgent.Named(application.Database, "Paused").Id;
        var draft = StoredAgent.Named(application.Database, "Draft").Id;
        var stopped = await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: AgentState.Stopped);

        await client.StartAgentAsync(onSpot);
        await client.StartAgentAsync(onFutures);
        await client.StartAgentAsync(paused);
        await client.PauseAgentAsync(paused);

        // Grace has a key of her own, and an agent that runs with it.
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(grace.Id, Morning);
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's"));
        var graces = StoredAgent.Named(application.Database, "Grace's").Id;
        await gracesBrowser.StartAgentAsync(graces);

        // Ada deletes her Binance key for Demo.
        clock.Set(Morning.AddMinutes(30));
        await client.DeleteExchangeKeyAsync("demo");

        Assert.DoesNotContain(StoredExchangeCredential.All(application.Database), key => key.UserId == user.Id);

        // The agents that ran with it require attention, since then and for that reason.
        Assert.All(new[] { onSpot, onFutures }, agent => Assert.Equal(
            ("AttentionRequired", "ExchangeKeyDeleted", Morning.AddMinutes(30)), AgentControlTests.StateOf(application, agent)));
        Assert.Equal(
            ["Draft: Created", "Running: UserStarted", "AttentionRequired: ExchangeKeyDeleted"],
            StoredAgentStateChange.Told(application.Database, onSpot));

        // The others are as she left them: none of them ran.
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);
        Assert.Equal("Draft", AgentControlTests.StateOf(application, draft).State);
        Assert.Equal("Stopped", AgentControlTests.StateOf(application, stopped).State);
        Assert.Equal(["Draft: Created", "Running: UserStarted", "Paused: UserPaused"], StoredAgentStateChange.Told(application.Database, paused));

        // And Grace's agent runs: her key is there.
        Assert.Equal("Running", AgentControlTests.StateOf(application, graces).State);

        // The page of the agent says the state, what it means, and what to do about it.
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(onSpot)));

        Assert.Contains("<strong>Attention required</strong>, since 12 November 2026, 09:30 UTC.", page);
        Assert.Contains(AgentPages.Explain(AgentState.AttentionRequired), page);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.ExchangeKeyDeleted)}</p>", page);
        Assert.Contains("<td>Attention required</td>", await client.PageAsync(AgentBrowser.AgentsPage));

        // The service did it, and no user: the audit trail has the deletion of the key, and
        // nothing about the agents.
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database).Where(recorded => recorded.OccurredAt == Morning.AddMinutes(30)),
            recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "KeyDeleted");

        // Without a key the agent is not resumed, and neither is the one she paused.
        foreach (var agent in new[] { onSpot, paused })
        {
            var (_, refused) = await client.ResumeAgentAsync(agent);

            Assert.Contains(KeyPages.Says(StartBlock.NoKey, TradingMode.Demo), refused);
        }

        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, onSpot).State);

        // She adds a key, and resumes the agent. The other one waits for her as it was.
        clock.Set(Morning.AddMinutes(40));
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);

        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, onFutures).State);

        await client.ResumeAgentAsync(onSpot);

        Assert.Equal(("Running", "UserResumed", Morning.AddMinutes(40)), AgentControlTests.StateOf(application, onSpot));
        Assert.Equal(
            ["Draft: Created", "Running: UserStarted", "AttentionRequired: ExchangeKeyDeleted", "Running: UserResumed"],
            StoredAgentStateChange.Told(application.Database, onSpot));
    }

    [Fact]
    public async Task A_running_agent_requires_attention_when_binance_refuses_the_key_of_its_mode()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Runs"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Paused", symbol: "SOLUSDT"));
        var runs = StoredAgent.Named(application.Database, "Runs").Id;
        var paused = StoredAgent.Named(application.Database, "Paused").Id;
        await client.StartAgentAsync(runs);
        await client.StartAgentAsync(paused);
        await client.PauseAgentAsync(paused);

        // The key is checked again a day later, and Binance refuses it.
        application.Providers.BinanceAnswer = (_, _) => new ExchangeKeyCheck(KeyVerdict.Refused, SpotTrading: false, FuturesTrading: false);
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal("Invalid", StoredExchangeCredential.Of(application.Database, user.Id, "Demo").Status);

        // The agent that ran with it requires attention, with the reason, as the check found it.
        Assert.Equal(("AttentionRequired", "ExchangeKeyInvalid", Morning.AddDays(1)), AgentControlTests.StateOf(application, runs));
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);

        // Her session of the day before has ended, so she signs in again. The page of the
        // agent says why it waits for her.
        await client.SignInAsync(Ada);
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(runs)));

        Assert.Contains("<strong>Attention required</strong>, since 13 November 2026, 09:00 UTC.", page);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.ExchangeKeyInvalid)}</p>", page);

        // It is not resumed while the key is invalid, and the page says that the key is.
        var (_, refused) = await client.ResumeAgentAsync(runs);

        Assert.Contains(KeyPages.Says(StartBlock.NotValid, TradingMode.Demo), AgentBrowser.Read(refused));
        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, runs).State);

        // The key is refused again the next day. The agent was told once, when the key became
        // invalid: nothing more is recorded for it.
        clock.Set(Morning.AddDays(2));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(
            ["Draft: Created", "Running: UserStarted", "AttentionRequired: ExchangeKeyInvalid"],
            StoredAgentStateChange.Told(application.Database, runs));

        // Binance takes the key again, and the user resumes the agent.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo;
        clock.Set(Morning.AddDays(2).AddMinutes(10));
        await client.SignInAsync(Ada);
        await client.CheckExchangeKeyAsync("demo");
        await client.ResumeAgentAsync(runs);

        Assert.Equal(("Running", "UserResumed", Morning.AddDays(2).AddMinutes(10)), AgentControlTests.StateOf(application, runs));
    }

    [Fact]
    public async Task The_key_of_one_environment_moves_no_agent_of_the_other_and_a_key_of_an_AI_provider_moves_none()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // An agent runs in Demo, and another in Live, which the test stores as it runs there,
        // without the steps that switch an agent and start it.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "In Demo"));
        var inDemo = StoredAgent.Named(application.Database, "In Demo").Id;
        await client.StartAgentAsync(inDemo);
        var inLive = await application.GiveAgentAsync(user.Id, Morning, name: "In Live", state: AgentState.Running, mode: TradingMode.Live);

        // The key of an AI provider goes. No agent uses a model yet, so none depends on it.
        clock.Set(Morning.AddMinutes(10));
        await client.DeleteAiKeyAsync("claude");

        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Equal(["Running", "Running"], new[] { inDemo, inLive }.Select(agent => AgentControlTests.StateOf(application, agent).State));

        // The Live key goes: the agent in Live requires attention, and the one in Demo runs.
        clock.Set(Morning.AddMinutes(20));
        await client.DeleteExchangeKeyAsync("live");

        Assert.Equal(("AttentionRequired", "ExchangeKeyDeleted", Morning.AddMinutes(20)), AgentControlTests.StateOf(application, inLive));
        Assert.Equal("Running", AgentControlTests.StateOf(application, inDemo).State);

        // And the other way round.
        clock.Set(Morning.AddMinutes(30));
        await client.DeleteExchangeKeyAsync("demo");

        Assert.Equal(("AttentionRequired", "ExchangeKeyDeleted", Morning.AddMinutes(30)), AgentControlTests.StateOf(application, inDemo));
        Assert.Equal(Morning.AddMinutes(20), AgentControlTests.StateOf(application, inLive).ChangedAt);
    }

    [Fact]
    public void Once_an_agent_uses_a_model_it_requires_attention_when_the_key_of_the_models_provider_goes()
    {
        using var model = ModelOnly.Context();

        // An agent is given no model yet, so no agent depends on a key of an AI provider, and
        // Agents moves none when such a key is deleted or refused. As soon as a role of an
        // agent's team has a model, the agents whose team uses the provider depend on its key.
        var choices = AgentsRules.ModelChoices(model.Model);

        Assert.True(
            choices.Count == 0,
            $"An agent uses a model now ({string.Join(", ", choices)}), and Agents moves no agent when the key of the model's " +
            "provider is deleted or refused. An agent that depends on a missing or invalid key moves to Attention required " +
            "(FR-KEY-10). Move the agents whose team uses the provider in AgentKeyDependents.AiKeyDeletedAsync and " +
            "AiKeyInvalidAsync, with causes of their own, show it in a test here, and take this test away. See docs/agents.md.");
    }
}
