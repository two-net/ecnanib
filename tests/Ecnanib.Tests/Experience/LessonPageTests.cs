using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A user can read all of their lessons, disable or delete any lesson, and reset an agent's
/// lessons (FR-EXP-8). These read the pages as a signed-in user does. See "The pages of the
/// lessons" in docs/experience.md.
/// </summary>
/// <remarks>The file of the tests lets a prompt recall 4 lessons, which the page says.</remarks>
public class LessonPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string LessonsPage = "/experience/lessons";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_user_reads_all_of_their_lessons_the_most_recently_closed_first()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Before anything is written, the page says so, and says what a lesson is all the same.
        var empty = AgentBrowser.Read(await client.PageAsync(LessonsPage));

        Assert.Contains("<h1 id=\"lessons-heading\">Lessons</h1>", empty);
        Assert.Contains("<p>No lesson is written yet. The first one is written after a position of one of your agents is closed.</p>", empty);
        Assert.Empty(LessonBrowser.LessonsOn(empty));

        var swing = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");
        var scalp = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Scalp", market: Market.Futures, symbol: "SOLUSDT", timeframe: Timeframe.FifteenMinutes);

        await application.GiveLessonAsync(user.Id, swing, Morning.AddHours(-5), takeaway: "The earliest.", resultInR: 1.25m);
        await application.GiveLessonAsync(user.Id, scalp, Morning.AddHours(-3), takeaway: "Of the scalp, in Live.", resultInR: -1m, mode: TradingMode.Live);
        await application.GiveLessonAsync(user.Id, swing, Morning.AddHours(-1), takeaway: "The latest.", resultInR: null, enabled: false);

        // One that waits to be written, and one that could not be: neither is a lesson to read,
        // and the page says how many there are of each.
        await application.GiveLessonAsync(user.Id, swing, Morning.AddMinutes(-20), outcome: null);
        await application.GiveLessonAsync(user.Id, swing, Morning.AddMinutes(-10), outcome: LessonOutcome.NoResult);

        var page = AgentBrowser.Read(await client.PageAsync(LessonsPage));
        var lessons = LessonBrowser.LessonsOn(page);

        // Every lesson of the user, of every agent, the most recently closed first: what was
        // traded and how the trade ended, whose agent's it is, the mode of the trade as a word,
        // when it was closed, and what the lesson says.
        Assert.Equal(
            ["BTCUSDT, Spot, 1H: no result in R", "SOLUSDT, Futures, 15m: -1 R", "BTCUSDT, Spot, 1H: +1.25 R"],
            lessons.Select(lesson => lesson.Heading));
        Assert.Equal(
            [
                "Agent Swing, in Demo. Closed 17 November 2026, 08:20 UTC.",
                "Agent Scalp, in Live. Closed 17 November 2026, 06:20 UTC.",
                "Agent Swing, in Demo. Closed 17 November 2026, 04:20 UTC.",
            ],
            lessons.Select(lesson => lesson.Of));
        Assert.Equal(["The latest.", "Of the scalp, in Live.", "The earliest."], lessons.Select(lesson => lesson.Parts["Takeaway"]));

        var earliest = lessons[2];

        Assert.Equal(["Setup", "Expected", "What happened", "Takeaway", "About"], earliest.Parts.Keys);
        Assert.Equal("Long on a breakout above the range high.", earliest.Parts["Setup"]);
        Assert.Equal("Continuation to the next resistance.", earliest.Parts["Expected"]);
        Assert.Equal("The price fell back into the range and the stop-loss was executed.", earliest.Parts["What happened"]);
        Assert.Equal("entry, timing", earliest.Parts["About"]);

        // Whether it is recalled, and the controls for it, each named after its lesson.
        Assert.Equal("Enabled: it may be recalled into prompts.", earliest.State);
        Assert.Equal(["Disable the BTCUSDT lesson of 17 November 2026, 04:20 UTC"], earliest.Buttons);
        Assert.Equal("Disabled: it is recalled into no prompt.", lessons[0].State);
        Assert.Equal(["Enable the BTCUSDT lesson of 17 November 2026, 08:20 UTC"], lessons[0].Buttons);
        Assert.Contains(lessons[1].Links, link => link.Text == "Delete the SOLUSDT lesson of 17 November 2026, 06:20 UTC");

        // The agent of a lesson leads to the lessons of that agent alone.
        Assert.Contains(new LessonBrowser.Link($"{LessonsPage}?agent={scalp}", "Scalp"), lessons[1].Links);

        Assert.Contains("<p>3 lessons, the most recently closed first. Page 1 of 1.</p>", page);
        Assert.Contains(
            "<p role=\"note\">The lesson of 1 closed position is not written yet. It is written within minutes of the result of the position.</p>",
            page);
        Assert.Contains(
            "<p role=\"note\">1 closed position has no lesson: its model could not be called, or gave none that is valid. It is not asked again.</p>",
            page);

        // It says what a lesson is, who writes it and under whose key, where it is recalled,
        // and what its user can do with it, in plain language (NFR-USA-2, NFR-SEC-8).
        Assert.Contains("the model of that agent's Risk Manager writes its lesson, once, in a call under your own key for that model's provider", page);
        Assert.Contains("It is sent the closed position, the plan that opened it, and the review of that plan, and nothing that says who you are.", page);
        Assert.Contains("The prompts of the Strategist and of the Risk Manager recall up to 4 of your lessons that are enabled", page);
        Assert.Contains("and then those of your other agents on the same symbol, market, and timeframe. Only your own lessons are ever recalled.", page);
        Assert.Contains("A lesson that you disable stays here, and is recalled into no prompt until you enable it again.", page);
        Assert.Contains("Resetting the lessons of an agent deletes every one of them", page);

        // A lesson says its mode as a word: the page shows lessons of both modes, and has no badge.
        Assert.DoesNotContain("mode-badge", page);

        // It leads back to the other pages of experience and of the agents.
        Assert.Contains("<a href=\"/experience\">Experience</a>", page);
        Assert.Contains("<a href=\"/agents\">Your agents</a>", page);
    }

    [Fact]
    public async Task The_lessons_of_one_agent_are_read_by_an_address_that_names_the_agent()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var swing = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");
        var scalp = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Scalp", timeframe: Timeframe.FifteenMinutes);
        var idle = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Idle");

        await application.GiveLessonAsync(user.Id, swing, Morning.AddHours(-3), takeaway: "Of the swing.");
        await application.GiveLessonAsync(user.Id, scalp, Morning.AddHours(-2), takeaway: "Of the scalp.");
        await application.GiveLessonAsync(user.Id, scalp, Morning.AddMinutes(-20), outcome: null);

        var page = AgentBrowser.Read(await client.PageAsync($"{LessonsPage}?agent={swing}"));

        Assert.Contains("<h1 id=\"lessons-heading\">Lessons of the agent Swing</h1>", page);
        Assert.Equal(["Of the swing."], LessonBrowser.LessonsOn(page).Select(lesson => lesson.Parts["Takeaway"]));

        // What waits for another agent is not said here.
        Assert.DoesNotContain("is not written yet", page);

        // It leads to the page that resets the lessons of this agent, to the agent, and to the
        // lessons of every agent.
        Assert.Contains($"<a href=\"/agents/{swing}/lessons/reset\">Reset the lessons of Swing</a>", page);
        Assert.Contains($"<a href=\"/agents/{swing}/state\">State of the agent</a>", page);
        Assert.Contains($"<a href=\"{LessonsPage}\">Lessons of all your agents</a>", page);

        // An agent with no lesson says so, and is there all the same: its pages lead here.
        var none = AgentBrowser.Read(await client.PageAsync($"{LessonsPage}?agent={idle}"));

        Assert.Contains("<h1 id=\"lessons-heading\">Lessons of the agent Idle</h1>", none);
        Assert.Contains("<p>This agent has no lesson.</p>", none);
        Assert.Empty(LessonBrowser.LessonsOn(none));

        // The pages of an agent lead to its lessons, and the experience screen to all of them.
        var link = $"<a href=\"{LessonsPage}?agent={swing}\">Lessons of the agent</a>";

        Assert.Contains(link, await client.PageAsync(AgentBrowser.AgentStatePage(swing)));
        Assert.Contains(link, await client.PageAsync(AgentBrowser.AgentPage(swing)));
        Assert.Contains($"<a href=\"{LessonsPage}\">Lessons from closed trades</a>", await client.PageAsync("/experience"));
    }

    [Fact]
    public async Task The_lessons_are_shown_a_page_at_a_time()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");

        foreach (var hour in Enumerable.Range(1, 27))
        {
            await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-hour), takeaway: $"Closed {hour} hours ago.");
        }

        // The first page: the 25 most recent, and what leads to the older ones.
        var first = AgentBrowser.Read(await client.PageAsync(LessonsPage));

        Assert.Equal(
            Enumerable.Range(1, 25).Select(hour => $"Closed {hour} hours ago."),
            LessonBrowser.LessonsOn(first).Select(lesson => lesson.Parts["Takeaway"]));
        Assert.Contains("<p>27 lessons, the most recently closed first. Page 1 of 2.</p>", first);
        Assert.Contains($"<a href=\"{LessonsPage}?page=2\">Older lessons</a>", first);
        Assert.DoesNotContain("Newer lessons", first);

        // The second: the rest, and what leads back. The first page has an address with no page in it.
        var second = AgentBrowser.Read(await client.PageAsync($"{LessonsPage}?page=2"));

        Assert.Equal(
            ["Closed 26 hours ago.", "Closed 27 hours ago."],
            LessonBrowser.LessonsOn(second).Select(lesson => lesson.Parts["Takeaway"]));
        Assert.Contains("<p>27 lessons, the most recently closed first. Page 2 of 2.</p>", second);
        Assert.Contains($"<a href=\"{LessonsPage}\">Newer lessons</a>", second);
        Assert.DoesNotContain("Older lessons", second);

        // The pages of the lessons of one agent keep the agent in their addresses.
        var ofAgent = AgentBrowser.Read(await client.PageAsync($"{LessonsPage}?agent={agent}"));

        Assert.Contains($"<a href=\"{LessonsPage}?agent={agent}&page=2\">Older lessons</a>", ofAgent);
        Assert.Equal(2, LessonBrowser.LessonsOn(AgentBrowser.Read(await client.PageAsync($"{LessonsPage}?agent={agent}&page=2"))).Count);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?page=two")]
    [InlineData("?page=01")]
    [InlineData("?page=2")]
    [InlineData("?page=1&page=1")]
    [InlineData("?agent=swing")]
    [InlineData("?agent=019a7c3e-5b1f-7d2a-9c4e-1f2a3b4c5d6e")]
    [InlineData("?agent=019A7C3E-5B1F-7D2A-9C4E-1F2A3B4C5D6E")]
    [InlineData("?agent={0}&agent={0}")]
    [InlineData("?agent={{{0}}}")]
    public async Task An_address_that_names_a_choice_which_there_is_not_is_not_found(string query)
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1));

        // A page that there is not, an agent that is none of the user's, and a choice that is
        // made twice or written in another way than the page writes it: the page shows what its
        // address asks for, and never something else in its place.
        using var answer = await client.GetAsync(LessonsPage + string.Format(System.Globalization.CultureInfo.InvariantCulture, query, agent), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?page=1")]
    [InlineData("?page=")]
    [InlineData("?agent=")]
    [InlineData("?agent={0}")]
    [InlineData("?agent={0}&page=1")]
    [InlineData("?mode=live&market=futures&period=7")]
    public async Task A_choice_that_is_absent_or_empty_is_not_made_and_what_the_page_has_no_choice_of_is_not_gone_by(string query)
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), takeaway: "The one lesson.");

        var page = AgentBrowser.Read(
            await client.PageAsync(LessonsPage + string.Format(System.Globalization.CultureInfo.InvariantCulture, query, agent)));

        Assert.Equal(["The one lesson."], LessonBrowser.LessonsOn(page).Select(lesson => lesson.Parts["Takeaway"]));
    }

    [Fact]
    public async Task A_user_disables_a_lesson_and_enables_it_again()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");

        var lesson = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-2), takeaway: "The one that is disabled.");
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), takeaway: "The other.");

        var scope = new LessonScope(agent, "BTCUSDT", Market.Spot, Timeframe.OneHour);

        Assert.Equal(2, (await application.Lessons.RecallAsync(user.Id, scope, Cancellation)).Count);

        // Disabled with one press of a button of the page, which answers with the page as it
        // is now: by its own address, so that a reload of it sends no form again.
        using (var disabled = await SendAsync(client, $"{LessonsPage}?agent={agent}", $"disable-lesson-{lesson}"))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.Equal($"{LessonsPage}?agent={agent}", disabled.RequestMessage!.RequestUri!.PathAndQuery);
        }

        var page = AgentBrowser.Read(await client.PageAsync(LessonsPage));
        var shown = LessonBrowser.LessonsOn(page).Single(one => one.Parts["Takeaway"] == "The one that is disabled.");

        // It stays on the page, says that it is recalled into no prompt, and can be enabled again.
        Assert.Equal("Disabled: it is recalled into no prompt.", shown.State);
        Assert.Equal(["Enable the BTCUSDT lesson of 17 November 2026, 07:20 UTC"], shown.Buttons);
        Assert.False((await application.Lessons.FindAsync(user.Id, lesson, Cancellation))!.Enabled);

        // And it is recalled into no prompt, while the other still is (FR-EXP-7).
        Assert.Equal(
            ["The other."],
            (await application.Lessons.RecallAsync(user.Id, scope, Cancellation)).Select(text => text[(text.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]));

        // The form that disabled it is no longer on the page: sent again, it is the form of no page.
        using (var again = await SendAsync(client, LessonsPage, $"disable-lesson-{lesson}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }

        // Enabled again, it is recalled again.
        using (var enabled = await SendAsync(client, LessonsPage, $"enable-lesson-{lesson}"))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.Equal(LessonsPage, enabled.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.True((await application.Lessons.FindAsync(user.Id, lesson, Cancellation))!.Enabled);
        Assert.Equal(2, (await application.Lessons.RecallAsync(user.Id, scope, Cancellation)).Count);
    }

    [Fact]
    public async Task Deleting_a_lesson_asks_once_more_and_is_for_good()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing", mode: TradingMode.Live);

        // Written in Demo, before the agent was switched to Live.
        var lesson = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-2), takeaway: "The one that is deleted.", mode: TradingMode.Demo);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), takeaway: "The other.");

        var deletePage = $"/agents/{agent}/lessons/{lesson}/delete";

        // The page of the lessons leads to a page that asks once more.
        Assert.Contains(
            $"<a href=\"{deletePage}\">Delete the BTCUSDT lesson of 17 November 2026, 07:20 UTC</a>",
            AgentBrowser.Read(await client.PageAsync(LessonsPage)));

        var asked = AgentBrowser.Read(await client.PageAsync(deletePage));

        // It says which lesson, what deleting it does, and that disabling it keeps it. It has
        // the badge of the agent's mode under its heading (FR-UI-3), and says the mode of the
        // lesson as a word.
        Assert.Contains("<h1>Delete a lesson of the agent Swing</h1>", asked);
        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} ", asked);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Demo), asked);
        Assert.Contains("This deletes the lesson, at once and for good.", asked);
        Assert.Contains("To keep the lesson and leave it out of the prompts, disable it instead.", asked);

        var shown = Assert.Single(LessonBrowser.LessonsOn(asked));

        Assert.Equal("The one that is deleted.", shown.Parts["Takeaway"]);
        Assert.Equal("Written from a trade in Demo. Closed 17 November 2026, 07:20 UTC.", shown.Of);
        Assert.Contains($"<a href=\"{LessonsPage}?agent={agent}\">Keep the lesson</a>", asked);
        Assert.Contains("<button type=\"submit\">Delete the lesson</button>", asked);

        // Reading the page deletes nothing.
        Assert.NotNull(await application.Lessons.FindAsync(user.Id, lesson, Cancellation));

        // The button deletes it, and answers with the lessons of the agent.
        var token = Browser.AntiforgeryTokenIn(asked);

        using (var deleted = await client.SendFormAsync(deletePage, "delete-lesson", token))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Equal($"{LessonsPage}?agent={agent}", deleted.RequestMessage!.RequestUri!.PathAndQuery);

            var after = AgentBrowser.Read(await deleted.Content.ReadAsStringAsync(Cancellation));

            Assert.Equal(["The other."], LessonBrowser.LessonsOn(after).Select(one => one.Parts["Takeaway"]));
        }

        Assert.Null(await application.Lessons.FindAsync(user.Id, lesson, Cancellation));
        Assert.Single(application.RowsOf(user.Id)["lessons"]);

        // The trade that it was of stays: a lesson is not a trade.
        Assert.Equal(2, application.RowsOf(user.Id)["positions"].Count);

        // The form that is sent twice finds no lesson the second time, and is answered as the
        // first one was. And the page of a lesson that is gone is not found.
        using (var again = await client.SendFormAsync(deletePage, "delete-lesson", token))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal($"{LessonsPage}?agent={agent}", again.RequestMessage!.RequestUri!.PathAndQuery);
        }

        using var gone = await client.GetAsync(deletePage, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Single(application.RowsOf(user.Id)["lessons"]);
    }

    [Fact]
    public async Task Resetting_the_lessons_of_an_agent_asks_once_more_and_deletes_every_one_of_them()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var swing = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        await application.GiveLessonAsync(user.Id, swing, Morning.AddHours(-4), takeaway: "Of the swing, earlier.");
        await application.GiveLessonAsync(user.Id, swing, Morning.AddHours(-3), takeaway: "Of the swing, later.", enabled: false);
        await application.GiveLessonAsync(user.Id, swing, Morning.AddMinutes(-20), outcome: null);
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-2), takeaway: "Of the twin.");

        var resetPage = $"/agents/{swing}/lessons/reset";
        var asked = AgentBrowser.Read(await client.PageAsync(resetPage));

        // The page says how many lessons go, and what stays. It has the badge of the agent's mode.
        Assert.Contains("<h1>Reset the lessons of the agent Swing</h1>", asked);
        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", asked);
        Assert.Contains(
            "<p>This deletes the 2 lessons of this agent, at once and for good. The lesson of 1 closed position that is not written yet is " +
            "not written either. The agent recalls none of its own lessons from then on, and neither do your other agents. " +
            "The lessons of your other agents stay. It cannot be undone.</p>",
            asked);
        Assert.Contains($"<a href=\"{LessonsPage}?agent={swing}\">Keep the lessons</a>", asked);
        Assert.Contains("<button type=\"submit\">Reset the lessons</button>", asked);

        // Reading the page resets nothing.
        Assert.Equal(4, application.RowsOf(user.Id)["lessons"].Count);

        var token = Browser.AntiforgeryTokenIn(asked);

        using (var reset = await client.SendFormAsync(resetPage, "reset-lessons", token))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.Equal($"{LessonsPage}?agent={swing}", reset.RequestMessage!.RequestUri!.PathAndQuery);
            Assert.Contains("<p>This agent has no lesson.</p>", AgentBrowser.Read(await reset.Content.ReadAsStringAsync(Cancellation)));
        }

        // Every lesson of the agent is gone, with the one that waited. The lesson of the other
        // agent stays, and is what both agents recall from now on.
        Assert.Equal(
            ["Of the twin."],
            LessonBrowser.LessonsOn(AgentBrowser.Read(await client.PageAsync(LessonsPage))).Select(lesson => lesson.Parts["Takeaway"]));
        Assert.Single(application.RowsOf(user.Id)["lessons"]);
        Assert.Empty(await application.DueLessons.DueAsync(most: 10, Cancellation));
        Assert.Single(await application.Lessons.RecallAsync(user.Id, new LessonScope(swing, "BTCUSDT", Market.Spot, Timeframe.OneHour), Cancellation));

        // Sent twice, the form is answered as the first one was. And the page says then that
        // there is nothing to reset.
        using (var again = await client.SendFormAsync(resetPage, "reset-lessons", token))
        {
            Assert.Equal($"{LessonsPage}?agent={swing}", again.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.Contains("This agent has no lesson that is written.", AgentBrowser.Read(await client.PageAsync(resetPage)));
        Assert.Single(application.RowsOf(user.Id)["lessons"]);
    }

    [Fact]
    public async Task The_lessons_of_a_user_are_out_of_reach_of_another_user()
    {
        using var application = Application();

        // Grace has an agent with two lessons.
        var grace = await application.RegisterVerifiedAsync(Grace);
        var gracesAgent = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2), name: "Grace's swing");
        var gracesLesson = await application.GiveLessonAsync(grace.Id, gracesAgent, Morning.AddHours(-2), takeaway: "Only Grace reads this.");
        await application.GiveLessonAsync(grace.Id, gracesAgent, Morning.AddHours(-1), takeaway: "And this.");

        var before = application.RowsOf(grace.Id)["lessons"];

        // Ada has an agent on the same symbol, market, and timeframe, and one lesson.
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        var adasAgent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2), name: "Ada's swing");
        await application.GiveLessonAsync(ada.Id, adasAgent, Morning.AddHours(-3), takeaway: "Ada's own.");

        // She reads her own, and nothing of Grace's.
        var page = await client.PageAsync(LessonsPage);
        var read = AgentBrowser.Read(page);

        Assert.Equal(["Ada's own."], LessonBrowser.LessonsOn(read).Select(lesson => lesson.Parts["Takeaway"]));
        Assert.DoesNotContain("Grace", read);
        Assert.DoesNotContain(gracesLesson.ToString(), page);
        Assert.DoesNotContain(gracesAgent.ToString(), page);

        // The lessons of an agent of Grace, and the pages that delete one of them or reset them,
        // are not found for her, as those of an agent that does not exist are not: nothing tells
        // her that an id is someone's. Nor is a lesson of Grace found under an agent of her own.
        foreach (var path in new[]
        {
            $"{LessonsPage}?agent={gracesAgent}",
            $"/agents/{gracesAgent}/lessons/reset",
            $"/agents/{gracesAgent}/lessons/{gracesLesson}/delete",
            $"/agents/{adasAgent}/lessons/{gracesLesson}/delete",
        })
        {
            using var answer = await client.GetAsync(path, Cancellation);
            var shown = await answer.Content.ReadAsStringAsync(Cancellation);

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Grace", AgentBrowser.Read(shown));
            Assert.DoesNotContain("Only Grace reads this.", AgentBrowser.Read(shown));
        }

        // Forms that are posted all the same change nothing of Grace's. Her page has no form
        // that disables a lesson of Grace: it is the form of no page.
        var token = Browser.AntiforgeryTokenIn(page);

        using (var disabled = await client.SendFormAsync(LessonsPage, $"disable-lesson-{gracesLesson}", token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        }

        // Deleting and resetting are answered as for a lesson and an agent that are gone
        // already, and delete nothing.
        foreach (var (path, form) in new[]
        {
            ($"/agents/{gracesAgent}/lessons/{gracesLesson}/delete", "delete-lesson"),
            ($"/agents/{adasAgent}/lessons/{gracesLesson}/delete", "delete-lesson"),
            ($"/agents/{gracesAgent}/lessons/reset", "reset-lessons"),
        })
        {
            using var answer = await client.SendFormAsync(path, form, token);

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.StartsWith(LessonsPage, answer.RequestMessage!.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        }

        Assert.Equal(before, application.RowsOf(grace.Id)["lessons"]);
        Assert.Single(application.RowsOf(ada.Id)["lessons"]);

        // Her download holds her lesson, and nothing of Grace's.
        var export = await client.PageAsync("/account/export");

        Assert.Equal(1, JsonDocument.Parse(export).RootElement.GetProperty("lessons").GetArrayLength());
        Assert.Contains("Ada\\u0027s own.", export);
        Assert.DoesNotContain("Only Grace reads this.", export);
        Assert.DoesNotContain(gracesAgent.ToString(), export);

        // And Grace reads hers.
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);

        Assert.Equal(
            ["And this.", "Only Grace reads this."],
            LessonBrowser.LessonsOn(AgentBrowser.Read(await gracesBrowser.PageAsync(LessonsPage))).Select(lesson => lesson.Parts["Takeaway"]));
    }

    [Fact]
    public async Task The_pages_of_the_lessons_are_for_a_signed_in_user()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var lesson = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), takeaway: "Not for a visitor.");

        using var visitor = application.CreateClient();

        // A visitor is sent to sign in, and reads nothing of a lesson.
        foreach (var path in new[] { LessonsPage, $"/agents/{agent}/lessons/reset", $"/agents/{agent}/lessons/{lesson}/delete" })
        {
            var (address, page) = await visitor.OpenAsync(path);

            Assert.StartsWith("/sign-in", address, StringComparison.Ordinal);
            Assert.DoesNotContain("Not for a visitor.", page);
        }
    }

    [Fact]
    public async Task What_a_model_wrote_is_shown_as_text()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));

        // A lesson is one line of text, whatever a model put in it: the page writes it as text,
        // and never as markup (NFR-SEC-4).
        const string Markup = "<script>alert('lesson')</script> & <b>wait</b>";
        var lesson = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), takeaway: Markup, setup: "<img src=x onerror=alert(1)>");

        foreach (var path in new[] { LessonsPage, $"/agents/{agent}/lessons/{lesson}/delete" })
        {
            var page = await client.PageAsync(path);

            Assert.DoesNotContain("<script>alert", page);
            Assert.DoesNotContain("<img src=x", page);
            Assert.DoesNotContain("<b>wait</b>", page);
            Assert.Contains("&lt;script&gt;alert(", page);

            // As a person reads it, it is what was written.
            Assert.Equal(Markup, LessonBrowser.LessonsOn(page).Single().Parts["Takeaway"]);
        }
    }

    [Fact]
    public async Task The_download_of_a_users_data_holds_their_lessons()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), market: Market.Futures, timeframe: Timeframe.FourHours);

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-3), takeaway: "The first.", resultInR: 1.5m, mode: TradingMode.Live);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-2), takeaway: "The second.", enabled: false);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-1), outcome: null);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddMinutes(-30), outcome: LessonOutcome.NoKey);

        var export = JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement.GetProperty("lessons");

        // Every row, in the order that the positions were closed in, with the names of things:
        // a lesson with what it says, and one that waits or could not be written with which.
        Assert.Equal(["Written", "Written", "Waiting", "NoKey"], export.EnumerateArray().Select(lesson => lesson.GetProperty("outcome").GetString()));

        var first = export[0];

        Assert.Equal(
            [
                "agentId", "mode", "market", "symbol", "timeframe", "closedAt", "resultInR", "outcome", "setup", "expected", "happened",
                "takeaway", "tags", "enabled", "writtenAt",
            ],
            first.EnumerateObject().Select(field => field.Name));
        Assert.Equal(
            (agent, "Live", "Futures", "BTCUSDT", "4H", 1.5m, "The first.", true),
            (
                first.GetProperty("agentId").GetGuid(),
                first.GetProperty("mode").GetString(),
                first.GetProperty("market").GetString(),
                first.GetProperty("symbol").GetString(),
                first.GetProperty("timeframe").GetString(),
                first.GetProperty("resultInR").GetDecimal(),
                first.GetProperty("takeaway").GetString(),
                first.GetProperty("enabled").GetBoolean()));
        Assert.Equal(Morning.AddHours(-3), first.GetProperty("closedAt").GetDateTimeOffset());
        Assert.Equal(["Entry", "Timing"], first.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        // A lesson that is disabled says so, and one that is not written has no words.
        Assert.False(export[1].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, export[2].GetProperty("takeaway").ValueKind);
        Assert.Equal(JsonValueKind.Null, export[3].GetProperty("setup").ValueKind);
        Assert.Equal(0, export[2].GetProperty("tags").GetArrayLength());
    }

    private static AccountsApplication Application() => AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));

    /// <summary>Presses a button of a page: reads the page, and sends the form of that name with what the page gave it.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string form) =>
        await client.SendFormAsync(path, form, Browser.AntiforgeryTokenIn(await client.PageAsync(path)));
}

