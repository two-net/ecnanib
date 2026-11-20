using System.Text.RegularExpressions;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The experience screens show a strength score per model, role, market, and timeframe: the
/// role's measure damped toward zero for small samples, measure × n ÷ (n + damping). Slices
/// with fewer samples than the minimum show "not enough data", and the formula is shown in the
/// UI (FR-EXP-3, FR-EXP-4). See "The screens" in docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests takes 3 samples as the minimum, and damps by 25, which is not what the
/// tracked file damps by: so a page that shows 25 shows the damping of its file. The clock
/// stands at the morning of the tests, and everything happened before it.
/// </remarks>
public class StrengthPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Claude = StoredAgentRows.TeamModel;
    private const string Gemini = "gemini-test-model";

    private const string ByModel = "The strength score of each model in each role in Demo";
    private const string OfModel = "The strength score of the model in each role in Demo";

    private const string Formula = "Strength score = measure × n ÷ (n + 25), where n is the sample size.";

    private static readonly string[] Columns = ["Role", "Market", "Timeframe", "Samples", "Measure", "Strength score"];

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_screen_shows_the_strength_score_of_each_model_in_each_role_with_its_formula()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (_, claude, gemini) = await TeamAsync(application, user.Id);

        var page = await client.PageAsync(ExperienceBrowser.Screen);
        var read = AgentBrowser.Read(page);

        Assert.Contains("<h2 id=\"experience-strengths\">Strength by role</h2>", page);

        // The formula is shown, with the damping of the configuration file, and from how many
        // samples on a slice has a score.
        Assert.Contains(Formula, read);
        Assert.Contains("A slice of fewer than 3 samples says \"Not enough data\", and has no score.", read);

        // And how each role is measured, and that a vetoed plan is followed without an order.
        Assert.Contains("the share of the calls that were not neutral whose direction matched the price change over the next 4 closed candles, minus 50 percent", read);
        Assert.Contains("Strategist: the average R of the plans that were executed.", read);
        Assert.Contains("Risk Manager: the average R of the plans that it approved, minus the average R that the plans which it vetoed would have produced.", read);
        Assert.Contains("with the stop-loss taken to be hit first when both fall inside one candle. A shadow trade never places an order.", read);

        // The Market Analyst was right 3 times of 4, which is 25 points above a half, and 4
        // samples of 29 leave 3.4 of them. The Risk Manager approved plans that made 0.5 R on
        // average and vetoed plans that would have lost 1 R: 1.5 R, of which 6 samples of 31
        // leave 0.29. The Strategist made 0.5 R on average, of which 4 of 29 leave 0.07.
        Assert.Equal(["Model", "Provider", .. Columns], ExperienceBrowser.ColumnsOf(page, ByModel));
        Assert.Equal(
            [
                ["Claude Test", "Claude", "Market Analyst", "Spot", "1H", "4", "+25 percentage points", "+3.4 percentage points"],
                ["Claude Test", "Claude", "Risk Manager", "Spot", "1H", "6 (4 approved, 2 vetoed)", "+1.5 R", "+0.29 R"],
                [Gemini, "Gemini", "Strategist", "Spot", "1H", "4", "+0.5 R", "+0.07 R"],
            ],
            ExperienceBrowser.RowsOf(page, ByModel));

        // The scores are of the mode that the page shows, and the section has no badge of its own.
        Assert.Contains("A strength score says how well a model did one role, on one market and one timeframe, in Demo.", read);
        Assert.Single(Regex.Matches(page, Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))));

        // Each model leads to its page, and it says what the scores follow.
        Assert.Contains($"<a href=\"{ExperienceBrowser.ModelPage(claude)}\">Claude Test</a>", page);
        Assert.Contains($"<a href=\"{ExperienceBrowser.ModelPage(gemini)}\">{Gemini}</a>", page);
        Assert.Contains("When you delete an agent, they are deleted with it, and its calls, its trades, and its shadow trades leave the scores.", read);

        // Nothing is left out, so nothing is noted.
        Assert.DoesNotContain("role=\"note\"", page);
    }

    [Fact]
    public async Task A_slice_below_the_minimum_sample_says_not_enough_data_and_no_score()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        // Two calls, both right, where three are the minimum sample: the best measure there is,
        // and nothing to go by.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 2);

        var page = await client.PageAsync(ExperienceBrowser.Screen);

        // It says its sample, and neither a measure nor a score.
        Assert.Equal([[Claude, "Claude", "Market Analyst", "Spot", "1H", "2", "Not enough data"]], ExperienceBrowser.RowsOf(page, ByModel));
        Assert.DoesNotContain("percentage points</td>", page);

        // One more call is the minimum sample, and the slice has its measure and its score.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddDays(-1), matched: 1);

        Assert.Equal(
            [[Claude, "Claude", "Market Analyst", "Spot", "1H", "3", "+50 percentage points", "+5.4 percentage points"]],
            ExperienceBrowser.RowsOf(await client.PageAsync(ExperienceBrowser.Screen), ByModel));
    }

    [Fact]
    public async Task A_measure_that_cannot_be_said_is_written_as_not_available()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        // The Risk Manager approved three plans, and vetoed none that has a result: it has the
        // minimum sample, and nothing to take away from what it approved.
        foreach (var hour in Enumerable.Range(0, 3))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-20 + hour), resultInR: 1m, roles: [(Role.RiskManager, claude)]);
        }

        var page = await client.PageAsync($"{ExperienceBrowser.Screen}?role=risk-manager");

        Assert.Equal(
            [[Claude, "Claude", "Risk Manager", "Spot", "1H", "3 (3 approved, 0 vetoed)", "Not available", "Not available"]],
            ExperienceBrowser.RowsOf(page, ByModel));
    }

    [Fact]
    public async Task A_screen_with_nothing_measured_says_so_and_shows_the_formula_all_the_same()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        foreach (var path in new[] { ExperienceBrowser.Screen, $"{ExperienceBrowser.Screen}?mode=live&market=futures" })
        {
            var page = await client.PageAsync(path);
            var read = AgentBrowser.Read(page);
            var mode = path.Contains("live", StringComparison.Ordinal) ? "Live" : "Demo";

            Assert.Contains($"Nothing is measured here in {mode} yet: no call of an analyst is scored, no plan is executed, and no vetoed plan has a result.", read);
            Assert.Contains(Formula, read);
            Assert.DoesNotContain("<table", page);
        }
    }

    [Fact]
    public async Task The_strength_scores_follow_the_filter_and_the_mode_of_the_page()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (agent, claude, _) = await TeamAsync(application, user.Id);
        var futures = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Futures", market: Market.Futures, timeframe: Timeframe.FourHours);

        // The same model was wrong on Futures on 4 hours, in Demo, and wrong in Live.
        await application.GiveCallsAsync(user.Id, futures, Role.MarketAnalyst, claude, Morning.AddHours(-5), missed: 4);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), missed: 3, mode: TradingMode.Live);

        async Task<IReadOnlyList<string>> SlicesOf(string query, string caption = ByModel) =>
            [.. ExperienceBrowser.RowsOf(await client.PageAsync(ExperienceBrowser.Screen + query), caption).Select(row => string.Join(" | ", row[2..6]))];

        // With nothing chosen, every slice of Demo is there, each market and timeframe as a slice of its own.
        Assert.Equal(
            [
                "Market Analyst | Spot | 1H | 4",
                "Market Analyst | Futures | 4H | 4",
                "Risk Manager | Spot | 1H | 6 (4 approved, 2 vetoed)",
                "Strategist | Spot | 1H | 4",
            ],
            await SlicesOf(string.Empty));

        // A role, a market, and a timeframe each leave the other slices out.
        Assert.Equal(["Strategist | Spot | 1H | 4"], await SlicesOf("?role=strategist"));
        Assert.Equal(["Market Analyst | Futures | 4H | 4"], await SlicesOf("?market=futures"));
        Assert.Equal(["Market Analyst | Futures | 4H | 4"], await SlicesOf("?timeframe=4H"));
        Assert.Equal(["Market Analyst | Spot | 1H | 4", "Market Analyst | Futures | 4H | 4"], await SlicesOf("?role=market-analyst"));

        // A period counts a call by the close of its candle: these were all made in the last 7 days.
        Assert.Equal(4, (await SlicesOf("?period=7")).Count);

        // In Live there is what happened in Live, and nothing of Demo: the table says its mode.
        var live = await client.PageAsync($"{ExperienceBrowser.Screen}?mode=live");

        Assert.False(ExperienceBrowser.HasTable(live, ByModel));
        Assert.Equal(
            [["Claude Test", "Claude", "Market Analyst", "Spot", "1H", "3", "-50 percentage points", "-5.4 percentage points"]],
            ExperienceBrowser.RowsOf(live, "The strength score of each model in each role in Live"));
        Assert.Contains("in Live.", AgentBrowser.Read(live));
    }

    [Fact]
    public async Task The_page_of_a_model_shows_its_strength_in_each_role_and_of_no_other_model()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (_, claude, gemini) = await TeamAsync(application, user.Id);

        var page = await client.PageAsync(ExperienceBrowser.ModelPage(claude));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<h2 id=\"model-experience-strengths\">Strength by role</h2>", page);
        Assert.Contains(Formula, read);

        // The slices of the model alone, by role: a row does not name the model again.
        Assert.Equal(Columns, ExperienceBrowser.ColumnsOf(page, OfModel));
        Assert.Equal(
            [
                ["Market Analyst", "Spot", "1H", "4", "+25 percentage points", "+3.4 percentage points"],
                ["Risk Manager", "Spot", "1H", "6 (4 approved, 2 vetoed)", "+1.5 R", "+0.29 R"],
            ],
            ExperienceBrowser.RowsOf(page, OfModel));

        Assert.Equal(
            [["Strategist", "Spot", "1H", "4", "+0.5 R", "+0.07 R"]],
            ExperienceBrowser.RowsOf(await client.PageAsync(ExperienceBrowser.ModelPage(gemini)), OfModel));

        // A model that nothing of the user counts for says that, and shows the formula all the same.
        var unused = await application.GiveModelAsync("grok", "grok-test-model", Morning);
        var none = await client.PageAsync(ExperienceBrowser.ModelPage(unused));

        Assert.Contains("Nothing is measured here in Demo yet", AgentBrowser.Read(none));
        Assert.Contains(Formula, AgentBrowser.Read(none));
        Assert.DoesNotContain("<table", none);
    }

    [Fact]
    public async Task What_the_scores_leave_out_is_said()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        // One call waits for its candles, and two could not be scored. Two vetoed plans are
        // followed still, and one was never entered.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-1), matched: 3, awaiting: 1, withoutCandles: 2);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-3), open: true);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-2), open: true);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-4), resultInR: null, outcome: ShadowOutcome.NotEntered);

        var page = await client.PageAsync(ExperienceBrowser.Screen);

        Assert.Equal(
            [
                "1 call of an analyst is not counted yet: it is scored once 4 candles have closed after it.",
                "2 calls of analysts are not counted: the candles after them could no longer be had from Binance.",
                "2 vetoed plans are not counted yet: their shadow trades have reached neither their stop-loss nor their take-profit.",
                "1 vetoed plan is not counted, because its shadow trade has no result: its entry was not reached, its stop-loss or take-profit "
                    + "was on the wrong side of its entry, or the candles after it could no longer be had from Binance.",
            ],
            Regex.Matches(page, "<p role=\"note\">(.*?)</p>").Select(note => AgentBrowser.Read(note.Groups[1].Value)));

        // Only what has an outcome is a sample.
        Assert.Equal(
            [[Claude, "Claude", "Market Analyst", "Spot", "1H", "3", "+50 percentage points", "+5.4 percentage points"]],
            ExperienceBrowser.RowsOf(page, ByModel));
    }

    [Fact]
    public async Task A_user_sees_nothing_of_the_strength_scores_of_another_user()
    {
        using var application = Application();
        using var adasBrowser = application.CreateClient();
        using var gracesBrowser = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await adasBrowser.SignInAsync(Ada);
        await gracesBrowser.SignInAsync(Grace);

        // Both use the same model, whose page has one address for everyone.
        var model = await application.GiveModelAsync("claude", Claude, Morning);
        await application.GiveCallsAsync(
            ada.Id, await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2)), Role.MarketAnalyst, model, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(
            grace.Id, await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2)), Role.MarketAnalyst, model, Morning.AddHours(-5), missed: 8);

        string[] adas = ["Market Analyst", "Spot", "1H", "4", "+50 percentage points", "+6.9 percentage points"];
        string[] graces = ["Market Analyst", "Spot", "1H", "8", "-50 percentage points", "-12.1 percentage points"];

        Assert.Equal([adas], ExperienceBrowser.RowsOf(await adasBrowser.PageAsync(ExperienceBrowser.ModelPage(model)), OfModel));
        Assert.Equal([graces], ExperienceBrowser.RowsOf(await gracesBrowser.PageAsync(ExperienceBrowser.ModelPage(model)), OfModel));
        Assert.Equal([[Claude, "Claude", .. adas]], ExperienceBrowser.RowsOf(await adasBrowser.PageAsync(ExperienceBrowser.Screen), ByModel));
        Assert.Equal([[Claude, "Claude", .. graces]], ExperienceBrowser.RowsOf(await gracesBrowser.PageAsync(ExperienceBrowser.Screen), ByModel));
    }

    /// <summary>
    /// Gives a user an agent whose team did something in every role, in Demo, on Spot on the
    /// hour: a model of Claude was the Market Analyst, right 3 times of 4, and the Risk Manager,
    /// which approved four plans and vetoed two. A model of Gemini was the Strategist of the four.
    /// </summary>
    private static async Task<(Guid Agent, Guid Claude, Guid Gemini)> TeamAsync(AccountsApplication application, Guid userId)
    {
        // The model of Claude has a name that its provider gives it, and the one of Gemini has
        // none. It is in the catalog before the agent is given its team, which would put it
        // there without one.
        var claude = await application.GiveModelAsync("claude", Claude, Morning, displayName: "Claude Test");
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);
        var agent = await application.GiveAgentAsync(userId, Morning.AddDays(-2));

        await application.GiveCallsAsync(userId, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 3, missed: 1);

        // Four plans were executed: 2 R, -1 R, 1 R, and nothing, which is 0.5 R on average.
        foreach (var (result, hour) in new[] { 2m, -1m, 1m, 0m }.Select((result, hour) => (result, hour)))
        {
            await application.GiveTradeAsync(
                userId, agent, Morning.AddHours(-20 + hour), resultInR: result, roles: [(Role.Strategist, gemini), (Role.RiskManager, claude)]);
        }

        // And two were vetoed, which would have lost 1 R on average.
        await application.GiveShadowTradeAsync(userId, agent, claude, Morning.AddHours(-9), resultInR: -1.2m);
        await application.GiveShadowTradeAsync(userId, agent, claude, Morning.AddHours(-8), resultInR: -0.8m);

        return (agent, claude, gemini);
    }

    /// <summary>The application, with a minimum sample that a test reaches with a few rows.</summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: file => file.With("Experience:MinSamples", 3));
}
