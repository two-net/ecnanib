using System.Net;
using Ecnanib.Accounts;
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
/// A new agent is in Demo, and its user switches it to Live: with a valid Live Binance key, a
/// confirmation of the risk, and a fresh proof of who they are, and the switch is recorded in
/// the audit trail (FR-AGT-7, FR-ACC-4, NFR-LOG-7). The mode goes by the rule of the market: it
/// is switched only while the agent is a draft, or is stopped with nothing open (FR-AGT-8), and
/// only to an environment whose market has the symbol of the agent (FR-AGT-6).
/// </summary>
public class AgentModeSwitchTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;

    private const string SwitchedTemplate = "User {UserId} switched agent {AgentId} from {PreviousTradingMode} to {TradingMode}";

    private const string RefusedTemplate =
        "User {UserId} was refused switching agent {AgentId} to {TradingMode}: {ModeSwitchOutcome}";

    private const string AskedTemplate = "User {UserId} is asked to confirm who they are before {Action}";

    private const string LiveButton = "<button type=\"submit\">Switch the agent to Live</button>";
    private const string DemoButton = "<button type=\"submit\">Switch the agent to Demo</button>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.AgentSwitchedToLive)]
    public async Task A_user_switches_an_agent_to_Live_with_a_valid_key_a_risk_confirmation_and_a_fresh_proof_and_it_is_recorded()
    {
        var clock = new TestClock(Morning);
        var market = new StandInMarketData();
        using var application = AgentsApplication.With(market, clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The user proves who they are, and stores a Live key that Binance takes. The proof
        // holds for a while, on this browser.
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var livePage = AgentBrowser.SwitchToLivePage(agent.Id);
        var statePage = AgentBrowser.AgentStatePage(agent.Id);

        // A new agent is in Demo. The page of its state and the page of its settings lead to
        // the page that switches it.
        Assert.Equal("Demo", agent.Mode);
        Assert.Contains($"<a href=\"{livePage}\">Switch the agent to Live</a>", await client.PageAsync(statePage));
        Assert.Contains($"<a href=\"{livePage}\">Switch the agent to Live</a>", await client.PageAsync(AgentBrowser.AgentPage(agent.Id)));

        // The page says what is at stake, and has a box for the confirmation, which is not
        // ticked for its user. Opening it switches nothing.
        clock.Set(Morning.AddMinutes(5));
        var page = AgentBrowser.Read(await client.PageAsync(livePage));

        Assert.Contains("<h1>Switch the agent Swing to Live</h1>", page);
        Assert.All(AgentPages.LiveRisks, risk => Assert.Contains($"<li>{risk}</li>", page));
        Assert.Contains("<a href=\"/legal/risk-disclosure\" target=\"_blank\" rel=\"noopener\">risk disclosure (opens in a new tab)</a>", page);
        Assert.Contains("<input id=\"live-risk\" name=\"Risk\" type=\"checkbox\" required value=\"confirmed\"", page);
        Assert.DoesNotContain(" checked", page);
        Assert.Contains("I understand that in Live this agent trades with real money in my Binance account", page);
        Assert.Contains(LiveButton, page);
        Assert.Contains($"<a href=\"{statePage}\">Keep the agent in Demo</a>", page);
        Assert.Equal("Demo", StoredAgent.All(application.Database).Single().Mode);

        // With the box ticked, the agent is in Live. The answer is the page of its state, by a
        // request that changes nothing.
        var (path, state) = await client.SwitchToLiveAsync(agent.Id);

        Assert.Equal(statePage, path);

        // Its mode, and nothing else: it is the draft that it was, and nothing was started.
        Assert.Equal(agent with { Mode = "Live" }, StoredAgent.All(application.Database).Single());
        Assert.Equal(["Draft: Created"], StoredAgentStateChange.Told(application.Database, agent.Id));

        // Whoever switches an agent to Live has considered Live: the account says so, as of the
        // switch, which does the last step of the setup checklist (FR-UI-4).
        Assert.Equal(
            Morning.AddMinutes(5).UtcDateTime,
            application.Database.Scalar<DateTime>($"SELECT live_considered_at FROM users WHERE id = '{user.Id}'"));

        Assert.Contains($"<p>Mode: {AgentBrowser.Badge(TradingMode.Live)} The agent trades <code>BTCUSDT</code> on Spot in your real Binance account.</p>", state);
        Assert.Contains($"<a href=\"{AgentBrowser.SwitchToDemoPage(agent.Id)}\">Switch the agent to Demo</a>", state);

        // The exchange was asked which symbols the market has in Live, where the agent trades now.
        Assert.Contains((Market.Spot, TradingMode.Live), market.Asked);

        // The switch is in the audit trail, with the agent as its target, and with who did it,
        // when, and from where.
        var recorded = Assert.Single(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        Assert.Equal(
            ("AgentSwitchedToLive", $"agent:{agent.Id}", Morning.AddMinutes(5), user.Id, "203.0.113.7"),
            (recorded.Action, recorded.Target, recorded.OccurredAt, recorded.UserId, recorded.Origin));

        // The log names the user and the agent by their ids, and never the name of the agent.
        var written = Assert.Single(application.Events(), written => written.Template == SwitchedTemplate);

        Assert.Equal(
            (user.Id.ToString(), agent.Id.ToString(), "Demo", "Live"),
            (written.Text("UserId"), written.Text("AgentId"), written.Text("PreviousTradingMode"), written.Text("TradingMode")));
        Assert.DoesNotContain(application.Events(), logged => logged.ToString().Contains("Swing", StringComparison.Ordinal));

        // The page that switched it says that the agent is in Live, and has no form.
        var again = await client.PageAsync(livePage);

        Assert.Contains("<p role=\"status\">This agent is in Live.</p>", again);
        Assert.DoesNotContain("type=\"checkbox\"", again);
        Assert.DoesNotContain(LiveButton, again);
    }

    [Fact]
    public async Task The_page_asks_for_a_fresh_proof_first_and_a_form_posted_without_one_switches_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry());
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var livePage = AgentBrowser.SwitchToLivePage(agent.Id);

        // The proof that the key was stored with holds until just before its time is up.
        clock.Set(Morning + ReauthenticationRules.HoldsFor - TimeSpan.FromSeconds(1));
        Assert.Contains(LiveButton, await client.PageAsync(livePage));

        // After that, being signed in is not enough: the page sends the user to prove who they
        // are, and shows nothing of itself.
        clock.Set(Morning + ReauthenticationRules.HoldsFor);
        var (asked, shown) = await client.OpenAsync(livePage);

        Assert.Equal($"{ConfirmPage}?returnUrl={Uri.EscapeDataString(livePage)}", asked);
        Assert.Contains("<h1>Confirm that it is you</h1>", shown);
        Assert.DoesNotContain(LiveButton, shown);

        // A form that is posted all the same, with the box ticked, switches nothing: the switch
        // itself asks for the proof, whatever the page did.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));
        var (refused, _) = await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.Equal(ConfirmPage, refused);
        Assert.Equal("Demo", StoredAgent.All(application.Database).Single().Mode);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");

        // The log says what the proof was asked for, and that the switch was refused for it.
        Assert.Contains(
            application.Events(),
            written => written.Template == AskedTemplate && written.Text("Action") == nameof(SensitiveAction.SwitchAgentToLive));
        Assert.Contains(
            application.Events(),
            written => written.Template == RefusedTemplate && written.Text("ModeSwitchOutcome") == nameof(ModeSwitchOutcome.ProofNeeded));

        // Once the user has proved who they are, they are back on the page, which has the form.
        var (back, form) = await client.ConfirmIdentityAsync(page: asked);

        Assert.Equal(livePage, back);
        Assert.Contains(LiveButton, form);

        // And the agent is switched.
        await client.SwitchToLiveAsync(agent.Id);

        Assert.Equal("Live", StoredAgent.All(application.Database).Single().Mode);
        Assert.Equal(user.Id, Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive").UserId);
    }

    [Fact]
    public async Task Nothing_is_switched_without_the_risk_confirmation()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry());
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var livePage = AgentBrowser.SwitchToLivePage(agent.Id);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(livePage));

        // A browser does not send the form while the box is not ticked. A form that comes
        // without the confirmation all the same switches nothing, and comes back saying so.
        var (path, answer) = await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token);

        Assert.Equal(livePage, path);
        Assert.Contains($"<p id=\"live-risk-error\" class=\"field-error\">{AgentPages.ConfirmRiskFirst}</p>", answer);
        Assert.Contains("aria-invalid=\"true\"", answer);
        Assert.Contains(LiveButton, answer);
        Assert.Equal("Demo", StoredAgent.All(application.Database).Single().Mode);

        // What confirms is the value of the box, and nothing else that a form may bring.
        await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, ("Risk", "yes"));
        await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, ("Confirm", "live"), ("Mode", "Live"));

        Assert.Equal("Demo", StoredAgent.All(application.Database).Single().Mode);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");
        Assert.Contains(
            application.Events(),
            written => written.Template == RefusedTemplate && written.Text("ModeSwitchOutcome") == nameof(ModeSwitchOutcome.RiskNotConfirmed));

        // With the box ticked, it is switched.
        await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.Equal("Live", StoredAgent.All(application.Database).Single().Mode);
    }

    [Fact]
    public async Task A_Live_key_that_is_missing_is_not_valid_or_lacks_the_permission_of_the_market_keeps_an_agent_in_Demo()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);

        // The user has a key for Demo, and none for Live. They have just proved who they are.
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "On Spot"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "On Futures", market: "Futures"));
        var onSpot = StoredAgent.Named(application.Database, "On Spot").Id;
        var onFutures = StoredAgent.Named(application.Database, "On Futures").Id;

        // No Live key: the page says so, leads to the keys, and has no form.
        var noKey = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(onSpot)));

        Assert.Contains(
            "<p class=\"form-error\" role=\"alert\">No Binance key is stored for Live. Add one, and switch the agent then.</p>", noKey);
        Assert.Contains($"<a href=\"{KeyBrowser.KeysPage}\">Your keys</a>", noKey);
        Assert.DoesNotContain("type=\"checkbox\"", noKey);
        Assert.DoesNotContain(LiveButton, noKey);

        // A form that is posted all the same switches nothing, and is told the same.
        var token = Browser.AntiforgeryTokenIn(noKey);
        var (path, refused) = await client.PostAsync(
            AgentBrowser.SwitchToLivePage(onSpot), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.Equal(AgentBrowser.SwitchToLivePage(onSpot), path);
        Assert.Contains("No Binance key is stored for Live.", refused);
        Assert.DoesNotContain(LiveButton, refused);

        // A Live key that trades on Spot only: the agent on Futures stays in Demo, and the page
        // names the permission that the key lacks. The agent on Spot is not kept by it.
        application.Providers.BinanceAnswer = (environment, _) => environment == TradingMode.Live
            ? StandInProviders.TakenInLive with { FuturesTrading = false }
            : StandInProviders.TakenInDemo;
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        var noPermission = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(onFutures)));

        Assert.Contains("Your Binance key for Live lacks the Futures trading permission, which this agent's market needs.", noPermission);
        Assert.DoesNotContain(LiveButton, noPermission);
        Assert.Contains(LiveButton, await client.PageAsync(AgentBrowser.SwitchToLivePage(onSpot)));

        await client.PostAsync(
            AgentBrowser.SwitchToLivePage(onFutures), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        // Binance refuses the key when it is checked again: no agent is switched with it.
        application.Providers.BinanceAnswer = (_, _) => new ExchangeKeyCheck(KeyVerdict.Refused, SpotTrading: false, FuturesTrading: false);
        clock.Set(Morning.AddMinutes(5));
        await client.CheckExchangeKeyAsync("live");

        Assert.Equal("Invalid", StoredExchangeCredential.Of(application.Database, user.Id, "Live").Status);

        var notValid = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(onSpot)));

        Assert.Contains("Your Binance key for Live is not valid: Binance did not take it when it was last checked.", notValid);
        Assert.DoesNotContain(LiveButton, notValid);

        await client.PostAsync(AgentBrowser.SwitchToLivePage(onSpot), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        // Both agents are in Demo, and nothing was recorded as a switch.
        Assert.Equal(["Demo", "Demo"], StoredAgent.All(application.Database).Select(agent => agent.Mode));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");
        Assert.Equal(
            3,
            application.Events().Count(written =>
                written.Template == RefusedTemplate && written.Text("ModeSwitchOutcome") == nameof(ModeSwitchOutcome.KeyBlocks)));
    }

    [Fact]
    public async Task Only_a_draft_or_a_stopped_agent_with_nothing_open_is_switched()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();

        var running = await application.GiveAgentAsync(user.Id, Morning, name: "Running", state: AgentState.Running);
        var paused = await application.GiveAgentAsync(user.Id, Morning, name: "Paused", state: AgentState.Paused, symbol: "SOLUSDT");
        var waiting = await application.GiveAgentAsync(
            user.Id, Morning, name: "Waiting", state: AgentState.AttentionRequired, market: Market.Futures);
        var stopped = await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: AgentState.Stopped);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));

        // An active agent is not switched, in whichever state it is active: its pages say when
        // the mode can be, and offer no switch, and a form that is posted all the same is refused.
        foreach (var (agent, inWords) in new[] { (running, "running"), (paused, "paused"), (waiting, "waiting for your attention") })
        {
            var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(agent)));

            Assert.Contains($"<p class=\"form-error\" role=\"alert\">This agent is {inWords}. {AgentPages.ModeLocked}</p>", page);
            Assert.DoesNotContain(LiveButton, page);

            var state = await client.PageAsync(AgentBrowser.AgentStatePage(agent));

            Assert.Contains($"<p role=\"note\">{AgentPages.ModeLocked}</p>", state);
            Assert.DoesNotContain("Switch the agent to Live", state);

            var (_, refused) = await client.PostAsync(
                AgentBrowser.SwitchToLivePage(agent), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

            Assert.Contains(AgentPages.ModeLocked, refused);
        }

        // Neither is a stopped agent that has open orders, or kept its position.
        exposure.HoldsOrders(stopped);
        Assert.Contains(
            $"This agent is stopped. {AgentPages.ModeLocked}", await client.PageAsync(AgentBrowser.SwitchToLivePage(stopped)));

        exposure.HoldsNothing(stopped);
        exposure.Holds(stopped);
        await client.PostAsync(AgentBrowser.SwitchToLivePage(stopped), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.All(StoredAgent.All(application.Database), agent => Assert.Equal("Demo", agent.Mode));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");

        // Once it holds nothing, it is switched, and stays stopped. So is a draft.
        exposure.HoldsNothing(stopped);
        await client.SwitchToLiveAsync(stopped);

        var draft = await application.GiveAgentAsync(user.Id, Morning, name: "Draft", symbol: "SOLUSDT");
        await client.SwitchToLiveAsync(draft);

        Assert.Equal(
            ["Draft: Live Draft", "Paused: Demo Paused", "Running: Demo Running", "Stopped: Live Stopped", "Waiting: Demo AttentionRequired"],
            StoredAgent.All(application.Database).Select(agent => $"{agent.Name}: {agent.Mode} {agent.State}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_agent_whose_symbol_the_market_lacks_in_Live_stays_in_Demo()
    {
        // The Spot market has both symbols in Demo, and one of them in Live, where it is priced
        // in another asset.
        var market = new StandInMarketData
        {
            Symbols = (_, mode) => mode == TradingMode.Demo
                ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT")
                : InAnotherAsset(StandInMarketData.Listed("BTCUSDT"), "FDUSD"),
        };
        using var application = AgentsApplication.With(market, new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Solana", symbol: "SOLUSDT"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Bitcoin"));
        var solana = StoredAgent.Named(application.Database, "Solana").Id;
        var bitcoin = StoredAgent.Named(application.Database, "Bitcoin");
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));

        // The page says that the market does not have the symbol there, and leads to the settings.
        var lacks = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(solana)));

        Assert.Contains("Spot does not have SOLUSDT in Live.", lacks);
        Assert.Contains($"<a href=\"{AgentBrowser.AgentPage(solana)}\">Settings of the agent</a>", lacks);
        Assert.DoesNotContain(LiveButton, lacks);

        var (_, refused) = await client.PostAsync(
            AgentBrowser.SwitchToLivePage(solana), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.Contains("Spot does not have SOLUSDT in Live.", refused);
        Assert.Equal("Demo", StoredAgent.Named(application.Database, "Solana").Mode);

        // An agent whose symbol the market has there is switched, and its capital is in the
        // asset that the symbol is priced in there.
        Assert.Equal("USDT", bitcoin.QuoteAsset);
        await client.SwitchToLiveAsync(bitcoin.Id);

        Assert.Equal(bitcoin with { Mode = "Live", QuoteAsset = "FDUSD" }, StoredAgent.Named(application.Database, "Bitcoin"));

        // In Live, the form of its settings offers the symbols that its market has in Live.
        var settings = await client.PageAsync(AgentBrowser.AgentPage(bitcoin.Id));

        Assert.Equal(["BTCUSDT"], AgentBrowser.SymbolsOfferedOn(settings));

        // While the exchange gives no answer about Live, nothing is switched: a symbol is not
        // taken on trust.
        market.Symbols = (_, mode) => mode == TradingMode.Demo ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT") : StandInMarketData.NoAnswer;

        var unknown = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToLivePage(solana)));

        Assert.Contains("Binance gave no answer about which symbols Spot has in Live", unknown);
        Assert.DoesNotContain(LiveButton, unknown);

        await client.PostAsync(AgentBrowser.SwitchToLivePage(solana), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed);

        Assert.Equal("Demo", StoredAgent.Named(application.Database, "Solana").Mode);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");
    }

    [Fact]
    public async Task A_form_that_is_sent_twice_switches_once_also_after_the_agent_was_started()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry());
        var agent = Assert.Single(StoredAgent.All(application.Database));
        await application.GiveTeamsAsync(user.Id, Morning);
        var livePage = AgentBrowser.SwitchToLivePage(agent.Id);
        var statePage = AgentBrowser.AgentStatePage(agent.Id);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(livePage));

        // The form is sent, and the agent is in Live.
        Assert.Equal(statePage, (await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed)).Path);

        // Sent again, it finds the agent in Live, which is what was asked for: it is answered
        // as the first one was, and nothing is recorded again.
        clock.Set(Morning.AddMinutes(1));
        Assert.Equal(statePage, (await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed)).Path);

        // So it is when the agent was started meanwhile, and when the proof is no longer fresh:
        // the agent is in Live, and nothing stands in the way of what is done already.
        await client.ConfirmControlAsync(agent.Id, "start");
        clock.Set(Morning + ReauthenticationRules.HoldsFor);

        Assert.Equal(statePage, (await client.PostAsync(livePage, AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed)).Path);

        var stored = StoredAgent.All(application.Database).Single();

        Assert.Equal(("Live", "Running"), (stored.Mode, stored.State));
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToLive");
        Assert.Single(application.Events(), written => written.Template == SwitchedTemplate);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.AgentSwitchedToDemo)]
    public async Task A_user_switches_an_agent_back_to_Demo_and_it_is_recorded()
    {
        var clock = new TestClock(Morning);
        var market = new StandInMarketData();
        using var application = AgentsApplication.With(market, clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);

        // The user has no key at all, and has not proved who they are since they signed in: an
        // agent goes back to Demo without either, because no money is at stake there.
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", state: AgentState.Stopped, mode: TradingMode.Live);
        var running = await application.GiveAgentAsync(
            user.Id, Morning, name: "Running", state: AgentState.Running, symbol: "SOLUSDT", mode: TradingMode.Live);
        var demoPage = AgentBrowser.SwitchToDemoPage(agent);
        var statePage = AgentBrowser.AgentStatePage(agent);

        // The pages of an agent in Live lead to the page that switches it back, and not to the
        // one that switches it to Live.
        var state = await client.PageAsync(statePage);

        Assert.Contains($"<a href=\"{demoPage}\">Switch the agent to Demo</a>", state);
        Assert.DoesNotContain("Switch the agent to Live", state);
        Assert.Contains($"<a href=\"{demoPage}\">Switch the agent to Demo</a>", await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // The page asks once more, and takes neither a box nor a proof.
        clock.Set(Morning.AddMinutes(30));
        var (address, page) = await client.OpenAsync(demoPage);

        Assert.Equal(demoPage, address);
        Assert.Contains("<h1>Switch the agent Swing to Demo</h1>", page);
        Assert.Contains(DemoButton, page);
        Assert.DoesNotContain("type=\"checkbox\"", page);
        Assert.Contains($"<a href=\"{statePage}\">Keep the agent in Live</a>", page);
        Assert.Equal("Live", StoredAgent.Named(application.Database, "Swing").Mode);

        var before = StoredAgent.Named(application.Database, "Swing");
        var (path, switched) = await client.SwitchToDemoAsync(agent);

        Assert.Equal(statePage, path);
        Assert.Equal(before with { Mode = "Demo" }, StoredAgent.Named(application.Database, "Swing"));
        Assert.Contains(AgentBrowser.Badge(TradingMode.Demo), switched);
        Assert.Contains((Market.Spot, TradingMode.Demo), market.Asked);

        // It is in the audit trail, as an action of its own.
        var recorded = Assert.Single(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("AgentSwitched", StringComparison.Ordinal));

        Assert.Equal(
            ("AgentSwitchedToDemo", $"agent:{agent}", Morning.AddMinutes(30), user.Id, "203.0.113.7"),
            (recorded.Action, recorded.Target, recorded.OccurredAt, recorded.UserId, recorded.Origin));

        // A form that is sent twice finds the agent in Demo, and records nothing again.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));

        Assert.Equal(statePage, (await client.PostAsync(demoPage, AgentBrowser.SwitchToDemoForm, token)).Path);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToDemo");
        Assert.Contains("<p role=\"status\">This agent is in Demo.</p>", await client.PageAsync(demoPage));

        // An agent that runs in Live is stopped before it is switched, like one in Demo.
        var locked = AgentBrowser.Read(await client.PageAsync(AgentBrowser.SwitchToDemoPage(running)));

        Assert.Contains($"This agent is running. {AgentPages.ModeLocked}", locked);
        Assert.DoesNotContain(DemoButton, locked);

        await client.PostAsync(AgentBrowser.SwitchToDemoPage(running), AgentBrowser.SwitchToDemoForm, token);

        Assert.Equal("Live", StoredAgent.Named(application.Database, "Running").Mode);

        // And an agent whose symbol the market lacks in Demo stays in Live.
        market.Symbols = (_, mode) => mode == TradingMode.Live ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT") : StandInMarketData.Listed("BTCUSDT");
        var solana = await application.GiveAgentAsync(
            user.Id, Morning, name: "Solana", state: AgentState.Stopped, symbol: "SOLUSDT", mode: TradingMode.Live);

        Assert.Contains("Spot does not have SOLUSDT in Demo.", await client.PageAsync(AgentBrowser.SwitchToDemoPage(solana)));

        await client.PostAsync(AgentBrowser.SwitchToDemoPage(solana), AgentBrowser.SwitchToDemoForm, token);

        Assert.Equal("Live", StoredAgent.Named(application.Database, "Solana").Mode);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentSwitchedToDemo");
    }

    [Fact]
    public async Task An_agent_of_another_user_is_not_switched()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));

        // Grace has an agent in Demo and one in Live.
        var grace = await application.RegisterVerifiedAsync(Grace);
        var inDemo = await application.GiveAgentAsync(grace.Id, Morning, name: "Grace's in Demo");
        var inLive = await application.GiveAgentAsync(
            grace.Id, Morning, name: "Grace's in Live", state: AgentState.Stopped, symbol: "SOLUSDT", mode: TradingMode.Live);
        var before = TextOf(application.RowsOf(grace.Id));

        // Ada has a Live key, and has just proved who she is.
        await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));

        // The pages of Grace's agents are not found for her, like those of an agent that does
        // not exist, and say nothing of them.
        foreach (var page in new[] { AgentBrowser.SwitchToLivePage(inDemo), AgentBrowser.SwitchToDemoPage(inLive) })
        {
            using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);
            var shown = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Grace", shown);
        }

        // And a form that she posts at them is not found either, and switches nothing.
        using (var answer = await client.SendFormAsync(
            AgentBrowser.SwitchToLivePage(inDemo), AgentBrowser.SwitchToLiveForm, token, AgentBrowser.RiskConfirmed))
        {
            Assert.Contains(answer.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        }

        using (var answer = await client.SendFormAsync(AgentBrowser.SwitchToDemoPage(inLive), AgentBrowser.SwitchToDemoForm, token))
        {
            Assert.Contains(answer.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        }

        // Every row of Grace is as it was, and nothing was recorded as a switch.
        Assert.Equal(before, TextOf(application.RowsOf(grace.Id)));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("AgentSwitched", StringComparison.Ordinal));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Fact]
    public async Task A_copy_of_an_agent_in_Live_is_a_draft_in_Demo_and_is_refused_where_Demo_lacks_its_symbol()
    {
        // The Spot market has both symbols in Live, and one of them in Demo, where it is priced
        // in another asset.
        var market = new StandInMarketData
        {
            Symbols = (_, mode) => mode == TradingMode.Live
                ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT")
                : InAnotherAsset(StandInMarketData.Listed("BTCUSDT"), "TUSD"),
        };
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(market, clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var bitcoin = await application.GiveAgentAsync(user.Id, Morning, name: "Bitcoin", state: AgentState.Running, mode: TradingMode.Live);
        var solana = await application.GiveAgentAsync(
            user.Id, Morning, name: "Solana", state: AgentState.Stopped, symbol: "SOLUSDT", mode: TradingMode.Live);

        // A copy of an agent in Live is a new agent: a draft in Demo, whatever the agent is.
        // Its capital is in the asset that its symbol is priced in there.
        clock.Set(Morning.AddMinutes(10));
        var (path, _) = await client.CloneAgentAsync(bitcoin);
        var copy = StoredAgent.Named(application.Database, "Copy of Bitcoin");

        Assert.Equal(AgentBrowser.AgentPage(copy.Id), path);
        Assert.Equal(("Demo", "Draft", "BTCUSDT", "TUSD"), (copy.Mode, copy.State, copy.Symbol, copy.QuoteAsset));
        Assert.Equal("Live", StoredAgent.Named(application.Database, "Bitcoin").Mode);
        Assert.Equal(["Demo"], StoredAgentStateChange.Of(application.Database, copy.Id).Select(change => change.Mode));

        // An agent whose symbol the market lacks in Demo is not copied: the copy would trade a
        // symbol that its market does not have. The list says so.
        var (list, refused) = await client.CloneAgentAsync(solana);

        Assert.Equal(AgentBrowser.AgentsPage, list);
        Assert.Contains(
            "<p class=\"form-error\" role=\"alert\">A copy is made in Demo, and the market of this agent does not have its symbol " +
            "there. Nothing was copied.</p>",
            refused);
        Assert.DoesNotContain(StoredAgent.All(application.Database), agent => agent.Name == "Copy of Solana");

        // Neither is one while the exchange gives no answer about Demo.
        market.Symbols = (_, mode) => mode == TradingMode.Live ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT") : StandInMarketData.NoAnswer;

        var (_, unknown) = await client.CloneAgentAsync(bitcoin);

        Assert.Contains("A copy is made in Demo, and Binance gave no answer about which symbols the market has there.", unknown);
        Assert.Equal(3, StoredAgent.All(application.Database).Count);

        // An agent in Demo is copied all the same: its symbol was held to its market there
        // when it was stored, so the copy does not wait for what the exchange says now.
        await client.CloneAgentAsync(copy.Id);

        var second = StoredAgent.Named(application.Database, "Copy of Copy of Bitcoin");

        Assert.Equal(("Demo", "Draft", "TUSD"), (second.Mode, second.State, second.QuoteAsset));
    }

    [Fact]
    public async Task A_form_that_brings_a_mode_creates_an_agent_in_Demo_all_the_same()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The form that creates an agent has no field for the mode, and one that is posted with
        // it all the same is not read: a new agent is in Demo (FR-AGT-7).
        var (path, _) = await client.CreateAgentAsync([.. AgentBrowser.Entry(name: "Swing"), ("Mode", "Live"), ("Confirm", "live")]);

        Assert.Equal(AgentBrowser.AgentsPage, path);

        var agent = Assert.Single(StoredAgent.All(application.Database));

        Assert.Equal(("Demo", "Draft"), (agent.Mode, agent.State));
        Assert.Equal(["Demo"], StoredAgentStateChange.Of(application.Database, agent.Id).Select(change => change.Mode));

        // Neither does the form that changes its settings switch it.
        await client.SaveAgentAsync(agent.Id, [.. AgentBrowser.Entry(name: "Renamed"), ("Mode", "Live"), ("Confirm", "live")]);

        var saved = Assert.Single(StoredAgent.All(application.Database));

        Assert.Equal(("Renamed", "Demo"), (saved.Name, saved.Mode));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("AgentSwitched", StringComparison.Ordinal));
    }

    /// <summary>A reading of the symbols of a market, with each priced in another asset than its name says.</summary>
    private static ExchangeSymbols InAnotherAsset(ExchangeSymbols symbols, string quoteAsset) =>
        new(symbols.Outcome, [.. symbols.Symbols.Select(symbol => symbol with { QuoteAsset = quoteAsset })], symbols.ReadAt);
}