/// <summary>What a browser reads of the lessons on a page.</summary>
internal static partial class LessonBrowser
{
    /// <summary>What leads somewhere: where to, and what it says.</summary>
    internal sealed record Link(string Href, string Text);

    /// <summary>A lesson as a page shows it.</summary>
    /// <param name="Heading">Its heading: what was traded, and how the trade ended.</param>
    /// <param name="Of">The line under it: whose agent's it is, the mode of the trade, and when it was closed.</param>
    /// <param name="Parts">What it says, by the name of each part, in the order of the page.</param>
    /// <param name="State">Whether it is recalled.</param>
    /// <param name="Buttons">What its buttons say.</param>
    /// <param name="Links">What leads from it to another page.</param>
    internal sealed record Shown(
        string Heading, string Of, IReadOnlyDictionary<string, string> Parts, string State, IReadOnlyList<string> Buttons, IReadOnlyList<Link> Links);

    /// <summary>
    /// The lessons on a page, in the order the page has them, each as a person reads it: what
    /// the markup writes as numbers is read as the characters that it stands for.
    /// </summary>
    public static IReadOnlyList<Shown> LessonsOn(string page) =>
    [
        .. Article().Matches(Comment().Replace(page, string.Empty)).Select(article =>
        {
            var inside = article.Groups[1].Value;
            var paragraphs = Paragraph().Matches(inside).Select(paragraph => Text(paragraph.Groups[1].Value)).ToList();

            return new Shown(
                Text(Heading().Match(inside).Groups[1].Value),
                paragraphs[0],
                new OrderedParts(Part().Matches(inside).Select(part => (Text(part.Groups[1].Value), Text(part.Groups[2].Value)))),
                paragraphs[^1],
                [.. Button().Matches(inside).Select(button => Text(button.Groups[1].Value))],
                [.. Anchor().Matches(inside).Select(anchor => new Link(anchor.Groups[1].Value, Text(anchor.Groups[2].Value)))]);
        }),
    ];

