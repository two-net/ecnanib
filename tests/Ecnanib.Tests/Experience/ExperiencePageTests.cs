using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The experience screens show the track record of each provider and of each model, filterable
/// by role, market, timeframe, mode, and period, with closed trades, win rate, average R, total
/// R, profit factor, largest drawdown in R, and average holding time (FR-EXP-2). Demo and Live
/// are kept apart, and a screen that is not told a mode shows Live where there is enough,
/// otherwise Demo, and says which (FR-EXP-9). See "The screens" in docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests takes 3 trades as the minimum sample. The clock stands at the morning
/// of the tests, and the trades were closed before it.
/// </remarks>
public class ExperiencePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Gemini = "gemini-test-model";

    private const string ByProvider = "The track record of each provider in Demo";
    private const string ByModel = "The track record of each model in Demo";
    private const string ByRole = "The track record of the model in Demo, by the role that it held";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_screen_shows_the_track_record_of_each_provider_and_of_each_model()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A model of Gemini is the Strategist of the first two trades, and a model of Claude
        // holds every other role, and every role of the third.
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning, displayName: "Claude Test");
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", Gemini);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 2m, holdingSeconds: 3600);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), resultInR: -1m, holdingSeconds: 1800);
        await application.GiveTradeAsync(
            user.Id,
            agent,
            Morning.AddHours(-3),
            resultInR: 1.5m,
            holdingSeconds: 7200,
            roles: [.. Enum.GetValues<Role>().Select(role => (role, claude))]);

        var page = await client.PageAsync(ExperienceBrowser.Screen);
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Experience</title>", page);
        Assert.Contains("<h1>Experience</h1>", page);
        Assert.Contains("Every figure is in R: the amount that was risked on a trade", read);

        // Claude: 3 trades that made 2, lost 1, and made 1.5. The total ran 2, 1, 2.5, so it
        // fell by 1 at the most. Gemini: the first two of them, which is a small sample here.
        Assert.Equal(["Provider", .. ExperienceBrowser.Measures], ExperienceBrowser.ColumnsOf(page, ByProvider));
        Assert.Equal(
            [
                ["Claude", "3", "66.7 percent", "+0.83 R", "+2.5 R", "3.5", "1 R", "1 hour 10 minutes", "None"],
                ["Gemini", "2", "50 percent", "+0.5 R", "+1 R", "2", "1 R", "45 minutes", "Small sample."],
            ],
            ExperienceBrowser.RowsOf(page, ByProvider));

        // Each model by the name that its provider gives it, or by its identifier, with its provider.
        Assert.Equal(["Model", "Provider", .. ExperienceBrowser.Measures], ExperienceBrowser.ColumnsOf(page, ByModel));
        Assert.Equal(
            [
                ["Claude Test", "Claude", "3", "66.7 percent", "+0.83 R", "+2.5 R", "3.5", "1 R", "1 hour 10 minutes", "None"],
                [Gemini, "Gemini", "2", "50 percent", "+0.5 R", "+1 R", "2", "1 R", "45 minutes", "Small sample."],
            ],
            ExperienceBrowser.RowsOf(page, ByModel));

        // It says for whom a trade counts, which trades it counts, and what a record follows.
        Assert.Contains("A trade counts once for every model of the team that decided it, and once for every provider of that team.", read);
        Assert.Contains("Counted are the trades of all time.", read);
        Assert.Contains("When you delete an agent, its positions are deleted with it, and its trades leave the record.", read);
        Assert.Contains("<a href=\"/catalog\">Model catalog</a>", page);
    }

    [Fact]
    public async Task A_screen_with_no_trade_says_so_and_has_no_table()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync(ExperienceBrowser.Screen);
        var read = AgentBrowser.Read(page);

        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", page);
        Assert.Equal(2, Regex.Count(read, "No closed trade is counted here in Demo\\."));
        Assert.DoesNotContain("<table", page);
        Assert.DoesNotContain("role=\"note\"", page);

        // The filter is there all the same.
        Assert.Contains("<form method=\"get\" action=\"/experience\"", page);
    }

    [Fact]
    public async Task Each_model_leads_to_its_page_with_the_same_filter_and_the_page_leads_back()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 2m);

        // With no filter, the link is the path of the model alone.
        Assert.Contains(
            $"<a href=\"/experience/models/{model}\">{StoredAgentRows.TeamModel}</a>", await client.PageAsync(ExperienceBrowser.Screen));

        // With one, the link keeps it, but for the role: the page of a model says every role.
        var filtered = await client.PageAsync($"{ExperienceBrowser.Screen}?role=strategist&market=spot&timeframe=1H&mode=demo&period=30");

        Assert.Contains(
            $"<a href=\"/experience/models/{model}?market=spot&amp;timeframe=1H&amp;mode=demo&amp;period=30\">{StoredAgentRows.TeamModel}</a>",
            filtered);

        var ofModel = await client.PageAsync($"{ExperienceBrowser.ModelPage(model)}?market=spot&timeframe=1H&mode=demo&period=30");

        Assert.Contains(
            "<a href=\"/experience?market=spot&amp;timeframe=1H&amp;mode=demo&amp;period=30\">Experience of every provider and model</a>", ofModel);
        Assert.Contains("<a href=\"/catalog\">Back to the model catalog</a>", ofModel);

        // Each leads to the other mode with the same filter, and names it as a word.
        Assert.Contains(
            "<a href=\"/experience?role=strategist&amp;market=spot&amp;timeframe=1H&amp;mode=live&amp;period=30\">Show your results in Live, with 0 closed trades</a>",
            filtered);
        Assert.Contains(
            $"<a href=\"/experience/models/{model}?market=spot&amp;timeframe=1H&amp;mode=live&amp;period=30\">Show your results in Live, with 0 closed trades</a>",
            ofModel);
    }

    [Theory]
    [InlineData("mode=both")]
    [InlineData("mode=Live")]
    [InlineData("mode=1")]
    [InlineData("market=1")]
    [InlineData("market=Spot")]
    [InlineData("timeframe=1h")]
    [InlineData("timeframe=OneHour")]
    [InlineData("period=31")]
    [InlineData("period=0")]
    [InlineData("period=all")]
    [InlineData("role=Strategist")]
    [InlineData("role=2")]
    [InlineData("mode=demo&mode=live")]
    [InlineData("mode=demo&mode=demo")]
    [InlineData("market=spot&period=7&mode=demo%20")]
    public async Task An_address_that_names_a_choice_which_there_is_not_is_not_found(string query)
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        foreach (var path in new[] { ExperienceBrowser.Screen, ExperienceBrowser.ModelPage(model) })
        {
            using var answer = await client.GetAsync($"{path}?{query}", TestContext.Current.CancellationToken);

            // Nothing else is shown in its place: neither Demo for a mode that there is not, nor
            // every trade for a period that there is not.
            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Mode:", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_choice_that_is_left_empty_is_not_made_and_the_form_starts_from_the_choices_of_the_address()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        static string[] Chosen(string page, params string[] names) => [.. names.Select(name => ExperienceBrowser.ChosenIn(page, name))];

        // The form sends every choice, also one that was left as it starts: that is no choice.
        var empty = await client.PageAsync($"{ExperienceBrowser.Screen}?role=&market=&timeframe=&mode=&period=");

        Assert.Equal(["", "", "", "", ""], Chosen(empty, "role", "market", "timeframe", "mode", "period"));
        Assert.Contains("These are your results in Demo: with 0 closed trades, Live has fewer than the 3 that are enough to go by.", empty);
        Assert.Contains("Counted are the trades of all time.", empty);

        // It asks for the page that shows it, by an address, so that nothing is changed by it.
        Assert.Contains("<form method=\"get\" action=\"/experience\"", empty);
        Assert.Contains("<button type=\"submit\">Show</button>", empty);

        var chosen = await client.PageAsync($"{ExperienceBrowser.Screen}?role=risk-manager&market=futures&timeframe=4H&mode=live&period=90");

        Assert.Equal(["risk-manager", "futures", "4H", "live", "90"], Chosen(chosen, "role", "market", "timeframe", "mode", "period"));
        Assert.Contains("These are your results in Live, which you chose.", chosen);
        Assert.Contains("Counted are the trades that were closed since 19 August 2026, 09:20 UTC.", chosen);
        Assert.Contains("A trade counts for the model that held the role of Risk Manager when its position was opened", chosen);

        // What each choice offers: no mode of both, and every role, market, timeframe, and period.
        Assert.Equal(["", "demo", "live"], ExperienceBrowser.ChoicesOf(chosen, "mode"));
        Assert.Equal(["", "7", "30", "90", "365"], ExperienceBrowser.ChoicesOf(chosen, "period"));
        Assert.Equal(["", "market-analyst", "sentiment-analyst", "strategist", "risk-manager"], ExperienceBrowser.ChoicesOf(chosen, "role"));
        Assert.Equal(["", "spot", "futures"], ExperienceBrowser.ChoicesOf(chosen, "market"));
        Assert.Equal(["", "15m", "1H", "4H", "1D"], ExperienceBrowser.ChoicesOf(chosen, "timeframe"));

        // The page of a model has the same form, for itself and without the role.
        var ofModel = await client.PageAsync($"{ExperienceBrowser.ModelPage(model)}?market=futures&timeframe=15m&mode=demo&period=7");

        Assert.Contains($"<form method=\"get\" action=\"/experience/models/{model}\"", ofModel);
        Assert.Equal(["futures", "15m", "demo", "7"], Chosen(ofModel, "market", "timeframe", "mode", "period"));
        Assert.DoesNotContain("name=\"role\"", ofModel);

        // What an address says besides is not a choice of the filter, and is not gone by.
        Assert.Equal(["", ""], Chosen(await client.PageAsync($"{ExperienceBrowser.Screen}?sort=total"), "mode", "period"));
    }

    [Fact]
    public async Task The_page_of_a_model_shows_its_record_in_any_role_and_in_each_role()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The model of Claude is both analysts and the Risk Manager of the first trade, and the
        // Strategist as well of the second.
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning, displayName: "Claude Test");
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var gemini = await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", Gemini);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 2m, holdingSeconds: 90_000);
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "claude", StoredAgentRows.TeamModel);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), resultInR: -0.5m, holdingSeconds: 93_600, complete: false);

        var page = await client.PageAsync(ExperienceBrowser.ModelPage(claude));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Experience of Claude Test</title>", page);
        Assert.Matches($@"<h1>Experience of Claude Test</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", page);
        Assert.Contains("A model of Claude, which names it <code>claude-test-model</code>.", read);
        Assert.Contains("A trade counts for this model when the model held a role of the team as the position was opened.", read);

        Assert.Equal(["Role", .. ExperienceBrowser.Measures], ExperienceBrowser.ColumnsOf(page, ByRole));
        Assert.Equal(
            [
                ["Any role", "2", "50 percent", "+0.75 R", "+1.5 R", "4", "0.5 R", "1 day 1 hour", "Small sample. 1 result is an estimate."],
                ["Market Analyst", "2", "50 percent", "+0.75 R", "+1.5 R", "4", "0.5 R", "1 day 1 hour", "Small sample. 1 result is an estimate."],
                ["Sentiment Analyst", "2", "50 percent", "+0.75 R", "+1.5 R", "4", "0.5 R", "1 day 1 hour", "Small sample. 1 result is an estimate."],
                ["Strategist", "1", "0 percent", "-0.5 R", "-0.5 R", "0", "0.5 R", "1 day 2 hours", "Small sample. 1 result is an estimate."],
                ["Risk Manager", "2", "50 percent", "+0.75 R", "+1.5 R", "4", "0.5 R", "1 day 1 hour", "Small sample. 1 result is an estimate."],
            ],
            ExperienceBrowser.RowsOf(page, ByRole));
        Assert.Contains("A result is an estimate when not all of it is what Binance said", read);

        // A role that the model never held says so, and no measure: there is nothing to measure.
        Assert.Equal(
            [
                ["Any role", "1", "100 percent", "+2 R", "+2 R", "No losing trade", "0 R", "1 day 1 hour", "Small sample."],
                ["Market Analyst", "0", "No closed trade is counted."],
                ["Sentiment Analyst", "0", "No closed trade is counted."],
                ["Strategist", "1", "100 percent", "+2 R", "+2 R", "No losing trade", "0 R", "1 day 1 hour", "Small sample."],
                ["Risk Manager", "0", "No closed trade is counted."],
            ],
            ExperienceBrowser.RowsOf(await client.PageAsync(ExperienceBrowser.ModelPage(gemini)), ByRole));
    }

    [Fact]
    public async Task The_page_of_a_model_that_no_trade_of_the_user_counts_for_says_so()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 2m);
        var unused = await application.GiveModelAsync("grok", "grok-test-model", Morning);

        // The catalog links every model to its page, so the page is there, and says that it has
        // nothing to show: it is not "not found".
        var page = await client.PageAsync(ExperienceBrowser.ModelPage(unused));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Experience of grok-test-model</title>", page);
        Assert.Contains("Nothing is recorded here about how this model has traded for you in Demo.", read);
        Assert.DoesNotContain("<table", page);
        Assert.Contains("Status in the catalog: Active", read);
        Assert.Contains("<a href=\"/catalog\">Back to the model catalog</a>", page);
    }

    [Fact]
    public async Task A_user_sees_nothing_of_the_trades_of_another_user_with_the_same_model()
    {
        using var application = Application();
        using var adasBrowser = application.CreateClient();
        using var gracesBrowser = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await adasBrowser.SignInAsync(Ada);
        await gracesBrowser.SignInAsync(Grace);

        // Both use the same model, which the catalog has once, for everyone.
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        await application.GiveTradeAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2)), Morning.AddHours(-5), resultInR: 2m);

        foreach (var hour in Enumerable.Range(1, 4))
        {
            await application.GiveTradeAsync(
                grace.Id,
                await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2), name: $"Agent {hour}", symbol: $"COIN{hour}USDT"),
                Morning.AddHours(-hour),
                resultInR: -1m);
        }

        static string[] TradesAndTotals(IReadOnlyList<string[]> rows) => [.. rows.Select(row => $"{row[^8]} {row[^5]}")];

        // On the screen each sees the record of their own trades.
        Assert.Equal(["1 +2 R"], TradesAndTotals(ExperienceBrowser.RowsOf(await adasBrowser.PageAsync(ExperienceBrowser.Screen), ByModel)));
        Assert.Equal(["4 -4 R"], TradesAndTotals(ExperienceBrowser.RowsOf(await gracesBrowser.PageAsync(ExperienceBrowser.Screen), ByModel)));

        // On the page of the model, which has one address for both, as well.
        var adas = ExperienceBrowser.RowsOf(await adasBrowser.PageAsync(ExperienceBrowser.ModelPage(model)), ByRole);
        var graces = ExperienceBrowser.RowsOf(await gracesBrowser.PageAsync(ExperienceBrowser.ModelPage(model)), ByRole);

        Assert.All(TradesAndTotals(adas), row => Assert.Equal("1 +2 R", row));
        Assert.All(TradesAndTotals(graces), row => Assert.Equal("4 -4 R", row));
    }

    [Fact]
    public async Task Demo_results_are_shown_as_Demo_and_Live_as_Live_and_never_together()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-9), resultInR: 1m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-8), resultInR: 1.5m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-7), resultInR: -3m, mode: TradingMode.Live);

        static string Totals(string page, TradingMode mode) =>
            string.Join(' ', ExperienceBrowser.RowsOf(page, $"The track record of each provider in {mode}").Select(row => $"{row[1]}: {row[4]}"));

        foreach (var (path, modelPath) in new[] { (ExperienceBrowser.Screen, false), (ExperienceBrowser.ModelPage(model), true) })
        {
            // Told a mode, a page shows that one, with its badge under the heading, and leads to
            // the other, which it names as a word: the page has one badge.
            var demo = await client.PageAsync($"{path}?mode=demo");

            Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} These are your results in Demo, which you chose\. ", demo);
            Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), demo);
            Assert.Contains("Results in Demo and results in Live are never added up.", demo);
            Assert.Contains($"<a href=\"{path}?mode=live\">Show your results in Live, with 1 closed trade</a>", demo);

            var live = await client.PageAsync($"{path}?mode=live");

            Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} These are your results in Live, which you chose\. ", live);
            Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Demo), live);
            Assert.Contains($"<a href=\"{path}?mode=demo\">Show your results in Demo, with 2 closed trades</a>", live);

            if (!modelPath)
            {
                // What was made in Demo is not set off against what was lost in Live.
                Assert.Equal("2: +2.5 R", Totals(demo, TradingMode.Demo));
                Assert.Equal("1: -3 R", Totals(live, TradingMode.Live));
                Assert.False(ExperienceBrowser.HasTable(demo, "The track record of each provider in Live"));
                Assert.False(ExperienceBrowser.HasTable(live, "The track record of each provider in Demo"));
            }

            // Told none, it shows Demo, because Live has fewer trades than are enough, and says so.
            var chosen = await client.PageAsync(path);

            Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", chosen);
            Assert.Contains("These are your results in Demo: with 1 closed trade, Live has fewer than the 3 that are enough to go by.", chosen);
            Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), chosen);
        }

        // With enough trades in Live, a page that is told no mode shows Live, and says why.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-6), resultInR: 1m, mode: TradingMode.Live);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 1m, mode: TradingMode.Live);

        foreach (var path in new[] { ExperienceBrowser.Screen, ExperienceBrowser.ModelPage(model) })
        {
            var chosen = await client.PageAsync(path);

            Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} ", chosen);
            Assert.Contains("These are your results in Live: with 3 closed trades, Live has the 3 that are enough to go by.", chosen);
            Assert.Contains($"<a href=\"{path}?mode=demo\">Show your results in Demo, with 2 closed trades</a>", chosen);
            Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Demo), chosen);
        }

        Assert.Equal("3: -1 R", Totals(await client.PageAsync(ExperienceBrowser.Screen), TradingMode.Live));
    }

    [Fact]
    public async Task Trades_that_are_not_counted_are_said_and_a_result_that_is_an_estimate_is_marked()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        foreach (var hour in Enumerable.Range(1, 3))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-10 - hour), resultInR: 1m);
        }

        // A position that was liquidated: its result is an estimate, and it is counted.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-9), resultInR: -4.126m, complete: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-8), resultInR: -1m, complete: false);

        // Two whose results are not recorded yet, and one whose result says no R.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-1), resultInR: null);

        foreach (var (path, caption) in new[] { (ExperienceBrowser.Screen, ByProvider), (ExperienceBrowser.ModelPage(model), ByRole) })
        {
            var page = await client.PageAsync(path);
            var row = ExperienceBrowser.RowsOf(page, caption)[0];

            // Five trades are counted, with the two estimates: a loss says its sign, and a figure
            // has two decimal places at the most.
            Assert.Equal(["5", "60 percent", "-0.43 R", "-2.13 R", "0.59", "5.13 R", "1 hour", "2 results are estimates."], row[^8..]);

            Assert.Contains(
                "<p role=\"note\">2 closed trades are not counted yet: their results are recorded some minutes after the close.</p>", page);
            Assert.Contains(
                "<p role=\"note\">1 closed trade is not counted: what was risked on it is not known, so its result has no R.</p>", page);
        }
    }

    [Fact]
    public async Task What_a_provider_wrote_is_shown_as_text_and_never_as_markup()
    {
        const string name = "<img/src=x/onerror=alert(1)>";

        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), team: false);
        var model = await application.GiveModelAsync("claude", "claude-named-model", Morning, displayName: name);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-1), resultInR: 1m, roles: [(Role.Strategist, model)]);

        foreach (var path in new[] { ExperienceBrowser.Screen, ExperienceBrowser.ModelPage(model) })
        {
            var page = await client.PageAsync(path);

            Assert.DoesNotContain("<img", page);
            Assert.Contains(name, AgentBrowser.Read(page));
        }
    }

    [Fact]
    public async Task The_bar_at_the_top_leads_a_signed_in_user_to_the_experience_screen()
    {
        // The layout marks its elements for its own styles, so the link carries more than its address.
        const string link = "<a href=\"/experience\"[^>]*>Experience</a>";

        using var application = Application();
        using var client = application.CreateClient();

        // A visitor could not open it, and is not shown the way.
        Assert.DoesNotMatch(link, await client.PageAsync("/"));

        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Matches(link, await client.PageAsync("/"));
        Assert.Matches(link, await client.PageAsync("/account"));
        Assert.Matches(link, await client.PageAsync(ExperienceBrowser.Screen));
    }

    [Theory]
    [InlineData("/experience")]
    [InlineData("/experience?mode=live&period=30")]
    public async Task A_visitor_who_opens_the_screen_is_sent_to_sign_in(string page)
    {
        using var application = Application();
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    /// <summary>The application, with a minimum sample that a test reaches with a few trades.</summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: file => file.With("Experience:MinSamples", 3));
}
