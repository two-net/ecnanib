using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// A user creates, changes, copies, and deletes agents, and agent names are unique per user
/// (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, allocated capital,
/// and risk settings within the hard bounds (FR-AGT-2, FR-RSK-2). The symbol is one of the
/// configuration file that the chosen market has (FR-AGT-6). The market, the symbol, and the
/// timeframe change only while the agent is a draft, or is stopped with no open position or
/// orders (FR-AGT-8). A user whose address is not verified creates no agent, and no user has
/// more than the configured maximum (FR-ACC-1, FR-ACC-7).
/// </summary>
public class AgentPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string AgentsPage = AgentBrowser.AgentsPage;
    private const string NewAgentPage = AgentBrowser.NewAgentPage;

    private const string CreatedTemplate = "User {UserId} created agent {AgentId}";
    private const string NotVerifiedTemplate = "User {UserId} was refused creating an agent: their email address is not verified";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_user_creates_an_agent_which_is_a_draft_in_Demo_with_the_settings_that_were_entered()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The list is empty, says how many agents an account can have, and leads to the form.
        var empty = await client.PageAsync(AgentsPage);

        Assert.Contains("Your agents</h1>", empty);
        Assert.Contains("<p>You have no agent yet.</p>", empty);
        Assert.Contains("<p>You have 0 of 7 agents.</p>", empty);
        Assert.Contains("<a href=\"/agents/new\">Create an agent</a>", empty);

        // The form starts from the defaults of the one profile, as the file has them, on the
        // Spot market, with the symbols that it has. The timeframe is for the user to choose.
        var form = await client.PageAsync(NewAgentPage);

        Assert.Contains("<h1>Create an agent</h1>", form);
        Assert.Equal(
            ("1.5", "1.75", "4", "2.5", "5", "20", "0.8"),
            (ValueOf(form, "RiskPerTradePercent"), ValueOf(form, "MinRewardToRisk"), ValueOf(form, "LeverageCap"),
                ValueOf(form, "LiquidationBuffer"), ValueOf(form, "DailyLossPercent"), ValueOf(form, "DrawdownPercent"),
                ValueOf(form, "MaxEntryDistancePercent")));
        Assert.Equal(["BTCUSDT", "SOLUSDT"], AgentBrowser.SymbolsOfferedOn(form));
        Assert.Contains("<option value=\"Spot\" selected>Spot</option>", form);
        Assert.Contains("<option value selected>Choose a timeframe</option>", form);
        Assert.Contains($"<p>Mode: {AgentBrowser.Badge(TradingMode.Demo)} The agent trades in Binance's test environment, with no real funds.</p>", AgentBrowser.Read(form));
        Assert.Contains("<p>A new agent is in Demo. You can switch it to Live once it is created.</p>", form);
        Assert.DoesNotContain("name=\"Mode\"", form);

        // Every risk setting says what it is, with its bounds and its default (NFR-USA-2).
        Assert.Contains("From 0.25 to 4 percent. The default is 1.5.", form);
        Assert.Contains("A whole number from 2 to 8. The default is 4.", form);
        Assert.Contains("1.25 or higher. The default is 1.75.", form);
        Assert.Contains("1.75 or higher. The default is 2.5.", form);

        // They are the settings of the one profile, and the form names it (FR-RSK-1).
        Assert.Contains("Every agent trades the one profile, Aggressive.", form);

        // The four roles of the team are named, each with what it does (FR-TEAM-1). What a role
        // is given is shown in AgentRoleTests.
        Assert.All(
            new[] { "Market Analyst", "Sentiment Analyst", "Strategist", "Risk Manager" },
            role => Assert.Contains($"<h3>{role}</h3>", form));
        Assert.Contains("Combines both reports, recalled lessons, and the current position into a trade plan.", form);

        clock.Set(Morning.AddMinutes(5));
        var (path, list) = await client.CreateAgentAsync(AgentBrowser.Entry(
            name: "  Swing on Bitcoin ",
            market: "Futures",
            symbol: "BTCUSDT",
            timeframe: "4H",
            capital: "2500.50",
            riskPerTrade: "2.25",
            minRewardToRisk: "2",
            leverageCap: "6",
            liquidationBuffer: "3.5",
            dailyLoss: "7",
            drawdown: "30",
            entryDistance: "1.2"));

        // The answer is the list, with the agent on it: a draft, in Demo.
        Assert.Equal(AgentsPage, path);

        var agent = Assert.Single(StoredAgent.All(application.Database));

        Assert.Contains($"<th scope=\"row\"><a href=\"/agents/{agent.Id}\">Swing on Bitcoin</a></th>", list);
        Assert.All(
            new[] { $"<td>{AgentBrowser.Badge(TradingMode.Demo)}</td>", "<td>Futures</td>", "<td><code>BTCUSDT</code></td>", "<td>4H</td>", "<td>2500.5 USDT</td>", "<td>Draft</td>" },
            cell => Assert.Contains(cell, list));
        Assert.Contains("<p>You have 1 of 7 agents.</p>", list);

        // The row holds what was entered, with the name trimmed, and belongs to who is signed in.
        Assert.Equal(
            new StoredAgent(
                agent.Id,
                user.Id,
                "Swing on Bitcoin",
                "Demo",
                "Futures",
                "BTCUSDT",
                "USDT",
                "FourHours",
                2500.5m,
                2.25m,
                2m,
                6,
                3.5m,
                7m,
                30m,
                1.2m,
                "Draft",
                "Created",
                Morning.AddMinutes(5),
                Morning.AddMinutes(5)),
            agent);

        // Its creation is the first change of its state, with the same cause and time (FR-AGT-10).
        var first = Assert.Single(StoredAgentStateChange.All(application.Database));
        Assert.Equal(
            (agent.Id, user.Id, "Draft", "Created", Morning.AddMinutes(5)),
            (first.AgentId, first.UserId, first.State, first.Cause, first.ChangedAt));

        // The log names the user and the agent by their ids, and never by what the user typed.
        var created = Assert.Single(application.Events(), written => written.Template == CreatedTemplate);
        Assert.Equal((user.Id.ToString(), agent.Id.ToString()), (created.Text("UserId"), created.Text("AgentId")));
        Assert.DoesNotContain("Swing on Bitcoin", application.LogText());
    }

    [Fact]
    public async Task The_symbols_that_are_offered_are_those_of_the_file_that_the_chosen_market_has()
    {
        // The file lists BTCUSDT and SOLUSDT. The Spot market has both, and the Futures market
        // has the first only. The exchange also has a symbol that the file does not list.
        var market = new StandInMarketData
        {
            Symbols = (where, _) => where == Market.Spot
                ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT", "ETHUSDT")
                : StandInMarketData.Listed("BTCUSDT", "ETHUSDT"),
        };
        using var application = AgentsApplication.With(market, new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A press of the button that shows the symbols of the chosen market stores nothing: the
        // form comes back with the symbols of Futures, and with what was typed.
        var (path, shown) = await client.SubmitAsync(
            NewAgentPage,
            AgentBrowser.SaveForm,
            [.. AgentBrowser.Entry(name: "Typed so far", market: "Futures", symbol: "SOLUSDT", timeframe: "15m"), ("Show", "symbols")]);

        Assert.Equal(NewAgentPage, path);
        Assert.Contains(">Symbol on Futures</label>", shown);
        Assert.Equal("Typed so far", ValueOf(shown, "Name"));
        Assert.Contains("<option value=\"Futures\" selected>Futures</option>", shown);
        Assert.Contains("<option value=\"15m\" selected>", shown);
        Assert.DoesNotContain("class=\"field-error\"", shown);
        Assert.Empty(StoredAgent.All(application.Database));

        // The exchange has ETHUSDT on both markets, and the file does not list it: it is not
        // offered. Each symbol says what it is priced in.
        Assert.Equal(["BTCUSDT"], AgentBrowser.SymbolsOfferedOn(shown));
        Assert.Contains("BTCUSDT, priced in USDT</option>", shown);

        // A symbol that the chosen market lacks is refused on the server, whatever the form offered.
        var (refusedAt, refused) = await client.CreateAgentAsync(AgentBrowser.Entry(market: "Futures", symbol: "SOLUSDT"));

        Assert.Equal(NewAgentPage, refusedAt);
        Assert.Equal(["The chosen market does not have that symbol. Choose one of the symbols listed for the market."], ErrorsOn(refused));
        Assert.Empty(StoredAgent.All(application.Database));

        // So is one that the file does not list, though the exchange has it.
        var (_, notListed) = await client.CreateAgentAsync(AgentBrowser.Entry(market: "Spot", symbol: "ETHUSDT"));

        Assert.Equal(["Choose a symbol."], ErrorsOn(notListed));
        Assert.Empty(StoredAgent.All(application.Database));

        // The same symbol on the market that has it is stored, with what it is priced in.
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(market: "Spot", symbol: "SOLUSDT"))).Path);
        Assert.Equal(("Spot", "SOLUSDT", "USDT"), Stored(application, agent => (agent.Market, agent.Symbol, agent.QuoteAsset)));

        // Every question was about Demo, where a new agent trades.
        Assert.All(market.Asked, asked => Assert.Equal(TradingMode.Demo, asked.Mode));
    }

    [Fact]
    public async Task Nothing_is_saved_while_the_exchange_does_not_say_which_symbols_the_market_has()
    {
        var market = new StandInMarketData { Symbols = (_, _) => StandInMarketData.NoAnswer };
        using var application = AgentsApplication.With(market, new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The form says that no symbol can be chosen now, and offers none.
        var form = await client.PageAsync(NewAgentPage);

        Assert.Contains("Binance gave no answer about which symbols Spot has, so none can be chosen now.", form);
        Assert.Empty(AgentBrowser.SymbolsOfferedOn(form));

        // A form that is posted all the same stores nothing: a symbol is not taken on trust.
        var (path, answer) = await client.CreateAgentAsync(AgentBrowser.Entry());

        Assert.Equal(NewAgentPage, path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.SymbolsUnknown}</p>", answer);
        Assert.Equal("Swing", ValueOf(answer, "Name"));
        Assert.Empty(StoredAgent.All(application.Database));

        // Once the exchange answers, the agent is created.
        market.Symbols = (_, _) => StandInMarketData.Listed("BTCUSDT", "SOLUSDT");
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry())).Path);
        var agent = Assert.Single(StoredAgent.All(application.Database));

        // An agent keeps the symbol it has while the exchange gives no answer: its name, its
        // capital, and its risk settings are changed without asking the exchange anything.
        market.Symbols = (_, _) => StandInMarketData.NoAnswer;

        var editor = await client.PageAsync(AgentBrowser.AgentPage(agent.Id));
        Assert.Equal(["BTCUSDT"], AgentBrowser.SymbolsOfferedOn(editor));

        Assert.Equal(
            AgentsPage,
            (await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "Swing, renamed", capital: "750", dailyLoss: "6"))).Path);
        Assert.Equal(
            ("Swing, renamed", 750m, 6m, "BTCUSDT"),
            Stored(application, stored => (stored.Name, stored.AllocatedCapital, stored.DailyLossPercent, stored.Symbol)));

        // Another symbol is not taken while the exchange cannot say that the market has it.
        var (changedAt, changed) = await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "Swing, renamed", symbol: "SOLUSDT"));

        Assert.Equal(AgentBrowser.AgentPage(agent.Id), changedAt);
        Assert.Contains(AgentPages.SymbolsUnknown, changed);
        Assert.Equal("BTCUSDT", Stored(application, stored => stored.Symbol));
    }

    [Theory]
    [InlineData("RiskPerTradePercent", "0.24", "0.25", "4", "4.01", "This setting is from 0.25 to 4 percent.")]
    [InlineData("MinRewardToRisk", "1.24", "1.25", "100", "100.01", "The minimum reward-to-risk ratio is 1.25 or higher, and 100 at most.")]
    [InlineData("LeverageCap", "1", "2", "8", "9", "The leverage cap is from 2 to 8.")]
    [InlineData("LiquidationBuffer", "1.74", "1.75", "100", "100.01", "The liquidation buffer is 1.75 or higher, and 100 at most.")]
    [InlineData("DailyLossPercent", "0.99", "1", "12", "12.01", "This setting is from 1 to 12 percent.")]
    [InlineData("DrawdownPercent", "7.99", "8", "40", "40.01", "This setting is from 8 to 40 percent.")]
    [InlineData("MaxEntryDistancePercent", "0.19", "0.2", "2", "2.01", "This setting is from 0.2 to 2 percent.")]
    public async Task A_risk_setting_is_taken_within_its_hard_bounds_and_refused_outside_them_on_the_server(
        string field, string belowTheLowest, string lowest, string highest, string aboveTheHighest, string message)
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Just outside the bounds, on either side: nothing is stored, the field is marked, and
        // the page says what the setting takes. What was entered is still in the field.
        foreach (var outside in new[] { belowTheLowest, aboveTheHighest })
        {
            var (path, answer) = await client.CreateAgentAsync(AgentBrowser.Entry().With(field, outside));

            Assert.Equal(NewAgentPage, path);
            Assert.Equal([message], ErrorsOn(answer));
            Assert.Matches($"name=\"{field}\"[^>]* aria-invalid=\"true\"", answer);
            Assert.Equal(outside, ValueOf(answer, field));
            Assert.Empty(StoredAgent.All(application.Database));
        }

        // At the bounds themselves the agent is stored.
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "At the lowest").With(field, lowest))).Path);
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "At the highest").With(field, highest))).Path);
        Assert.Equal(2, StoredAgent.All(application.Database).Count);

        // And an agent that is stored is held to them in the same way when it is changed.
        var agent = StoredAgent.Named(application.Database, "At the lowest");
        var stored = Row(application, agent.Id);

        foreach (var outside in new[] { belowTheLowest, aboveTheHighest })
        {
            var (path, answer) = await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "At the lowest").With(field, outside));

            Assert.Equal(AgentBrowser.AgentPage(agent.Id), path);
            Assert.Equal([message], ErrorsOn(answer));
            Assert.Equal(stored, Row(application, agent.Id));
        }
    }

    [Fact]
    public async Task The_limits_that_the_specification_fixes_are_said_on_the_form_and_no_form_changes_them()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The form says both in plain language, and that they cannot be changed (NFR-USA-2).
        var form = AgentBrowser.Read(await client.PageAsync(NewAgentPage));

        Assert.Contains("<p>Two more limits are fixed. They are the same for every agent, and cannot be changed:</p>", form);
        Assert.Contains("<li>On Spot, a position is worth at most 100 percent of the agent's equity.</li>", form);
        Assert.Contains("<li>An agent has no more than 1 position open at a time.</li>", form);

        // The risk settings have a field for each of the seven that are an agent's, in the
        // order of the specification, and for nothing else: neither of the two is asked for.
        var settings = Regex.Match(form, "<legend>Risk settings</legend>.*?</fieldset>", RegexOptions.Singleline);

        Assert.True(settings.Success, $"The page has no risk settings:{Environment.NewLine}{form}");
        Assert.Equal(
            [
                "RiskPerTradePercent", "MinRewardToRisk", "LeverageCap", "LiquidationBuffer", "DailyLossPercent", "DrawdownPercent",
                "MaxEntryDistancePercent",
            ],
            Regex.Matches(settings.Value, " name=\"([^\"]+)\"").Select(field => field.Groups[1].Value));

        // A form that brings them all the same stores what a form without them stores.
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "As offered"))).Path);
        Assert.Equal(
            AgentsPage,
            (await client.CreateAgentAsync(
            [
                .. AgentBrowser.Entry(name: "With more"),
                ("SpotPositionPercentOfEquity", "500"),
                ("MaxPositionPercent", "500"),
                ("OpenPositionsPerAgent", "3"),
                ("OpenPositions", "3"),
            ])).Path);

        var offered = StoredAgent.Named(application.Database, "As offered");
        var withMore = StoredAgent.Named(application.Database, "With more");

        Assert.Equal(offered with { Id = withMore.Id, Name = "With more" }, withMore);
    }

    [Theory]
    [InlineData("Name", "", "Enter a name.")]
    [InlineData("Name", "   ", "Enter a name.")]
    [InlineData("Name", "A name that is far too long to be the name of an agent of anyone", "That name is too long. A name has 60 characters at most.")]
    [InlineData("Name", "Two\nlines", "A name is in one line. Enter it without a line break.")]
    [InlineData("Market", "Margin", "Choose a market: Spot or Futures.")]
    [InlineData("Market", "1", "Choose a market: Spot or Futures.")]
    [InlineData("Symbol", "", "Choose a symbol.")]
    [InlineData("Symbol", "DOGEUSDT", "Choose a symbol.")]
    [InlineData("Timeframe", "", "Choose a timeframe: 15m, 1H, 4H, or 1D.")]
    [InlineData("Timeframe", "1h", "Choose a timeframe: 15m, 1H, 4H, or 1D.")]
    [InlineData("Timeframe", "1W", "Choose a timeframe: 15m, 1H, 4H, or 1D.")]
    [InlineData("Timeframe", "OneHour", "Choose a timeframe: 15m, 1H, 4H, or 1D.")]
    [InlineData("AllocatedCapital", "", "Enter the capital to allocate.")]
    [InlineData("AllocatedCapital", "0", "Enter a capital above 0, and 1000000000000 at most.")]
    [InlineData("AllocatedCapital", "1000000000000.01", "Enter a capital above 0, and 1000000000000 at most.")]
    [InlineData("AllocatedCapital", "-5", "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.")]
    [InlineData("AllocatedCapital", "1,000", "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.")]
    [InlineData("AllocatedCapital", "1e3", "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.")]
    [InlineData("AllocatedCapital", "a lot", "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.")]
    [InlineData("AllocatedCapital", "0.123456789", "Enter the capital with 8 decimal places at most.")]
    [InlineData("RiskPerTradePercent", "", "Enter a percentage.")]
    [InlineData("RiskPerTradePercent", "two", "Enter the percentage as a number, with digits and a decimal point: 2 or 0.5.")]
    [InlineData("RiskPerTradePercent", "1.555", "Enter the percentage with 2 decimal places at most.")]
    [InlineData("LeverageCap", "", "Enter the leverage cap.")]
    [InlineData("LeverageCap", "2.5", "Enter the leverage cap as a whole number.")]
    [InlineData("LeverageCap", "five", "Enter the leverage cap as a whole number.")]
    [InlineData("MinRewardToRisk", "", "Enter the minimum reward-to-risk ratio.")]
    [InlineData("MinRewardToRisk", "high", "Enter a number, with digits and a decimal point: 2 or 1.5.")]
    [InlineData("MinRewardToRisk", "1.555", "Enter the number with 2 decimal places at most.")]
    [InlineData("LiquidationBuffer", "", "Enter the liquidation buffer.")]
    [InlineData("LiquidationBuffer", "-2", "Enter a number, with digits and a decimal point: 2 or 1.5.")]
    [InlineData("LiquidationBuffer", "2.005", "Enter the number with 2 decimal places at most.")]
    public async Task What_is_entered_for_a_setting_is_held_to_what_the_setting_takes(string field, string entered, string message)
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (path, answer) = await client.CreateAgentAsync(AgentBrowser.Entry().With(field, entered));

        // Nothing is stored, and the page says what is wrong, at the field that it is wrong in.
        Assert.Equal(NewAgentPage, path);
        Assert.Equal([message], ErrorsOn(answer));
        Assert.Empty(StoredAgent.All(application.Database));

        // What was right is still in the form, so that it is not typed again.
        Assert.Equal(("20", "0.8"), (ValueOf(answer, "DrawdownPercent"), ValueOf(answer, "MaxEntryDistancePercent")));
    }

    [Fact]
    public async Task Everything_that_is_wrong_is_said_at_once_each_at_its_field()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (_, answer) = await client.CreateAgentAsync(
            AgentBrowser.Entry(name: "", timeframe: "", capital: "0", riskPerTrade: "9", leverageCap: "0", drawdown: "x"));

        Assert.Equal(
            [
                "Enter a name.",
                "Choose a timeframe: 15m, 1H, 4H, or 1D.",
                "Enter a capital above 0, and 1000000000000 at most.",
                "This setting is from 0.25 to 4 percent.",
                "The leverage cap is from 2 to 8.",
                "Enter the percentage as a number, with digits and a decimal point: 2 or 0.5.",
            ],
            ErrorsOn(answer));
        Assert.Equal(6, Regex.Count(answer, "aria-invalid=\"true\""));
        Assert.Empty(StoredAgent.All(application.Database));
    }

    [Fact]
    public async Task A_user_has_no_two_agents_of_one_name()
    {
        const string taken = "You have another agent of that name. Give this one a name of its own.";

        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"))).Path);

        // The same name again, also with spaces around it, is refused, and nothing is stored.
        foreach (var name in new[] { "Swing", "  Swing  " })
        {
            var (path, answer) = await client.CreateAgentAsync(AgentBrowser.Entry(name: name, symbol: "SOLUSDT"));

            Assert.Equal(NewAgentPage, path);
            Assert.Equal([taken], ErrorsOn(answer));
        }

        Assert.Single(StoredAgent.All(application.Database));

        // Another name is another agent. A name is compared as it is written.
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "Scalp"))).Path);
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "swing"))).Path);

        // An agent cannot be given the name of another, and keeps its own when it is saved.
        var scalp = StoredAgent.Named(application.Database, "Scalp");
        var (renamedAt, renamed) = await client.SaveAgentAsync(scalp.Id, AgentBrowser.Entry(name: "Swing"));

        Assert.Equal(AgentBrowser.AgentPage(scalp.Id), renamedAt);
        Assert.Equal([taken], ErrorsOn(renamed));
        Assert.Equal(AgentsPage, (await client.SaveAgentAsync(scalp.Id, AgentBrowser.Entry(name: "Scalp", capital: "50"))).Path);
        Assert.Equal(["Swing", "Scalp", "swing"], StoredAgent.Of(application.Database, ada.Id).Select(agent => agent.Name));

        // The names are a user's own: another user has an agent of the same name.
        var grace = await application.RegisterVerifiedAsync(Grace);
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);

        Assert.Equal(AgentsPage, (await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"))).Path);
        Assert.Equal(["Swing"], StoredAgent.Of(application.Database, grace.Id).Select(agent => agent.Name));
    }

    [Fact]
    public async Task A_user_changes_the_settings_of_an_agent()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing", market: "Spot", symbol: "SOLUSDT", timeframe: "1H", capital: "1000"));
        var agent = Assert.Single(StoredAgent.All(application.Database));

        // The page of the agent holds what is stored, and offers the symbols of its market.
        var editor = await client.PageAsync(AgentBrowser.AgentPage(agent.Id));

        Assert.Contains("<h1>Settings of the agent Swing</h1>", editor);
        Assert.Equal(("Swing", "1000", "1.5", "4"), (ValueOf(editor, "Name"), ValueOf(editor, "AllocatedCapital"), ValueOf(editor, "RiskPerTradePercent"), ValueOf(editor, "LeverageCap")));
        Assert.Equal(("1.75", "2.5"), (ValueOf(editor, "MinRewardToRisk"), ValueOf(editor, "LiquidationBuffer")));
        Assert.Contains("<option value=\"Spot\" selected>Spot</option>", editor);
        Assert.Contains("<option value=\"SOLUSDT\" selected>", editor);
        Assert.Contains("<option value=\"1H\" selected>", editor);
        Assert.Contains("<button type=\"submit\">Save the agent</button>", editor);

        // A draft is changed in everything: its market, its symbol, and its timeframe as well.
        clock.Set(Morning.AddMinutes(20));
        var (path, list) = await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(
            name: "Position",
            market: "Futures",
            symbol: "BTCUSDT",
            timeframe: "1D",
            capital: "300.25",
            riskPerTrade: "0.5",
            minRewardToRisk: "1.25",
            leverageCap: "8",
            liquidationBuffer: "4",
            dailyLoss: "2",
            drawdown: "10",
            entryDistance: "0.2"));

        Assert.Equal(AgentsPage, path);
        Assert.Contains($"<a href=\"/agents/{agent.Id}\">Position</a>", list);

        // The same row, with the new settings. It is the agent it was: a draft in Demo, created when it was.
        Assert.Equal(
            agent with
            {
                Name = "Position",
                Market = "Futures",
                Symbol = "BTCUSDT",
                Timeframe = "OneDay",
                AllocatedCapital = 300.25m,
                RiskPerTradePercent = 0.5m,
                MinRewardToRisk = 1.25m,
                LeverageCap = 8,
                LiquidationBuffer = 4m,
                DailyLossPercent = 2m,
                DrawdownPercent = 10m,
                MaxEntryDistancePercent = 0.2m,
            },
            Assert.Single(StoredAgent.All(application.Database)));
    }

    [Theory]
    [InlineData(AgentState.Running)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Halted)]
    [InlineData(AgentState.AttentionRequired)]
    public async Task The_market_the_symbol_and_the_timeframe_of_an_active_agent_cannot_be_changed(AgentState state)
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The test stores an agent that is active: what holds here holds in every such state,
        // also in the ones that nothing leads to yet.
        var id = await application.GiveAgentAsync(user.Id, Morning, name: "Active", state: state);
        var stored = Row(application, id);

        // The page shows the three as they are, says why, and offers no choice of them.
        var editor = await client.PageAsync(AgentBrowser.AgentPage(id));

        Assert.Contains("<p>Market: <strong>Spot</strong>. Symbol: <strong>BTCUSDT</strong>. Timeframe: <strong>1H</strong>.</p>", editor);
        Assert.Contains($"<p role=\"note\">{AgentPages.StructureLocked}</p>", editor);
        Assert.All(
            new[] { "agent-market", "agent-symbol", "agent-timeframe" },
            choice => Assert.DoesNotContain($"<select id=\"{choice}\"", editor));

        // The models of its roles are offered all the same: they change in any state (FR-AGT-8).
        Assert.Contains("<select id=\"agent-strategist-model\"", editor);

        // A form that brings another market, symbol, or timeframe all the same is refused on
        // the server, each of them, and nothing of it is stored, the rest of the form included.
        (string Name, string Value)[][] changes =
        [
            AgentBrowser.Entry(name: "Renamed", market: "Futures"),
            AgentBrowser.Entry(name: "Renamed", symbol: "SOLUSDT"),
            AgentBrowser.Entry(name: "Renamed", timeframe: "4H"),
        ];

        foreach (var change in changes)
        {
            var (path, answer) = await client.SaveAgentAsync(id, change);

            Assert.Equal(AgentBrowser.AgentPage(id), path);
            Assert.Contains($"<p class=\"form-error\" role=\"alert\">Nothing was saved. {AgentPages.StructureLocked}</p>", answer);
            Assert.Equal(stored, Row(application, id));
        }

        // Its name, its capital, and its risk settings are changed while it is active: a change
        // to them applies from its next cycle.
        Assert.Equal(AgentsPage, (await client.SaveAgentAsync(id, AgentBrowser.Entry(name: "Renamed", capital: "400", riskPerTrade: "3"))).Path);

        var changed = StoredAgent.Named(application.Database, "Renamed");
        Assert.Equal((400m, 3m, state.ToString(), "Spot", "BTCUSDT", "OneHour"), (changed.AllocatedCapital, changed.RiskPerTradePercent, changed.State, changed.Market, changed.Symbol, changed.Timeframe));
    }

    [Fact]
    public async Task The_market_the_symbol_and_the_timeframe_of_a_stopped_agent_change_only_while_it_has_no_open_position_or_orders()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var holding = await application.GiveAgentAsync(user.Id, Morning, name: "Kept its position", state: AgentState.Stopped);
        var flat = await application.GiveAgentAsync(user.Id, Morning.AddMinutes(1), name: "Closed", state: AgentState.Stopped, symbol: "SOLUSDT");
        exposure.Holds(holding);

        // The stopped agent that still has a position: shown as it is, and refused on the server.
        var stored = Row(application, holding);

        Assert.Contains(AgentPages.StructureLocked, await client.PageAsync(AgentBrowser.AgentPage(holding)));

        var (path, answer) = await client.SaveAgentAsync(holding, AgentBrowser.Entry(name: "Kept its position", timeframe: "1D"));

        Assert.Equal(AgentBrowser.AgentPage(holding), path);
        Assert.Contains($"Nothing was saved. {AgentPages.StructureLocked}", answer);
        Assert.Equal(stored, Row(application, holding));

        // The stopped agent that has none is changed like a draft.
        Assert.Contains("<select id=\"agent-market\"", await client.PageAsync(AgentBrowser.AgentPage(flat)));
        Assert.Equal(
            AgentsPage,
            (await client.SaveAgentAsync(flat, AgentBrowser.Entry(name: "Closed", market: "Futures", symbol: "BTCUSDT", timeframe: "15m"))).Path);

        var changed = StoredAgent.Named(application.Database, "Closed");
        Assert.Equal(("Futures", "BTCUSDT", "FifteenMinutes", "Stopped"), (changed.Market, changed.Symbol, changed.Timeframe, changed.State));
    }

    [Fact]
    public async Task A_user_copies_an_agent_and_the_copy_is_a_draft_in_Demo_under_a_name_of_its_own()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(
            new StandInMarketData(), clock, settings: file => file.With("Limits:MaxAgentsPerUser", 20));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(
            name: "Swing", market: "Futures", timeframe: "4H", capital: "640", riskPerTrade: "3", leverageCap: "7", dailyLoss: "9", drawdown: "35", entryDistance: "1.5"));
        var swing = Assert.Single(StoredAgent.All(application.Database));

        // The list has a button for each agent, named after it.
        Assert.Contains("<button type=\"submit\">Copy Swing</button>", await client.PageAsync(AgentsPage));

        // The answer is the page of the copy, where it is given a name of its own.
        clock.Set(Morning.AddMinutes(10));
        var (path, editor) = await client.CloneAgentAsync(swing.Id);

        var copy = StoredAgent.Named(application.Database, "Copy of Swing");

        Assert.Equal(AgentBrowser.AgentPage(copy.Id), path);
        Assert.Contains("<h1>Settings of the agent Copy of Swing</h1>", editor);

        // It has the settings of the agent, and is an agent of its own: created now.
        Assert.Equal(
            swing with { Id = copy.Id, Name = "Copy of Swing", StateChangedAt = Morning.AddMinutes(10), CreatedAt = Morning.AddMinutes(10) },
            copy);
        Assert.NotEqual(swing.Id, copy.Id);

        // And a history of its own, which starts now: none of the changes of the agent it was
        // made from is its own.
        Assert.Equal(
            [("Draft", "Created", Morning.AddMinutes(10))],
            StoredAgentStateChange.Of(application.Database, copy.Id).Select(change => (change.State, change.Cause, change.ChangedAt)));

        // A second copy of the same agent has a name of its own as well.
        await client.CloneAgentAsync(swing.Id);
        await client.CloneAgentAsync(swing.Id);

        Assert.Equal(
            ["Swing", "Copy of Swing", "Copy of Swing (2)", "Copy of Swing (3)"],
            StoredAgent.All(application.Database).Select(agent => agent.Name));

        // A copy of an agent that is not a draft is a draft all the same, in Demo.
        var running = await application.GiveAgentAsync(user.Id, Morning.AddMinutes(20), name: "Running", state: AgentState.Running, symbol: "SOLUSDT");
        clock.Set(Morning.AddMinutes(30));
        await client.CloneAgentAsync(running);

        var draft = StoredAgent.Named(application.Database, "Copy of Running");
        Assert.Equal(("Draft", "Created", "Demo", "SOLUSDT", Morning.AddMinutes(30)), (draft.State, draft.StateCause, draft.Mode, draft.Symbol, draft.StateChangedAt));

        // A name is cut to the length of a name, with the number that tells the copies apart.
        var longName = new string('n', AgentRules.MaximumNameLength);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: longName));
        var longest = StoredAgent.Named(application.Database, longName);
        await client.CloneAgentAsync(longest.Id);
        await client.CloneAgentAsync(longest.Id);

        var copies = StoredAgent.All(application.Database).Select(agent => agent.Name).Where(name => name.Contains("nnn", StringComparison.Ordinal)).ToList();
        Assert.Equal([longName, $"Copy of {new string('n', 52)}", $"Copy of {new string('n', 48)} (2)"], copies);
        Assert.All(copies, name => Assert.True(name.Length <= AgentRules.MaximumNameLength));
    }

    [Fact]
    public async Task Creating_an_agent_beyond_the_most_an_account_can_have_is_refused()
    {
        const string limit = "You have 2 agents, which is the most one account can have. Delete one to create another.";

        using var application = AgentsApplication.With(
            new StandInMarketData(), new TestClock(Morning), settings: file => file.With("Limits:MaxAgentsPerUser", 2));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "First"))).Path);
        var (_, list) = await client.CreateAgentAsync(AgentBrowser.Entry(name: "Second"));

        // The list says that the account is full, and no longer leads to the form.
        Assert.Contains("<p>You have 2 of 2 agents.</p>", list);
        Assert.Contains($"<p role=\"status\">{limit}</p>", list);
        Assert.DoesNotContain("Create an agent", list);

        // The page of the form says the same, and has no form.
        var page = await client.PageAsync(NewAgentPage);

        Assert.Contains($"<p role=\"status\">{limit}</p>", page);
        Assert.DoesNotContain("name=\"Name\"", page);

        // A form that is posted all the same creates nothing, whatever it brings.
        var token = Browser.AntiforgeryTokenIn(page);
        var (path, answer) = await client.PostAsync(NewAgentPage, AgentBrowser.SaveForm, token, AgentBrowser.Entry(name: "Third"));
        var (_, unread) = await client.PostAsync(NewAgentPage, AgentBrowser.SaveForm, token, AgentBrowser.Entry(name: "", capital: "x"));

        Assert.Equal(NewAgentPage, path);
        Assert.Contains(limit, answer);
        Assert.Contains(limit, unread);
        Assert.DoesNotContain("class=\"field-error\"", unread);

        // Nor does a copy, which is one more agent: the list says why.
        var first = StoredAgent.Named(application.Database, "First");
        var (copiedAt, notCopied) = await client.CloneAgentAsync(first.Id);

        Assert.Equal(AgentsPage, copiedAt);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{limit}</p>", notCopied);
        Assert.Equal(["First", "Second"], StoredAgent.Of(application.Database, user.Id).Select(agent => agent.Name));

        // The limit is on what a user has: once an agent is deleted, another is created.
        await client.DeleteAgentAsync(first.Id);

        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: "Third"))).Path);
        Assert.Equal(["Second", "Third"], StoredAgent.Of(application.Database, user.Id).Select(agent => agent.Name));
    }

    [Fact]
    public async Task A_user_whose_email_address_is_not_verified_creates_no_agent()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        var user = StoredUser.Of(application.Database, Ada)!;

        // Such a user can sign in. The list says why they create no agent, and leads to no form.
        await client.SignInAsync(Ada);
        var list = await client.PageAsync(AgentsPage);

        Assert.Contains($"<p role=\"status\">{AgentPages.VerifyFirst}</p>", list);
        Assert.DoesNotContain("Create an agent", list);

        // The page of the form says the same, and has no form.
        var page = await client.PageAsync(NewAgentPage);

        Assert.Contains($"<p role=\"status\">{AgentPages.VerifyFirst}</p>", page);
        Assert.DoesNotContain("name=\"Name\"", page);

        // The form is posted all the same, as something that is not a page of the application
        // can. The check is in the action: nothing is created, and the answer says why.
        var (path, answer) = await client.PostAsync(NewAgentPage, AgentBrowser.SaveForm, Browser.AntiforgeryTokenIn(page), AgentBrowser.Entry());

        Assert.Equal(NewAgentPage, path);
        Assert.Contains(AgentPages.VerifyFirst, answer);
        Assert.Empty(StoredAgent.All(application.Database));

        var refused = Assert.Single(application.Events(), written => written.Template == NotVerifiedTemplate);
        Assert.Equal(user.Id.ToString(), refused.Text("UserId"));

        // Once the address is verified, the same form creates the agent.
        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[^1])));
        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry())).Path);
        Assert.Single(StoredAgent.Of(application.Database, user.Id));
    }

    [Fact]
    public async Task A_user_deletes_an_agent_for_good()
    {
        const string button = "<button type=\"submit\">Delete the agent</button>";

        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Scalp", symbol: "SOLUSDT"));
        var swing = StoredAgent.Named(application.Database, "Swing");

        // The list leads to a page that asks once more, and offers to keep the agent.
        Assert.Contains($"<a href=\"/agents/{swing.Id}/delete\">Delete Swing</a>", await client.PageAsync(AgentsPage));

        var (address, page) = await client.OpenAsync(AgentBrowser.DeleteAgentPage(swing.Id));

        Assert.Equal(AgentBrowser.DeleteAgentPage(swing.Id), address);
        Assert.Contains("<h1>Delete the agent Swing</h1>", page);
        Assert.Contains("at once and for good", page);
        Assert.Contains(button, page);
        Assert.Contains("<a href=\"/agents\">Keep the agent</a>", page);
        Assert.Equal(2, StoredAgent.All(application.Database).Count);

        // The button deletes the agent, and no other. The answer is the list.
        var (path, list) = await client.DeleteAgentAsync(swing.Id);

        Assert.Equal(AgentsPage, path);
        Assert.DoesNotContain("Swing", list);
        Assert.Equal(["Scalp"], StoredAgent.All(application.Database).Select(agent => agent.Name));
        Assert.DoesNotContain(swing.Id.ToString(), application.EverythingStored());

        // The changes of its state went with it, and those of the other agent are there.
        Assert.Equal(
            [StoredAgent.Named(application.Database, "Scalp").Id],
            StoredAgentStateChange.All(application.Database).Select(change => change.AgentId));

        // A form that is sent twice is answered as the first one was, and deletes nothing more.
        var (again, _) = await client.PostAsync(
            AgentBrowser.DeleteAgentPage(swing.Id), AgentBrowser.DeleteForm, Browser.AntiforgeryTokenIn(list));

        Assert.Equal(AgentsPage, again);
        Assert.Equal(["Scalp"], StoredAgent.All(application.Database).Select(agent => agent.Name));

        // Its page is not there any more.
        using var gone = await client.GetAsync(AgentBrowser.DeleteAgentPage(swing.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // An agent that is active is stopped first: the page says so and has no button, and a
        // form that is posted all the same deletes nothing.
        var running = await application.GiveAgentAsync(user.Id, Morning, name: "Running", state: AgentState.Running);
        var notStopped = await client.PageAsync(AgentBrowser.DeleteAgentPage(running));

        Assert.Contains("<p role=\"status\">This agent is not stopped. Stop it before you delete it.</p>", notStopped);
        Assert.DoesNotContain(button, notStopped);

        var (stayedAt, refused) = await client.PostAsync(
            AgentBrowser.DeleteAgentPage(running), AgentBrowser.DeleteForm, Browser.AntiforgeryTokenIn(notStopped));

        Assert.Equal(AgentBrowser.DeleteAgentPage(running), stayedAt);
        Assert.Contains("<p class=\"form-error\" role=\"alert\">This agent is not stopped. Stop it before you delete it.</p>", refused);

        // Nor is a stopped agent that still has a position or orders.
        var holding = await application.GiveAgentAsync(user.Id, Morning, name: "Kept its position", state: AgentState.Stopped, market: Market.Futures);
        exposure.Holds(holding);
        var (_, kept) = await client.DeleteAgentAsync(holding);

        Assert.Contains("This agent has an open position or open orders. Close them before you delete it.", kept);
        Assert.Equal(["Kept its position", "Running", "Scalp"], StoredAgent.All(application.Database).Select(agent => agent.Name).Order());

        // A stopped agent that has none is deleted like a draft.
        var stopped = await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: AgentState.Stopped, symbol: "SOLUSDT");

        Assert.Equal(AgentsPage, (await client.DeleteAgentAsync(stopped)).Path);
        Assert.DoesNotContain(StoredAgent.All(application.Database), agent => agent.Name == "Stopped");
    }

    [Fact]
    [ProvesOwnershipOf("agents")]
    public async Task The_agents_of_a_user_are_out_of_reach_of_another_user()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);

        // Grace has two agents.
        var grace = await application.RegisterVerifiedAsync(Grace);
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's swing"));
        await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's scalp", market: "Futures", timeframe: "15m"));
        var gracesAgent = StoredAgent.Named(application.Database, "Grace's swing");

        var before = application.RowsOf(grace.Id);
        Assert.Equal(2, before["agents"].Count);

        // Ada sees none of them: her list is empty.
        clock.Set(Morning.AddMinutes(5));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var list = await client.PageAsync(AgentsPage);

        Assert.Contains("<p>You have no agent yet.</p>", list);
        Assert.Contains("<p>You have 0 of 7 agents.</p>", list);
        Assert.DoesNotContain("Grace", AgentBrowser.Read(list));

        // The pages of an agent of Grace are not found for her, as those of an agent that does
        // not exist are not: nothing tells her that the id is someone's.
        foreach (var page in new[] { AgentBrowser.AgentPage(gracesAgent.Id), AgentBrowser.DeleteAgentPage(gracesAgent.Id) })
        {
            using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);
            var shown = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Grace", AgentBrowser.Read(shown));
        }

        // A form that is posted at them all the same changes nothing. The page that says "not
        // found" has no form that saves, so that answer is the one of a form which no page has.
        var token = Browser.AntiforgeryTokenIn(list);

        using (var saved = await client.SendFormAsync(
            AgentBrowser.AgentPage(gracesAgent.Id), AgentBrowser.SaveForm, token, AgentBrowser.Entry(name: "Taken over", capital: "1")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        }

        // Deleting is answered as for an agent that is gone already, and deletes nothing.
        Assert.Equal(AgentsPage, (await client.PostAsync(AgentBrowser.DeleteAgentPage(gracesAgent.Id), AgentBrowser.DeleteForm, token)).Path);

        // Her list has no form that copies an agent of Grace.
        using (var copied = await client.SendFormAsync(AgentsPage, AgentBrowser.CloneForm(gracesAgent.Id), token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, copied.StatusCode);
        }

        // What she creates, changes, copies, and deletes is her own, also under the names that Grace uses.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's swing"));
        var own = Assert.Single(StoredAgent.Of(application.Database, ada.Id));
        await client.SaveAgentAsync(own.Id, AgentBrowser.Entry(name: "Ada's swing", capital: "5"));
        await client.CloneAgentAsync(own.Id);
        await client.DeleteAgentAsync(own.Id);

        Assert.Equal(["Copy of Ada's swing"], StoredAgent.Of(application.Database, ada.Id).Select(agent => agent.Name));

        // Her pages and her download show her agent, and nothing of Grace's.
        var pages = new[] { await client.PageAsync(AgentsPage), await client.PageAsync("/account/export") };
        Assert.All(pages, page =>
        {
            Assert.Contains("Copy of Ada", AgentBrowser.Read(page).Replace("\\u0027", "'"));
            Assert.DoesNotContain("Grace", AgentBrowser.Read(page).Replace("\\u0027", "'"));
            Assert.DoesNotContain(gracesAgent.Id.ToString(), page);
            Assert.DoesNotContain(grace.Id.ToString(), page);
        });

        // And every row of Grace is as it was, to the last column.
        Assert.Equal(TextOf(before), TextOf(application.RowsOf(grace.Id)));
        Assert.Contains("Grace's swing", AgentBrowser.Read(await gracesBrowser.PageAsync(AgentsPage)));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Theory]
    [InlineData("/agents")]
    [InlineData("/agents/new")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/delete")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/state")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/prompts")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/risk-checks")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/stop")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/close-position")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/live")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/demo")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/confirm/start")]
    public async Task A_visitor_who_opens_its_pages_is_sent_to_sign_in(string page)
    {
        using var application = AgentsApplication.With(new StandInMarketData());
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    [Fact]
    public async Task A_visitor_who_posts_its_forms_is_sent_to_sign_in_and_changes_nothing()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), exposure: exposure);
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        var agent = await application.GiveAgentAsync(user.Id, Morning);
        var running = await application.GiveAgentAsync(user.Id, Morning, name: "Running", state: AgentState.Running, symbol: "SOLUSDT");
        exposure.Holds(running);
        var stored = application.RowsOf(user.Id);

        using var client = application.CreateClient();
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath));

        (string Path, string Form, (string Name, string Value)[] Fields)[] forms =
        [
            (NewAgentPage, AgentBrowser.SaveForm, AgentBrowser.Entry()),
            (AgentBrowser.AgentPage(agent), AgentBrowser.SaveForm, AgentBrowser.Entry(name: "Renamed")),
            (AgentsPage, AgentBrowser.CloneForm(agent), []),
            (AgentBrowser.DeleteAgentPage(agent), AgentBrowser.DeleteForm, []),
            (AgentBrowser.AgentStatePage(agent), AgentBrowser.StartForm, []),
            (AgentBrowser.AgentStatePage(running), AgentBrowser.PauseForm, []),
            (AgentBrowser.AgentStatePage(running), AgentBrowser.ResumeForm, []),
            (AgentBrowser.StopAgentPage(running), AgentBrowser.StopForm, [("Position", AgentPages.ClosePosition)]),
            (AgentBrowser.ClosePositionPage(running), AgentBrowser.ClosePositionForm, []),
            (AgentBrowser.SwitchToLivePage(agent), AgentBrowser.SwitchToLiveForm, [AgentBrowser.RiskConfirmed]),
            (AgentBrowser.SwitchToDemoPage(agent), AgentBrowser.SwitchToDemoForm, []),
            (AgentBrowser.ConfirmControlPage(agent, "start"), AgentBrowser.StartForm, []),
            (AgentBrowser.ConfirmControlPage(running, "pause"), AgentBrowser.PauseForm, []),
        ];

        foreach (var (path, form, fields) in forms)
        {
            Assert.Equal(SessionAuthentication.SignInPath, (await client.PostAsync(path, form, token, fields)).Path);
        }

        // Nothing was created, changed, started, stopped, or closed.
        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);
        Assert.Equal(2, StoredAgent.All(application.Database).Count);
        Assert.Empty(exposure.Closes);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_changes_nothing()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), exposure: exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry());
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var running = await application.GiveAgentAsync(user.Id, Morning, name: "Running", state: AgentState.Running, symbol: "SOLUSDT");
        exposure.Holds(running);
        var stored = application.RowsOf(user.Id);

        // As another site would send them, with the cookies of the user and without the token
        // of a page: each is refused, whatever it asks for.
        (string Path, string Form, (string Name, string Value)[] Fields)[] forms =
        [
            (NewAgentPage, AgentBrowser.SaveForm, AgentBrowser.Entry(name: "Another")),
            (AgentBrowser.AgentPage(agent.Id), AgentBrowser.SaveForm, AgentBrowser.Entry(name: "Renamed", riskPerTrade: "4")),
            (AgentsPage, AgentBrowser.CloneForm(agent.Id), []),
            (AgentBrowser.DeleteAgentPage(agent.Id), AgentBrowser.DeleteForm, []),
            (AgentBrowser.AgentStatePage(agent.Id), AgentBrowser.StartForm, []),
            (AgentBrowser.AgentStatePage(running), AgentBrowser.PauseForm, []),
            (AgentBrowser.AgentStatePage(running), AgentBrowser.ResumeForm, []),
            (AgentBrowser.StopAgentPage(running), AgentBrowser.StopForm, [("Position", AgentPages.ClosePosition)]),
            (AgentBrowser.ClosePositionPage(running), AgentBrowser.ClosePositionForm, []),
            (AgentBrowser.SwitchToLivePage(agent.Id), AgentBrowser.SwitchToLiveForm, [AgentBrowser.RiskConfirmed]),
            (AgentBrowser.SwitchToDemoPage(agent.Id), AgentBrowser.SwitchToDemoForm, []),
            (AgentBrowser.ConfirmControlPage(agent.Id, "start"), AgentBrowser.StartForm, []),
            (AgentBrowser.ConfirmControlPage(running, "pause"), AgentBrowser.PauseForm, []),
            (AgentBrowser.TestCyclePage(running), AgentBrowser.TestCycleForm, []),
        ];

        foreach (var (path, form, fields) in forms)
        {
            using var answer = await client.SendFormAsync(path, form, "not-the-token", fields);

            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }

        // No agent was started or stopped by another site, no position closed, and no test
        // cycle run on the keys of the user.
        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);
        Assert.Empty(application.RowsOf(user.Id)["decision_cycles"]);
        Assert.Empty(exposure.Closes);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/delete")]
    [InlineData("/agents/swing")]
    [InlineData("/agents/swing/delete")]
    [InlineData("/agents/1")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/state")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/prompts")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/risk-checks")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/stop")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/close-position")]
    [InlineData("/agents/swing/state")]
    [InlineData("/agents/swing/prompts")]
    [InlineData("/agents/swing/risk-checks")]
    [InlineData("/agents/swing/stop")]
    [InlineData("/agents/swing/close-position")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/live")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/demo")]
    [InlineData("/agents/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37/confirm/start")]
    [InlineData("/agents/swing/live")]
    [InlineData("/agents/swing/demo")]
    [InlineData("/agents/swing/confirm/start")]
    public async Task A_path_that_names_no_agent_of_the_user_is_not_found(string page)
    {
        using var application = AgentsApplication.With(new StandInMarketData());
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry());

        using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);
        var shown = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("name=\"Name\"", shown);
        Assert.DoesNotContain("Delete the agent", shown);
        Assert.DoesNotContain("the agent</button>", shown);
        Assert.DoesNotContain(AgentPages.ClosePositionNow, shown);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.AgentRiskSettingsChanged)]
    public async Task Changing_a_risk_setting_of_an_agent_is_recorded_in_the_audit_trail_by_the_id_of_the_agent()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Creating an agent sets its risk settings for the first time, and changes none.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        var agent = Assert.Single(StoredAgent.All(application.Database));

        // Nor does a change to anything else, or a form that brings the settings as they are,
        // however the numbers are written.
        clock.Set(Morning.AddMinutes(1));
        await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "Renamed", capital: "50", timeframe: "4H"));
        await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "Renamed", riskPerTrade: "1.50", liquidationBuffer: "2.50", dailyLoss: "5.0", entryDistance: "0.80"));

        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        // A change to each of the seven is recorded, once for each form that was saved.
        (string Name, string Value)[][] changes =
        [
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5", dailyLoss: "6"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5", dailyLoss: "6", drawdown: "25"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5", dailyLoss: "6", drawdown: "25", entryDistance: "1"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5", dailyLoss: "6", drawdown: "25", entryDistance: "1", minRewardToRisk: "2"),
            AgentBrowser.Entry(name: "Renamed", riskPerTrade: "2", leverageCap: "5", dailyLoss: "6", drawdown: "25", entryDistance: "1", minRewardToRisk: "2", liquidationBuffer: "3"),
        ];

        for (var change = 0; change < changes.Length; change++)
        {
            clock.Set(Morning.AddMinutes(10 + change));
            Assert.Equal(AgentsPage, (await client.SaveAgentAsync(agent.Id, changes[change])).Path);
        }

        // A change that is refused is not one, and neither are copying and deleting the agent.
        clock.Set(Morning.AddMinutes(30));
        await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "Renamed", riskPerTrade: "40"));
        await client.CloneAgentAsync(agent.Id);
        await client.DeleteAgentAsync(agent.Id);

        // One event for each change, in order, with the user, the agent by its id, the time,
        // and where the request came from.
        var events = StoredAuditEvent.All(application.Database)
            .Where(recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [.. Enumerable.Range(0, changes.Length).Select(change => $"AgentRiskSettingsChanged agent:{agent.Id} {Morning.AddMinutes(10 + change):O}")],
            events.Select(recorded => $"{recorded.Action} {recorded.Target} {recorded.OccurredAt:O}"));
        Assert.All(events, recorded => Assert.Equal((user.Id, "203.0.113.7"), (recorded.UserId, recorded.Origin)));

        // The trail holds the id of the agent, and neither its name nor a number of its settings.
        var trail = string.Join('\n', application.RowsOf(user.Id)["audit_events"]);
        Assert.DoesNotContain("Renamed", trail);

        // The log tells the same in its own words, without the name of the action.
        Assert.Equal(
            changes.Length,
            application.Events().Count(written => written.Template == "User {UserId} changed the risk settings of agent {AgentId}"));
        Assert.DoesNotContain(application.Events(), written => written.ToString().Contains("AgentRiskSettingsChanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_a_user_named_an_agent_is_shown_as_text_and_never_as_markup()
    {
        const string name = "<img/src=x/onerror=alert(1)>";

        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal(AgentsPage, (await client.CreateAgentAsync(AgentBrowser.Entry(name: name))).Path);
        var agent = Assert.Single(StoredAgent.All(application.Database));
        Assert.Equal(name, agent.Name);

        // The list, the page of the agent, the page that deletes it, and the pages of its state,
        // as the browser gets them.
        string[] pages =
        [
            await client.PageAsync(AgentsPage),
            await client.PageAsync(AgentBrowser.AgentPage(agent.Id)),
            await client.PageAsync(AgentBrowser.DeleteAgentPage(agent.Id)),
            await client.PageAsync(AgentBrowser.AgentStatePage(agent.Id)),
            await client.PageAsync(AgentBrowser.StopAgentPage(agent.Id)),
            await client.PageAsync(AgentBrowser.ClosePositionPage(agent.Id)),
        ];

        Assert.All(pages, page =>
        {
            Assert.DoesNotContain("<img/src", page);
            Assert.Contains("&lt;img/src=x/onerror=alert(1)&gt;", page);
        });
    }

    [Fact]
    public async Task The_bar_at_the_top_leads_a_signed_in_user_to_their_agents()
    {
        // The layout marks its elements for its own styles, so the link carries more than its address.
        const string link = "<a href=\"/agents\"[^>]*>Agents</a>";

        using var application = AgentsApplication.With(new StandInMarketData());
        using var client = application.CreateClient();

        // A visitor could not open it, and is not shown the way.
        Assert.DoesNotMatch(link, await client.PageAsync("/"));

        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Matches(link, await client.PageAsync("/"));
        Assert.Matches(link, await client.PageAsync("/account"));
    }

    [Fact]
    public async Task The_download_holds_the_agents_of_the_user_with_their_settings()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(
            name: "Swing", market: "Futures", timeframe: "4H", capital: "640.5", riskPerTrade: "3", minRewardToRisk: "2.25", leverageCap: "7",
            liquidationBuffer: "3", dailyLoss: "9", drawdown: "35", entryDistance: "1.5"));
        var agent = Assert.Single(StoredAgent.All(application.Database));

        var export = JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement;
        var exported = Assert.Single(export.GetProperty("agents").EnumerateArray());

        Assert.Equal(
            [
                "id", "name", "mode", "market", "symbol", "timeframe", "allocatedCapital", "quoteAsset", "risk", "state",
                "stateCause", "stateChangedAt", "createdAt", "reassignsRoles", "rolesReassignedAt",
            ],
            exported.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            (agent.Id, "Swing", "Demo", "Futures", "BTCUSDT", "4H", 640.5m, "USDT", "Draft", "Created", Morning),
            (exported.GetProperty("id").GetGuid(),
                exported.GetProperty("name").GetString(),
                exported.GetProperty("mode").GetString(),
                exported.GetProperty("market").GetString(),
                exported.GetProperty("symbol").GetString(),
                exported.GetProperty("timeframe").GetString(),
                exported.GetProperty("allocatedCapital").GetDecimal(),
                exported.GetProperty("quoteAsset").GetString(),
                exported.GetProperty("state").GetString(),
                exported.GetProperty("stateCause").GetString(),
                exported.GetProperty("createdAt").GetDateTimeOffset()));

        var risk = exported.GetProperty("risk");
        Assert.Equal(
            [
                "riskPerTradePercent", "minRewardToRisk", "leverageCap", "liquidationBuffer", "dailyLossPercent", "drawdownPercent",
                "maxEntryDistancePercent",
            ],
            risk.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            (3m, 2.25m, 7, 3m, 9m, 35m, 1.5m),
            (risk.GetProperty("riskPerTradePercent").GetDecimal(),
                risk.GetProperty("minRewardToRisk").GetDecimal(),
                risk.GetProperty("leverageCap").GetInt32(),
                risk.GetProperty("liquidationBuffer").GetDecimal(),
                risk.GetProperty("dailyLossPercent").GetDecimal(),
                risk.GetProperty("drawdownPercent").GetDecimal(),
                risk.GetProperty("maxEntryDistancePercent").GetDecimal()));

        // Whether the service reassigns its roles by strength is a setting of the agent, and is
        // off unless its user turned it on. The service has reassigned none (FR-TEAM-10).
        Assert.False(exported.GetProperty("reassignsRoles").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exported.GetProperty("rolesReassignedAt").ValueKind);
    }

    [Fact]
    public async Task An_account_is_deleted_only_while_no_agent_of_its_user_is_active()
    {
        const string standsInTheWay = "One of your agents is not stopped. Stop every agent first, and then delete your account.";

        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // A draft was never started, and a stopped agent is stopped: neither stands in the way.
        await application.GiveAgentAsync(user.Id, Morning, name: "Draft");
        await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: AgentState.Stopped, symbol: "SOLUSDT");

        Assert.DoesNotContain(standsInTheWay, await client.PageAsync("/account/delete"));

        // An agent in any other state is active: the test stores one that is paused. The page
        // says what stands in the way, and a form that is posted all the same deletes nothing
        // (FR-ACC-9).
        var paused = await application.GiveAgentAsync(user.Id, Morning, name: "Paused", state: AgentState.Paused, market: Market.Futures);
        var page = await client.PageAsync("/account/delete");

        Assert.Contains(standsInTheWay, page);

        var (path, answer) = await client.PostAsync("/account/delete", "delete-account", Browser.AntiforgeryTokenIn(page));

        Assert.Equal("/account/delete", path);
        Assert.Contains(standsInTheWay, answer);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
        Assert.Equal(3, StoredAgent.All(application.Database).Count);

        // Once it is stopped, the account is deleted, and its agents with it.
        application.Database.Execute($"UPDATE agents SET state = 'Stopped' WHERE id = '{paused}'");

        Assert.Equal("/account/deleted", (await client.DeleteAccountAsync()).Path);
        Assert.Null(StoredUser.Of(application.Database, Ada));
        Assert.Empty(StoredAgent.All(application.Database));
    }

    /// <summary>The value that a page has in the field of a name, or null when the field has none.</summary>
    private static string? ValueOf(string page, string field)
    {
        var input = Regex.Match(page, $"<input[^>]* name=\"{Regex.Escape(field)}\"[^>]*>");
        Assert.True(input.Success, $"The page has no field {field}:{Environment.NewLine}{page}");

        var value = Regex.Match(input.Value, " value=\"([^\"]*)\"");

        return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : null;
    }

    /// <summary>What a page says is wrong, each at its field, in the order of the fields.</summary>
    private static IReadOnlyList<string> ErrorsOn(string page) =>
        [.. Regex.Matches(page, "<p class=\"field-error\" role=\"alert\">([^<]*)</p>").Select(error => WebUtility.HtmlDecode(error.Groups[1].Value))];

    /// <summary>Something of the one agent that is stored.</summary>
    private static T Stored<T>(AccountsApplication application, Func<StoredAgent, T> of) =>
        of(Assert.Single(StoredAgent.All(application.Database)));

    /// <summary>The row of an agent, as the database would hand it to whoever reads it.</summary>
    private static string Row(AccountsApplication application, Guid agentId) =>
        Assert.Single(application.Database.Column($"""SELECT row_to_json(stored)::text FROM agents stored WHERE id = '{agentId}'"""));
}
