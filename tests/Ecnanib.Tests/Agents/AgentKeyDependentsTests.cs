using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;
using KeyPages = Ecnanib.WebUi.Pages.Account.KeyPages;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// An agent that depends on a missing or invalid key moves to Attention required (FR-KEY-10).
/// An agent runs with the Binance credential of its user for its mode, so the agents that run
/// in an environment move when the credential of that environment is deleted or refused. And it
/// runs with the models of its roles, each with the key of its user for the provider of the
/// model, so the agents that run with a model of a provider move when the key of that provider
/// is deleted or refused. The change records its cause and its time like any other (FR-AGT-10).
/// An agent that does not run stays as its user left it, and is asked about its keys when it is
/// resumed.
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
        await application.GiveTeamsAsync(user.Id, Morning);
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
        await application.GiveTeamsAsync(grace.Id, Morning);
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

        // Ada is told that each of the two waits for her, and why (FR-NTF-1): once for each,
        // and nothing about the agents that did not run. Grace is told nothing.
        const string waits =
            "The agent in BTCUSDT (Demo) waits for you: its Binance key for Demo was deleted. " + "Store a key that works on the page of your keys, and resume the agent.";

        Assert.Equal(
            new (NotificationType, Guid?, string)[]
            {
                (NotificationType.AttentionRequired, onSpot, waits),
                (NotificationType.AttentionRequired, onFutures, waits),
            }.OrderBy(told => told.Item2),
            await application.ToldAsync(user.Id));
        Assert.Empty(await application.ToldAsync(grace.Id));

        // The page of the agent says the state, what it means, and what to do about it.
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(onSpot)));

        Assert.Contains("<strong>Attention required</strong>, since 12 November 2026, 09:30 UTC.", page);
        Assert.Contains($"<strong>Agent needs attention</strong>, 12 November 2026, 09:30 UTC. {waits}", page);
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
        await application.GiveTeamsAsync(user.Id, Morning);
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

        // Its user is told that the key is invalid, and that the agent which ran with it waits
        // for her (FR-NTF-1): once each, and nothing about the agent that did not run.
        (NotificationType, Guid?, string)[] told =
        [
            (
                NotificationType.KeyInvalid,
                null,
                "Your Binance key for Demo was refused by Binance, and is invalid. The agents that ran with it wait for you. " +
                "Store a key that works on the page of your keys, and resume them."),
            (
                NotificationType.AttentionRequired,
                runs,
                "The agent in BTCUSDT (Demo) waits for you: its Binance key for Demo was refused, and is invalid. " + "Store a key that works on the page of your keys, and resume the agent."),
        ];

        Assert.Equal(told, await application.ToldAsync(user.Id));

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
        Assert.Equal(told, await application.ToldAsync(user.Id));

        // Binance takes the key again, and the user resumes the agent.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo;
        clock.Set(Morning.AddDays(2).AddMinutes(10));
        await client.SignInAsync(Ada);
        await client.CheckExchangeKeyAsync("demo");
        await client.ResumeAgentAsync(runs);

        Assert.Equal(("Running", "UserResumed", Morning.AddDays(2).AddMinutes(10)), AgentControlTests.StateOf(application, runs));
    }

    [Fact]
    public async Task The_key_of_one_environment_moves_no_agent_of_the_other()
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
        await application.GiveTeamsAsync(user.Id, Morning);
        var inDemo = StoredAgent.Named(application.Database, "In Demo").Id;
        await client.StartAgentAsync(inDemo);
        var inLive = await application.GiveAgentAsync(user.Id, Morning, name: "In Live", state: AgentState.Running, mode: TradingMode.Live);

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
    public async Task A_running_agent_requires_attention_when_the_key_of_a_provider_of_its_team_is_deleted()
    {
        // The file of the tests allows the models of Claude. This one allows those of GPT too.
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(
            new StandInMarketData(), clock, settings: file => file.WithJson("AiProviders:GPT:Allow", "[\"gpt-*\"]"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // Ada has an agent whose whole team is of Claude, one with a single role of Claude, and
        // one whose whole team is of GPT. All three run, and the second one is in Live: a key of
        // an AI provider is of no environment. She paused a fourth, and a fifth is a draft, both
        // with teams of Claude.
        var onClaude = await application.GiveAgentAsync(user.Id, Morning, name: "On Claude", state: AgentState.Running);
        var mixed = await application.GiveAgentAsync(
            user.Id, Morning, name: "Mixed", state: AgentState.Running, mode: TradingMode.Live, team: false);
        await application.GiveTeamAsync(user.Id, mixed, Morning, provider: "gpt", model: "gpt-test-model");
        await application.GiveRoleAsync(user.Id, mixed, Role.RiskManager, Morning);
        var onGpt = await application.GiveAgentAsync(
            user.Id, Morning, name: "On GPT", state: AgentState.Running, symbol: "SOLUSDT", team: false);
        await application.GiveTeamAsync(user.Id, onGpt, Morning, provider: "gpt", model: "gpt-test-model");
        var paused = await application.GiveAgentAsync(
            user.Id, Morning, name: "Paused", state: AgentState.Paused, market: Market.Futures);
        var draft = await application.GiveAgentAsync(user.Id, Morning, name: "Draft");

        // Grace has a key of her own for Claude, and an agent that runs with it.
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(grace.Id, Morning);
        var graces = await application.GiveAgentAsync(grace.Id, Morning, name: "Grace's", state: AgentState.Running);

        // Ada deletes her key for Claude.
        clock.Set(Morning.AddMinutes(30));
        await client.DeleteAiKeyAsync("claude");

        Assert.DoesNotContain(StoredAiCredential.All(application.Database), key => key.UserId == user.Id && key.Provider == "claude");

        // The agents that ran with a model of Claude require attention, since then and for that
        // reason, in Demo and in Live. Each change says the mode that its agent is in.
        Assert.All(new[] { onClaude, mixed }, agent => Assert.Equal(
            ("AttentionRequired", "AiKeyDeleted", Morning.AddMinutes(30)), AgentControlTests.StateOf(application, agent)));
        Assert.Equal(["Running: Created", "AttentionRequired: AiKeyDeleted"], StoredAgentStateChange.Told(application.Database, onClaude));
        Assert.Equal(["Live", "Live"], StoredAgentStateChange.Of(application.Database, mixed).Select(change => change.Mode));

        // The one that uses no model of Claude runs, and the others are as she left them:
        // neither of them ran.
        Assert.Equal("Running", AgentControlTests.StateOf(application, onGpt).State);
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);
        Assert.Equal("Draft", AgentControlTests.StateOf(application, draft).State);

        // And Grace's agent runs: her key is there.
        Assert.Equal("Running", AgentControlTests.StateOf(application, graces).State);

        // Ada is told that each of the two waits for her, with the roles that the key was for
        // and the mode of each (FR-NTF-1). Grace is told nothing.
        Assert.Equal(
            new (NotificationType, Guid?, string)[]
            {
                (
                    NotificationType.AttentionRequired,
                    onClaude,
                    "The agent in BTCUSDT (Demo) waits for you: your key for the AI provider of its Market Analyst, Sentiment Analyst, " +
                    "Strategist and Risk Manager was deleted. " + "Store a key that works on the page of your keys, and resume the agent."),
                (
                    NotificationType.AttentionRequired,
                    mixed,
                    "The agent in BTCUSDT (Live) waits for you: your key for the AI provider of its Risk Manager was deleted. " +
                    "Store a key that works on the page of your keys, and resume the agent."),
            }.OrderBy(told => told.Item2),
            await application.ToldAsync(user.Id));
        Assert.Empty(await application.ToldAsync(grace.Id));

        // No role was given another model: nothing is put in the place of what the user chose.
        Assert.Equal(
            ["MarketAnalyst: claude-test-model", "SentimentAnalyst: claude-test-model", "Strategist: claude-test-model", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, onClaude));

        // The page of the agent says the state, what it means, and what to do about it.
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(onClaude)));

        Assert.Contains("<strong>Attention required</strong>, since 12 November 2026, 09:30 UTC.", page);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.AiKeyDeleted)}</p>", page);

        // The service did it, and no user: the audit trail has the deletion of the key, and
        // nothing about the agents.
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database).Where(recorded => recorded.OccurredAt == Morning.AddMinutes(30)),
            recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "KeyDeleted");

        // Without the key the agent is not resumed, and neither is the one she paused. The page
        // names each role that the key is missing for.
        foreach (var agent in new[] { onClaude, paused })
        {
            var (_, refused) = await client.ResumeAgentAsync(agent);
            var read = AgentBrowser.Read(refused);

            Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.RolesBlock(AgentControl.Resume)}</p>", read);
            Assert.All(
                Enum.GetValues<Role>(),
                role => Assert.Contains(
                    $"<li>{AgentPages.Says(new RoleBlock(role, RoleProblem.NoKey, "Claude", "claude-test-model"))}</li>", read));
        }

        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, onClaude).State);
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);

        // She stores a key again, which Claude lists the model to, and resumes the agent. The
        // other one waits for her as it was.
        clock.Set(Morning.AddMinutes(40));
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);

        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, mixed).State);

        await client.ResumeAgentAsync(onClaude);

        Assert.Equal(("Running", "UserResumed", Morning.AddMinutes(40)), AgentControlTests.StateOf(application, onClaude));
        Assert.Equal(
            ["Running: Created", "AttentionRequired: AiKeyDeleted", "Running: UserResumed"],
            StoredAgentStateChange.Told(application.Database, onClaude));
    }

    [Fact]
    public async Task A_running_agent_requires_attention_when_a_provider_refuses_the_key_that_its_team_uses()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        var runs = await application.GiveAgentAsync(user.Id, Morning, name: "Runs", state: AgentState.Running);
        var paused = await application.GiveAgentAsync(user.Id, Morning, name: "Paused", state: AgentState.Paused, symbol: "SOLUSDT");

        // The keys are checked again a day later, and Claude refuses hers. Binance takes its own.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal("Invalid", StoredAiCredential.Of(application.Database, user.Id, "claude").Status);
        Assert.Equal("Valid", StoredExchangeCredential.Of(application.Database, user.Id, "Demo").Status);

        // The agent that ran with a model of Claude requires attention, with the reason, as the
        // check found it. The one that did not run is as she left it.
        Assert.Equal(("AttentionRequired", "AiKeyInvalid", Morning.AddDays(1)), AgentControlTests.StateOf(application, runs));
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);

        // Its user is told that the key is invalid, by the name of its provider, and that the
        // agent which ran with it waits for her (FR-NTF-1): once each.
        (NotificationType, Guid?, string)[] told =
        [
            (
                NotificationType.KeyInvalid,
                null,
                "Your key for Claude was refused by the provider, and is invalid. The agents that ran with it wait for you. " +
                "Store a key that works on the page of your keys, and resume them."),
            (
                NotificationType.AttentionRequired,
                runs,
                "The agent in BTCUSDT (Demo) waits for you: your key for the AI provider of its Market Analyst, Sentiment Analyst, " +
                "Strategist and Risk Manager was refused, and is invalid. " + "Store a key that works on the page of your keys, and resume the agent."),
        ];

        Assert.Equal(told, await application.ToldAsync(user.Id));

        // The page of the agent says why it waits for her.
        await client.SignInAsync(Ada);
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(runs)));

        Assert.Contains("<strong>Attention required</strong>, since 13 November 2026, 09:00 UTC.", page);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.AiKeyInvalid)}</p>", page);

        // It is not resumed while the key is invalid, and the page says that the key is.
        var (_, refused) = await client.ResumeAgentAsync(runs);

        Assert.Contains(
            $"<li>{AgentPages.Says(new RoleBlock(Role.Strategist, RoleProblem.KeyNotValid, "Claude", "claude-test-model"))}</li>",
            AgentBrowser.Read(refused));
        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, runs).State);

        // The key is refused again the next day. The agent was told once, when the key became
        // invalid: nothing more is recorded for it.
        clock.Set(Morning.AddDays(2));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(["Running: Created", "AttentionRequired: AiKeyInvalid"], StoredAgentStateChange.Told(application.Database, runs));
        Assert.Equal(told, await application.ToldAsync(user.Id));

        // Claude takes the key again, and lists the model to it. The user resumes the agent.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Accepted;
        clock.Set(Morning.AddDays(2).AddMinutes(10));
        await client.SignInAsync(Ada);
        await client.CheckAiKeyAsync("claude");
        await client.ResumeAgentAsync(runs);

        Assert.Equal(("Running", "UserResumed", Morning.AddDays(2).AddMinutes(10)), AgentControlTests.StateOf(application, runs));
    }
}