    /// <summary>What markup says to a person: without its tags, with one space where it has many, and with its characters read.</summary>
    private static string Text(string markup) =>
        WebUtility.HtmlDecode(Space().Replace(Tag().Replace(markup, string.Empty), " ").Trim());

    [GeneratedRegex("<article class=\"lesson\"[^>]*>(.*?)</article>", RegexOptions.Singleline)]
    private static partial Regex Article();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<h2[^>]*>(.*?)</h2>", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("<p>(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex Paragraph();

    [GeneratedRegex("<dt>(.*?)</dt>\\s*<dd>(.*?)</dd>", RegexOptions.Singleline)]
    private static partial Regex Part();

    [GeneratedRegex("<button type=\"submit\">(.*?)</button>", RegexOptions.Singleline)]
    private static partial Regex Button();

    [GeneratedRegex("<a href=\"([^\"]*)\">(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex("<[a-z/][^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex("\\s+")]
    private static partial Regex Space();

    /// <summary>The parts of a lesson by their names, which keep the order that the page has them in.</summary>
    private sealed class OrderedParts(IEnumerable<(string Name, string Said)> parts) : IReadOnlyDictionary<string, string>
    {
        private readonly List<(string Name, string Said)> _parts = [.. parts];

        public IEnumerable<string> Keys => _parts.Select(part => part.Name);

        public IEnumerable<string> Values => _parts.Select(part => part.Said);

        public int Count => _parts.Count;

        public string this[string key] => _parts.Single(part => part.Name == key).Said;

        public bool ContainsKey(string key) => _parts.Any(part => part.Name == key);

        public bool TryGetValue(string key, out string value)
        {
            value = _parts.FirstOrDefault(part => part.Name == key).Said ?? string.Empty;

            return ContainsKey(key);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            _parts.Select(part => new KeyValuePair<string, string>(part.Name, part.Said)).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
