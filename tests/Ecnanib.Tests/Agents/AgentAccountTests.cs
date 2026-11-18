using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Futures agents use isolated margin and one-way position mode. The system does not change
/// account-wide settings; if the account is in a conflicting mode or already holds a position in
/// the symbol, the agent does not start and the user is told what to change (FR-BIN-F2). See
/// "The controls" in docs/agents.md.
/// </summary>
public class AgentAccountTests
{
    private const string Ada = "ada@example.com";

    private const string RefusedTemplate =
        "User {UserId} was refused the control {AgentControl} of agent {AgentId}: their Binance account for {TradingMode} stands in its way ({AccountConflict})";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly ExchangeFuturesAccount Hedged = new(ExchangeCallOutcome.Succeeded, HedgeMode: true, MultiAssetsMargin: false, HoldsPosition: false);
    private static readonly ExchangeFuturesAccount MultiAssets = new(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: true, HoldsPosition: false);
    private static readonly ExchangeFuturesAccount Holding = new(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: true);

    [Fact]
    public async Task An_agent_of_the_Futures_market_does_not_start_while_the_account_conflicts_and_its_user_is_told_what_to_change()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", market: Market.Futures);

        (ExchangeFuturesAccount Account, AccountConflict Conflict, string Says)[] conflicts =
        [
            (
                Hedged,
                AccountConflict.HedgeMode,
                "Your Binance Futures account is in hedge mode, and an agent trades in one-way mode. The service does not change a " +
                "setting of your whole account. Switch the position mode to one-way at Binance, and start the agent."),
            (
                MultiAssets,
                AccountConflict.MultiAssetsMode,
                "Your Binance Futures account is in Multi-Assets mode, in which Binance takes no isolated margin, and an agent trades " +
                "with isolated margin. The service does not change a setting of your whole account. Switch the asset mode to " +
                "single-asset at Binance, and start the agent."),
            (
                Holding,
                AccountConflict.PositionInSymbol,
                "Your Binance Futures account for Demo holds a position in BTCUSDT that this agent did not open. An agent trades its " +
                "symbol alone. Close the position at Binance, and start the agent."),
            (
                ExchangeFuturesAccount.Without(ExchangeCallOutcome.TimedOut),
                AccountConflict.NotKnown,
                "Binance did not say how your Futures account is set, so the agent was not started. Try again in a moment."),
        ];

        foreach (var (account, _, says) in conflicts)
        {
            application.Providers.FuturesAccount = (_, _) => account;

            var (path, page) = await client.StartAgentAsync(agent);

            // The agent is the draft that it was, and the page says what stands in its way,
            // with what to change at Binance (FR-BIN-F2).
            Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
            Assert.Contains($"<p class=\"form-error\" role=\"alert\">{says}</p>", AgentBrowser.Read(page));
            Assert.Equal(("Draft", "Created", Morning), AgentControlTests.StateOf(application, agent));
        }

        // Binance was asked about the account of the agent's symbol in its environment, each time.
        Assert.All(application.Providers.FuturesAccountsAsked, asked => Assert.Equal((TradingMode.Demo, "BTCUSDT"), asked));
        Assert.Equal(conflicts.Length, application.Providers.FuturesAccountsAsked.Count);

        // What was refused is in no trail, and the log names what stood in the way.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
        Assert.Equal(
            conflicts.Select(conflict => $"Start {conflict.Conflict}"),
            application.Events()
                .Where(written => written.Template == RefusedTemplate)
                .Select(written => $"{written.Text("AgentControl")} {written.Text("AccountConflict")}"));

