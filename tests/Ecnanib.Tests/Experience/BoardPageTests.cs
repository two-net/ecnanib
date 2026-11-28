using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The page of the platform-wide board shows the same track record aggregated across all users,
/// and a slice only when enough users and trades contribute. It exposes no user identity,
/// balance, or individual trade, and a user opts out of contributing there (FR-EXP-10). See "The
/// platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests takes 3 users and 6 trades as what must contribute, and 3 trades as
/// the minimum sample. The clock stands at the morning of the tests, and the trades were closed
/// before it.
/// </remarks>
public class BoardPageTests
{
    private const string Ada = "ada@example.com";

    private const string Board = "/experience/board";
    private const string Gemini = "gemini-test-model";

    private const string ByProvider = "The track record of each provider across all users in Demo";
    private const string ByModel = "The track record of each model across all users in Demo";

    private const string LeaveForm = "leave-board";
    private const string ReturnForm = "return-to-board";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_page_shows_the_track_record_of_each_provider_and_of_each_model_across_all_users()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Three other users, each with two trades of one team: those of the first made 2 R each,
        // those of the second lost 1 R each, and those of the third made 1.5 R each. Ada, who
        // reads the board, has no trade.
        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning, displayName: "Claude Test"));

        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-3), team, resultInR: 2m, first: 1);
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-2), team, resultInR: -1m, first: 2);
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-1), team, resultInR: 1.5m, first: 3);

        var page = await client.PageAsync(Board);
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Platform-wide board</title>", page);
        Assert.Contains("<h1>Platform-wide board</h1>", page);
        Assert.Contains("This is how the trades of the agents of all users came out for each AI provider and each model, added up across users.", read);
        Assert.Contains("Every figure is in R: the amount that was risked on a trade", read);

        // The same measures as on the experience screen, of the 6 trades of all three: 4 won,
        // the total is 5 R, and it ran 2, 4, 3, 2, 3.5, 5, so it fell by 2 R at the most.
        Assert.Equal(["Provider", .. ExperienceBrowser.Measures], ExperienceBrowser.ColumnsOf(page, ByProvider));
        Assert.Equal(
            [["Claude", "6", "66.7 percent", "+0.83 R", "+5 R", "3.5", "2 R", "1 hour", "None"]],
            ExperienceBrowser.RowsOf(page, ByProvider));

        Assert.Equal(["Model", "Provider", .. ExperienceBrowser.Measures], ExperienceBrowser.ColumnsOf(page, ByModel));
        Assert.Equal(
            [["Claude Test", "Claude", "6", "66.7 percent", "+0.83 R", "+5 R", "3.5", "2 R", "1 hour", "None"]],
            ExperienceBrowser.RowsOf(page, ByModel));

        // It says what it never shows, and when a record is shown, with the numbers of the file.
        Assert.Contains("The board shows no user, no balance, and no single trade.", read);
        Assert.Contains("A provider or a model has a record here only when at least 3 users and 6 closed trades contribute to it", read);

        // And for whom a trade counts, which trades it counts, and what it follows.
        Assert.Contains("A trade counts once for every model of the team that decided it, and once for every provider of that team.", read);
        Assert.Contains("Counted are the trades of all time.", read);
        Assert.Contains("When an agent or an account is deleted, its trades leave the board, and so do the trades of a user who leaves it.", read);

        // A model leads nowhere: the page of a model is of the reader's own trades. The board
        // leads to those, and to the catalog.
        Assert.DoesNotContain("/experience/models/", page);
        Assert.Contains("<a href=\"/experience\">Your own experience records</a>", page);
        Assert.Contains("<a href=\"/catalog\">Model catalog</a>", page);
    }

    [Fact]
    public async Task A_record_that_too_few_contribute_to_is_not_on_the_page_at_all()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var claude = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var gemini = BoardContributors.TeamOf(await application.GiveModelAsync("gemini", Gemini, Morning, displayName: "Gemini Test"));

        // Claude has 6 trades of 3 users. Gemini has 6 trades too, of 2 users.
        await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), claude);
        await application.GiveContributorsAsync(users: 2, tradesEach: 3, Morning.AddHours(-2), gemini, first: 4);

        var page = await client.PageAsync(Board);

        Assert.Equal(["Claude"], ExperienceBrowser.RowsOf(page, ByProvider).Select(row => row[0]));
        Assert.Equal([StoredAgentRows.TeamModel], ExperienceBrowser.RowsOf(page, ByModel).Select(row => row[0]));

        // Nothing says that Gemini has trades here: not its name, not its model, and no number
        // of what is left out. It is the content of the page that says none of it: the foot of
        // every page names the four providers, to say that the service is none of theirs
        // (NFR-LEG-4), and says nothing of anyone's trades.
        var content = Regex.Match(page, "<main[^>]*>(.*?)</main>", RegexOptions.Singleline).Groups[1].Value;

        Assert.Contains(ByProvider, content);
        Assert.DoesNotContain("Gemini", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not counted", page, StringComparison.OrdinalIgnoreCase);

        // With a choice that leaves too few for anything, the page says so, and has no table.
        var futures = AgentBrowser.Read(await client.PageAsync($"{Board}?market=futures"));

        Assert.Contains("No provider has a record here in Demo: to none of them do at least 3 users and 6 closed trades contribute.", futures);
        Assert.Contains("No model has a record here in Demo: to none of them do at least 3 users and 6 closed trades contribute.", futures);
        Assert.False(ExperienceBrowser.HasTable(futures, ByProvider));
        Assert.False(ExperienceBrowser.HasTable(futures, ByModel));
    }

    [Fact]
    public async Task The_page_names_no_user_no_agent_and_no_trade()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var contributors = await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), team);
        var trades = await application.BoardTrades.OfContributorsAsync(null, Cancellation);

        Assert.Equal(6, trades.Count);

        foreach (var query in new[] { string.Empty, "?mode=demo", "?mode=live", "?role=strategist&market=spot&timeframe=1H&period=7" })
        {
            var page = await client.PageAsync(Board + query);

            // No address of a user and no id of one, of an agent, or of a position: a record is
            // of all of them together.
            Assert.All(contributors, contributor =>
            {
                Assert.DoesNotContain(contributor.User.Email, page, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(contributor.User.Id.ToString(), page, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(contributor.AgentId.ToString(), page, StringComparison.OrdinalIgnoreCase);
            });
            Assert.All(trades, trade => Assert.DoesNotContain(trade.Trade.PositionId.ToString(), page, StringComparison.OrdinalIgnoreCase));

            // Neither the name of an agent, nor what it trades, nor an amount of money.
            Assert.DoesNotContain("Seeded", page);
            Assert.DoesNotContain("BTCUSDT", page);
            Assert.DoesNotContain("USDT", page);
        }
    }

    [Fact]
    public async Task The_page_shows_one_mode_says_why_and_leads_to_the_other_with_no_number_of_trades()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var contributors = await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), team);

        // Two of the three have a trade in Live as well: too few for a record there.
        foreach (var (user, agent) in contributors.Take(2))
        {
            await application.GiveTradesAsync(user.Id, agent, count: 1, Morning.AddHours(-2), team, mode: TradingMode.Live);
        }

        var page = await client.PageAsync(Board);
        var read = AgentBrowser.Read(page);

        // Told no mode, it shows Demo, as a badge, says why, and leads to Live.
        Assert.Contains("Mode: <span class=\"mode-badge mode-demo\">Demo</span>", page);
        Assert.Contains("These are the results of all users in Demo: Live has no record that enough users and trades contribute to.", read);
        Assert.Contains("Results in Demo and results in Live are never added up.", read);
        Assert.Contains("<a href=\"/experience/board?mode=live\">Show the results of all users in Live</a>", page);
        Assert.Equal(["6"], ExperienceBrowser.RowsOf(page, ByProvider).Select(row => row[1]));

        // Asked for Live, it shows Live, which has nothing to show, and does not say that two
        // trades are there: the only numbers of trades on the page are what must contribute.
        var live = await client.PageAsync($"{Board}?mode=live");
        var readLive = AgentBrowser.Read(live);

        Assert.Contains("Mode: <span class=\"mode-badge mode-live\">Live</span>", live);
        Assert.Contains("These are the results of all users in Live, which you chose.", readLive);
        Assert.Contains("<a href=\"/experience/board?mode=demo\">Show the results of all users in Demo</a>", live);
        Assert.Contains("No provider has a record here in Live", readLive);
        Assert.DoesNotContain("2 closed trades", readLive);
        Assert.DoesNotContain("1 closed trade", readLive);

        // The filter is in the address, as on the experience screen, and a link keeps it.
        var chosen = await client.PageAsync($"{Board}?role=risk-manager&market=spot&timeframe=1H&mode=demo&period=30");

        Assert.Equal("risk-manager", ExperienceBrowser.ChosenIn(chosen, "role"));
        Assert.Equal("spot", ExperienceBrowser.ChosenIn(chosen, "market"));
        Assert.Equal("1H", ExperienceBrowser.ChosenIn(chosen, "timeframe"));
        Assert.Equal("demo", ExperienceBrowser.ChosenIn(chosen, "mode"));
        Assert.Equal("30", ExperienceBrowser.ChosenIn(chosen, "period"));
        Assert.Contains("<form method=\"get\" action=\"/experience/board\"", chosen);
        Assert.Contains("href=\"/experience/board?role=risk-manager&amp;market=spot&amp;timeframe=1H&amp;mode=live&amp;period=30\"", chosen);
        Assert.Contains("A trade counts for the model that held the role of Risk Manager when its position was opened", AgentBrowser.Read(chosen));
        Assert.Equal(["6"], ExperienceBrowser.RowsOf(chosen, ByProvider).Select(row => row[1]));
    }

    [Fact]
    public async Task A_user_leaves_the_board_and_comes_back_to_it_with_one_press_each()
    {
        using var application = Application();
        using var client = application.CreateClient();

        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var contributors = await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), team);
        var user = contributors[2].User;

        await client.SignInAsync(user.Email);

        // The third of the three reads the board, which their trades are counted on.
        const string address = "/experience/board?market=spot";

        var page = await client.PageAsync(address);
        var read = AgentBrowser.Read(page);

        Assert.Contains("<h2 id=\"board-contribution\">Your trades on the board</h2>", page);
        Assert.Contains("The closed trades of your agents are counted on the board: in records like the ones here, and never under your name.", read);
        Assert.Contains("Leaving the board changes nothing of your own experience records, and you still see the board.", read);
        Assert.Equal(["Leave the board"], ButtonsOn(page));
        Assert.Equal(["6"], ExperienceBrowser.RowsOf(page, ByProvider).Select(row => row[1]));

        // One press leaves it. The page answers with itself, by its own address, so a reload
        // sends no form again.
        using (var left = await client.SendFormAsync(address, LeaveForm, Browser.AntiforgeryTokenIn(page)))
        {
            Assert.Equal(HttpStatusCode.OK, left.StatusCode);
            Assert.Equal(address, left.RequestMessage!.RequestUri!.PathAndQuery);

            page = await left.Content.ReadAsStringAsync(Cancellation);
        }

        read = AgentBrowser.Read(page);

        // Their trades are counted in no record from now on: 4 trades of 2 users are left,
        // which is not shown.
        Assert.False(await application.BoardContribution.OfUserAsync(user.Id, Cancellation));
        Assert.Contains("You have left the board: none of your trades is counted in it.", read);
        Assert.Equal(["Count my trades on the board again"], ButtonsOn(page));
        Assert.False(ExperienceBrowser.HasTable(page, ByProvider));
        Assert.Contains("No provider has a record here in Demo", read);

        // Their own records are as they were.
        Assert.Equal(
            ["2"],
            ExperienceBrowser.RowsOf(await client.PageAsync(ExperienceBrowser.Screen), "The track record of each provider in Demo").Select(row => row[1]));

        // The form that left it is no longer on the page: sent again, it is the form of no page.
        using (var again = await client.SendFormAsync(address, LeaveForm, Browser.AntiforgeryTokenIn(page)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }

        Assert.False(await application.BoardContribution.OfUserAsync(user.Id, Cancellation));

        // One press comes back, with every trade that they have.
        using (var back = await client.SendFormAsync(address, ReturnForm, Browser.AntiforgeryTokenIn(page)))
        {
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
            Assert.Equal(address, back.RequestMessage!.RequestUri!.PathAndQuery);

            page = await back.Content.ReadAsStringAsync(Cancellation);
        }

        Assert.True(await application.BoardContribution.OfUserAsync(user.Id, Cancellation));
        Assert.Equal(["Leave the board"], ButtonsOn(page));
        Assert.Equal(["6"], ExperienceBrowser.RowsOf(page, ByProvider).Select(row => row[1]));
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_changes_nothing()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Something that is not the page sends the form, with a token that the page did not give.
        using (var forged = await client.SendFormAsync(Board, LeaveForm, "not-the-token"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        Assert.True(await application.BoardContribution.OfUserAsync(user.Id, Cancellation));
        Assert.Empty(application.RowsOf(user.Id)["board_opt_outs"]);
    }

    [Fact]
    public async Task The_download_of_a_users_data_says_whether_they_left_the_board()
    {
        var clock = new TestClock(Morning);

        using var application = Application(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A user who contributes has made no choice that is stored.
        Assert.Equal(0, JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement.GetProperty("boardOptOuts").GetArrayLength());

        clock.Set(Morning.AddMinutes(10));
        await client.SubmitAsync(Board, LeaveForm);

        // One who left has: when they left, and nothing else.
        var left = Assert.Single(JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement.GetProperty("boardOptOuts").EnumerateArray());

        Assert.Equal(["optedOutAt"], left.EnumerateObject().Select(member => member.Name));
        Assert.Equal(Morning.AddMinutes(10), left.GetProperty("optedOutAt").GetDateTimeOffset());

        // The account page says where the choice is made, and that it is in the download.
        var account = await client.PageAsync("/account");

        Assert.Contains("<a href=\"/experience/board\">platform-wide board</a>", account);
        Assert.Contains("Whether you left it is stored with your account, and is in a download.", AgentBrowser.Read(account));
    }

    [Fact]
    public async Task The_experience_screen_leads_to_the_board_with_its_filter()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Contains(
            "<a href=\"/experience/board\">Platform-wide board: how the trades of all users came out</a>",
            await client.PageAsync(ExperienceBrowser.Screen));
        Assert.Contains(
            "<a href=\"/experience/board?market=futures&amp;period=30\">Platform-wide board: how the trades of all users came out</a>",
            await client.PageAsync($"{ExperienceBrowser.Screen}?market=futures&period=30"));

        // And the board leads back, with the same choices.
        Assert.Contains(
            "<a href=\"/experience?market=futures&amp;period=30\">Your own experience records</a>",
            await client.PageAsync($"{Board}?market=futures&period=30"));
    }

    [Theory]
    [InlineData("mode=both")]
    [InlineData("mode=Live")]
    [InlineData("market=1")]
    [InlineData("period=31")]
    [InlineData("role=strategist&role=risk-manager")]
    public async Task An_address_that_names_a_choice_which_there_is_not_is_not_found(string query)
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.GetAsync($"{Board}?{query}", Cancellation);

        // Nothing else is shown in its place: neither Demo for a mode that there is not, nor
        // every trade for a period that there is not.
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Theory]
    [InlineData("/experience/board")]
    [InlineData("/experience/board?mode=live&period=30")]
    public async Task A_visitor_who_opens_the_board_is_sent_to_sign_in(string page)
    {
        using var application = Application();
        using var client = application.CreateClient();

        // The board is for the users of the service: nothing of it is shown to a visitor.
        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
        Assert.DoesNotContain("Platform-wide board", shown);
    }

    /// <summary>The application, with numbers that a test reaches with a few users and trades.</summary>
    private static AccountsApplication Application(TestClock? clock = null) =>
        AgentsApplication.With(
            new StandInMarketData(),
            clock ?? new TestClock(Morning),
            settings: file => file
                .With("Experience:MinSamples", 3)
                .With("Experience:Board:MinUsers", 3)
                .With("Experience:Board:MinTrades", 6));

    /// <summary>
    /// What the buttons of the forms that change something say, in the order of the page. The
    /// filter is a form that asks for a page, and the bar at the top has a form of its own.
    /// </summary>
    private static IReadOnlyList<string> ButtonsOn(string page)
    {
        var section = page[page.IndexOf("<h2 id=\"board-contribution\">", StringComparison.Ordinal)..];

        return
        [
            .. System.Text.RegularExpressions.Regex
                .Matches(section, "<form method=\"post\".*?<button type=\"submit\">(.*?)</button>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(button => WebUtility.HtmlDecode(button.Groups[1].Value)),
        ];
    }
}