        // Once the account is in one-way mode, with one asset, and holds nothing in the
        // symbol, the same form starts the agent.
        application.Providers.FuturesAccount = (_, _) => StandInProviders.OneWay;
        clock.Set(Morning.AddMinutes(5));

        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.StartAgentAsync(agent)).Path);
        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task What_keeps_an_agent_from_starting_keeps_it_from_being_resumed_and_pausing_asks_nothing_of_the_account()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var running = await application.GiveAgentAsync(
            user.Id, Morning, name: "Runs", state: AgentState.Running, market: Market.Futures, cause: AgentStateCause.UserStarted);
        var waiting = await application.GiveAgentAsync(
            user.Id,
            Morning,
            name: "Waits",
            state: AgentState.AttentionRequired,
            market: Market.Futures,
            symbol: "SOLUSDT",
            cause: AgentStateCause.ExchangeSettingConflict);

        application.Providers.FuturesAccount = (_, _) => Hedged;

        // Pausing reduces what the agent does, and asks Binance nothing.
        await client.PauseAgentAsync(running);

        Assert.Equal("Paused", AgentControlTests.StateOf(application, running).State);
        Assert.Empty(application.Providers.FuturesAccountsAsked);

        // The agent that waits for its user says why, and is not resumed while the account is
        // as it was.
        var shown = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(waiting)));
        Assert.Contains(AgentPages.Says(AgentStateCause.ExchangeSettingConflict), shown);

        var (_, refused) = await client.ResumeAgentAsync(waiting);

        Assert.Contains(
            "Your Binance Futures account is in hedge mode, and an agent trades in one-way mode. The service does not change a setting " +
            "of your whole account. Switch the position mode to one-way at Binance, and resume the agent.",
            AgentBrowser.Read(refused));
        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, waiting).State);
        Assert.Equal([(TradingMode.Demo, "SOLUSDT")], application.Providers.FuturesAccountsAsked);

        // Its user switches the account to one-way mode at Binance, and resumes the agent.
        application.Providers.FuturesAccount = (_, _) => StandInProviders.OneWay;
        clock.Set(Morning.AddMinutes(5));
        await client.ResumeAgentAsync(waiting);

        Assert.Equal(("Running", "UserResumed", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, waiting));
    }

    [Fact]
    public async Task An_agent_of_the_Spot_market_starts_whatever_the_account_of_Futures_is_set_to()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "On Spot");

        application.Providers.FuturesAccount = (_, _) => Hedged;

        await client.StartAgentAsync(agent);

        // Only the Futures market has modes of an account, and positions: nothing is asked.
        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);
        Assert.Empty(application.Providers.FuturesAccountsAsked);
    }

    [Fact]
    public async Task An_agent_in_Live_is_told_the_same_on_the_page_that_asks_once_more()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", market: Market.Futures, mode: TradingMode.Live);

        application.Providers.FuturesAccount = (_, _) => Holding;

        // The one-press form of the page of the state starts nothing in Live, and asks Binance
        // nothing: it leads to the page that asks once more.
        var (asks, _) = await client.StartAgentAsync(agent);

        Assert.Equal(AgentBrowser.ConfirmControlPage(agent, "start"), asks);
        Assert.Empty(application.Providers.FuturesAccountsAsked);

        // The form of that page asks, of the real account, and says what to change there.
        var (path, page) = await client.ConfirmControlAsync(agent, "start");

        Assert.Equal(AgentBrowser.ConfirmControlPage(agent, "start"), path);
        Assert.Contains(
            "Your Binance Futures account for Live holds a position in BTCUSDT that this agent did not open. An agent trades its symbol " +
            "alone. Close the position at Binance, and start the agent.",
            AgentBrowser.Read(page));
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);
        Assert.Equal([(TradingMode.Live, "BTCUSDT")], application.Providers.FuturesAccountsAsked);
    }

    [Fact]
    public async Task What_keeps_positions_is_asked_about_the_agent_as_it_is_and_what_it_says_stands_in_the_way()
    {
        var exposure = new StandInExposure { Conflict = _ => AccountConflict.MultiAssetsMode };
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", market: Market.Futures, symbol: "SOLUSDT");

        var (_, page) = await client.StartAgentAsync(agent);

        // Agents is handed no key of an exchange: it asks whatever keeps positions and orders,
        // with the agent as it is stored, and goes by what that says.
        var asked = Assert.Single(exposure.AccountsAsked);
        Assert.Equal((agent, TradingMode.Demo, Market.Futures, "SOLUSDT"), (asked.Id, asked.Mode, asked.Market, asked.Symbol));
        Assert.Contains("Your Binance Futures account is in Multi-Assets mode", AgentBrowser.Read(page));
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);
    }
}
