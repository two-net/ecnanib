using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.LiveUpdates;

namespace Ecnanib.Tests.Dashboard;

/// <summary>
/// First-time users see a setup checklist on the dashboard: add keys, create a Demo agent,
/// review its decisions, then consider Live, and each step shows as done once its user has
/// completed it (FR-UI-4). The first two are read from the keys and the agents of the user. The
/// last two are what the user says, with a form each, and a switch of an agent to Live says the
/// last one. The checklist is shown until both are said, and then no more. See "The setup
/// checklist" in docs/dashboard.md.
/// </summary>
public partial class SetupChecklistPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Dashboard = "/dashboard";

    private const string ReviewedForm = "setup-reviewed";
    private const string ConsideredForm = "setup-considered";

    private const string Done = "Done";
    private const string NotDone = "Not done yet";

    private static readonly DateTimeOffset Morning = Feeding.Morning;

    [Fact]
    public async Task A_first_time_user_is_shown_the_four_steps_in_their_order_with_none_done_and_what_to_do_for_each()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);

        var page = Read(await ada.Browser.PageAsync(Dashboard));
        var checklist = ChecklistOf(page);

        // Under the heading of the dashboard and what it says of itself, above the sections of
        // the modes: a section of its own, named by its heading.
        Assert.StartsWith("<section class=\"setup-checklist\" aria-labelledby=\"setup-heading\">", checklist, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"setup-heading\">Getting started</h2>", checklist);
        Assert.True(page.IndexOf("<h1>Dashboard</h1>", StringComparison.Ordinal) < page.IndexOf(checklist, StringComparison.Ordinal));
        Assert.True(page.IndexOf(checklist, StringComparison.Ordinal) < page.IndexOf("Your agents in Live", StringComparison.Ordinal));

        // What it is, when it goes away, and how many of its steps are done.
        Assert.Contains("This list goes away once the last two steps are done.", checklist);
        Assert.Contains(">None of the 4 steps is done yet.</strong>", checklist);

        // The four steps of the requirement, in its order, in a list that numbers them. Each
        // says that it is not done as a word (FR-THM-5).
        Assert.Contains("<ol class=\"setup-steps\">", checklist);

        var steps = StepsOf(checklist);

        Assert.Equal(
            [("setup-keys", "Add keys"), ("setup-agent", "Create a Demo agent"), ("setup-decisions", "Review its decisions"), ("setup-live", "Consider Live")],
            steps.Select(step => (step.Key, step.Name)));
        Assert.All(steps, step => Assert.Equal(NotDone, step.State));

        // What there is to do for each, and where: the keys, and the form of a new agent.
        Assert.Contains("Neither is stored yet.", steps[0].Says);
        Assert.Contains("<a href=\"/account/keys\">Go to your API keys</a>", steps[0].Says);
        Assert.Contains("It starts in Demo: Binance's test environment, with no real funds.", steps[1].Says);
        Assert.Contains("<a href=\"/agents/new\">Create an agent</a>", steps[1].Says);

        // The last two lead nowhere yet, and neither has its button: no team has decided.
        Assert.Contains("No team of yours has decided anything yet.", steps[2].Says);
        Assert.Contains("This step comes after you have reviewed decisions.", steps[3].Says);
        Assert.DoesNotContain("<a ", steps[2].Says);
        Assert.DoesNotContain("<a ", steps[3].Says);
        Assert.Empty(FormsOn(page));

        // The page follows what changes, so it has no field to type in.
        Assert.DoesNotContain("<input type=\"text\"", checklist);
        Assert.DoesNotContain("<select", checklist);
        Assert.DoesNotContain("<textarea", checklist);
    }

    [Fact]
    public async Task The_keys_are_added_once_a_key_of_an_AI_provider_and_a_Binance_key_for_Demo_are_stored()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);

        // A key of an AI provider alone: the step says which key is missing, and still leads
        // to the keys.
        await application.GiveAiKeyAsync(ada.User.Id, "claude", Morning, accessibleModels: null);

        var keys = StepsOf(ChecklistOf(Read(await ada.Browser.PageAsync(Dashboard))))[0];

        Assert.Equal(NotDone, keys.State);
        Assert.Contains("A key of an AI provider is stored. Still to add: a Binance key for Demo, which your agent trades with.", keys.Says);
        Assert.Contains("<a href=\"/account/keys\">Go to your API keys</a>", keys.Says);

        // With the Binance key for Demo, the step is done, and leads nowhere.
        await application.GiveExchangeKeyAsync(ada.User.Id, Morning);

        var page = Read(await ada.Browser.PageAsync(Dashboard));
        keys = StepsOf(ChecklistOf(page))[0];

        Assert.Equal(Done, keys.State);
        Assert.Contains("A key of an AI provider and a Binance key for Demo are stored.", keys.Says);
        Assert.DoesNotContain("<a ", keys.Says);
        Assert.Contains(">1 of the 4 steps is done.</strong>", page);

        // The step says the account as it is: a key that is deleted is a step that is open again.
        await ada.Browser.DeleteExchangeKeyAsync("demo");

        keys = StepsOf(ChecklistOf(Read(await ada.Browser.PageAsync(Dashboard))))[0];

        Assert.Equal(NotDone, keys.State);
        Assert.Contains("Still to add: a Binance key for Demo", keys.Says);
    }

    [Fact]
    public async Task A_Demo_agent_is_created_once_the_user_has_an_agent()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);

        // A user who has no key yet creates an agent all the same: the steps do not wait for
        // one another.
        await application.GiveAgentAsync(ada.User.Id, Morning.AddHours(-1), name: "Swing");

        var steps = StepsOf(ChecklistOf(Read(await ada.Browser.PageAsync(Dashboard))));

        Assert.Equal([NotDone, Done, NotDone, NotDone], steps.Select(step => step.State));
        Assert.Contains("You have an agent. Every agent starts in Demo, where no money is at stake.", steps[1].Says);
        Assert.DoesNotContain("<a ", steps[1].Says);

        // An agent that has decided nothing has nothing to review yet.
        Assert.Contains("No team of yours has decided anything yet.", steps[2].Says);
    }

    [Fact]
    public async Task The_user_marks_the_decisions_as_reviewed_and_then_Live_as_considered_and_the_checklist_is_gone_for_good()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        await application.GiveKeysAsync(ada.User.Id, Morning.AddDays(-1));
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Swing");

        // A cycle that its user ran as a test is no decision (FR-CYC-12): the step has no button.
        await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-3), kind: CycleKind.Test);

        var page = Read(await ada.Browser.PageAsync(Dashboard));

        Assert.Contains("No team of yours has decided anything yet.", StepsOf(ChecklistOf(page))[2].Says);
        Assert.Empty(FormsOn(page));

        // The team decides. The step names the agent, leads to its screen, where its decisions
        // are, and has the button. A decision alone does not do the step: its user says so.
        await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-1));

        page = Read(await ada.Browser.PageAsync(Dashboard));
        var steps = StepsOf(ChecklistOf(page));

        Assert.Equal([Done, Done, NotDone, NotDone], steps.Select(step => step.State));
        Assert.Contains("The team of Swing has decided.", steps[2].Says);
        Assert.Contains($"<a href=\"/agents/{agent}/state\">Read the decisions of Swing</a>", steps[2].Says);
        Assert.Contains("<button type=\"submit\">Mark as reviewed</button>", steps[2].Says);
        Assert.Equal([ReviewedForm], FormsOn(page));
        Assert.False(IsMarked(application, Ada, "decisions_reviewed_at"));

        // Live is not offered before the review, as the checklist says "then".
        Assert.Contains("This step comes after you have reviewed decisions.", steps[3].Says);

        // Its user marks the decisions as reviewed. The answer is the dashboard, by a request
        // that changes nothing, and the mark is stored with the account, as of now.
        feeding.Clock.SetUtcNow(Morning.AddMinutes(5));

        var (address, reviewed) = await ada.Browser.SubmitAsync(Dashboard, ReviewedForm);

        Assert.Equal(Dashboard, address);
        Assert.Equal(Morning.AddMinutes(5), MarkedAt(application, Ada, "decisions_reviewed_at"));

        reviewed = Read(reviewed);
        steps = StepsOf(ChecklistOf(reviewed));

        Assert.Equal([Done, Done, Done, NotDone], steps.Select(step => step.State));
        Assert.Contains("You marked the decisions as reviewed.", steps[2].Says);
        Assert.Contains(">3 of the 4 steps are done.</strong>", reviewed);

        // Now Live: what it is, what a switch takes (FR-AGT-7), that staying in Demo is a choice
        // too, where an agent is switched, the risk disclosure, and the button.
        Assert.Contains("In Live an agent trades in your real Binance account, with real money, and can lose it.", steps[3].Says);
        Assert.Contains("a valid Binance key for Live, your confirmation of the risk, and a fresh proof of who you are", steps[3].Says);
        Assert.Contains("Staying in Demo is a choice too: mark this step either way.", steps[3].Says);
        Assert.Contains("<a href=\"/agents\">Go to your agents</a>", steps[3].Says);
        Assert.Contains("<a href=\"/legal/risk-disclosure\">Read the risk disclosure</a>", steps[3].Says);
        Assert.Contains("<button type=\"submit\">I have considered Live</button>", steps[3].Says);
        Assert.Equal([ConsideredForm], FormsOn(reviewed));

        // Its user stays in Demo, and says that they considered Live. All four are done, and
        // the dashboard shows the checklist no more.
        feeding.Clock.SetUtcNow(Morning.AddMinutes(10));

        var (_, considered) = await ada.Browser.SubmitAsync(Dashboard, ConsideredForm);

        Assert.Equal(Morning.AddMinutes(10), MarkedAt(application, Ada, "live_considered_at"));
        Assert.DoesNotContain("setup-checklist", considered);
        Assert.DoesNotContain("Getting started", considered);
        Assert.Contains("<h1>Dashboard</h1>", considered);

        // For good: its user deletes the agent and the keys, and is no first-time user for it.
        Assert.Equal(AgentBrowser.AgentsPage, (await ada.Browser.DeleteAgentAsync(agent)).Path);
        await ada.Browser.DeleteAiKeyAsync("claude");
        await ada.Browser.DeleteExchangeKeyAsync("demo");

        var emptied = Read(await ada.Browser.PageAsync(Dashboard));

        Assert.Contains("<p>You have no agent in Demo.</p>", emptied);
        Assert.DoesNotContain("setup-checklist", emptied);

        // What was said first stands: the times are those of the two forms.
        Assert.Equal(Morning.AddMinutes(5), MarkedAt(application, Ada, "decisions_reviewed_at"));
        Assert.Equal(Morning.AddMinutes(10), MarkedAt(application, Ada, "live_considered_at"));
    }

    [Fact]
    public async Task A_form_that_is_not_on_the_page_marks_nothing_and_neither_does_one_without_the_token_of_the_page()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Swing");

        // No team has decided: neither form is on the page, and what posts one is refused.
        var token = Browser.AntiforgeryTokenIn(await ada.Browser.PageAsync(Dashboard));

        foreach (var form in new[] { ReviewedForm, ConsideredForm })
        {
            using var notThere = await ada.Browser.SendFormAsync(Dashboard, form, token);

            Assert.Equal(HttpStatusCode.BadRequest, notThere.StatusCode);
        }

        // With a decision, the form of the review is there, and that of Live is not yet.
        await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-1));

        token = Browser.AntiforgeryTokenIn(await ada.Browser.PageAsync(Dashboard));

        using (var early = await ada.Browser.SendFormAsync(Dashboard, ConsideredForm, token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        }

        // A form that comes without the token of the page is refused (NFR-SEC-4).
        using (var forged = await ada.Browser.SendFormAsync(Dashboard, ReviewedForm, "not the token of the page"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        Assert.False(IsMarked(application, Ada, "decisions_reviewed_at"));
        Assert.False(IsMarked(application, Ada, "live_considered_at"));
    }

    [Fact]
    public async Task Nothing_of_another_user_counts_and_a_mark_is_of_its_user_alone()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);

        // Grace has keys, an agent, and a decision. Ada has nothing.
        await application.GiveKeysAsync(grace.User.Id, Morning.AddDays(-1));
        var hers = await application.GiveAgentAsync(grace.User.Id, Morning.AddDays(-1), name: "Told to Grace");
        await application.GiveCycleAsync(grace.User.Id, hers, Morning.AddHours(-1));

        var page = Read(await ada.Browser.PageAsync(Dashboard));

        Assert.All(StepsOf(ChecklistOf(page)), step => Assert.Equal(NotDone, step.State));
        Assert.DoesNotContain("Told to Grace", page);
        Assert.Empty(FormsOn(page));

        // Grace marks both. The marks are of her account, and Ada is shown her checklist as it was.
        await grace.Browser.SubmitAsync(Dashboard, ReviewedForm);
        var (_, gone) = await grace.Browser.SubmitAsync(Dashboard, ConsideredForm);

        Assert.DoesNotContain("setup-checklist", gone);
        Assert.True(IsMarked(application, Grace, "decisions_reviewed_at"));
        Assert.True(IsMarked(application, Grace, "live_considered_at"));
        Assert.False(IsMarked(application, Ada, "decisions_reviewed_at"));
        Assert.False(IsMarked(application, Ada, "live_considered_at"));

        Assert.All(StepsOf(ChecklistOf(Read(await ada.Browser.PageAsync(Dashboard)))), step => Assert.Equal(NotDone, step.State));
    }

    [Fact]
    public async Task A_switch_of_an_agent_to_Live_does_the_last_step_and_a_user_who_went_to_Live_reviews_what_was_decided_there()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A Binance key for Live, and an agent, which is in Demo as every new one is.
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        var agent = Assert.Single(StoredAgent.All(application.Database));

        var steps = StepsOf(ChecklistOf(Read(await client.PageAsync(Dashboard))));

        // A Binance key for Live is not the key that a Demo agent trades with.
        Assert.Equal([NotDone, Done, NotDone, NotDone], steps.Select(step => step.State));
        Assert.False(IsMarked(application, Ada, "live_considered_at"));

        // The user switches the agent to Live, which is to have considered Live: the last step
        // is done by the switch, as of the switch.
        clock.Set(Morning.AddMinutes(5));
        await client.SwitchToLiveAsync(agent.Id);

        Assert.Equal("Live", StoredAgent.All(application.Database).Single().Mode);
        Assert.Equal(Morning.AddMinutes(5), MarkedAt(application, Ada, "live_considered_at"));

        var page = Read(await client.PageAsync(Dashboard));
        steps = StepsOf(ChecklistOf(page));

        Assert.Equal([NotDone, Done, NotDone, Done], steps.Select(step => step.State));
        Assert.Contains("You considered Live.", steps[3].Says);
        Assert.DoesNotContain(ConsideredForm, page);

        // The first time stands: the agent is switched back, and to Live again later.
        clock.Set(Morning.AddMinutes(20));
        await client.SwitchToDemoAsync(agent.Id);
        await client.ConfirmIdentityAsync();
        await client.SwitchToLiveAsync(agent.Id);

        Assert.Equal("Live", StoredAgent.All(application.Database).Single().Mode);
        Assert.Equal(Morning.AddMinutes(5), MarkedAt(application, Ada, "live_considered_at"));

        // The checklist is still shown: the decisions are not reviewed. A user who has none in
        // Demo reviews what a team of theirs decided in Live.
        var trend = await application.GiveAgentAsync(user.Id, Morning, name: "Trend", mode: TradingMode.Live);
        await application.GiveCycleAsync(user.Id, trend, Morning.AddMinutes(15));

        page = Read(await client.PageAsync(Dashboard));
        steps = StepsOf(ChecklistOf(page));

        Assert.Contains($"<a href=\"/agents/{trend}/state\">Read the decisions of Trend</a>", steps[2].Says);
        Assert.Equal([ReviewedForm], FormsOn(page));

        // Both are said then, and the checklist is shown no more, though the user never stored
        // a Binance key for Demo.
        var (_, after) = await client.SubmitAsync(Dashboard, ReviewedForm);

        Assert.DoesNotContain("setup-checklist", after);
        Assert.True(IsMarked(application, Ada, "decisions_reviewed_at"));
    }

    /// <summary>A step of the checklist, as the page writes it.</summary>
    /// <param name="Key">Which item of the list it is.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="State">Whether it is done, as the word that the page says.</param>
    /// <param name="Says">What follows the word: what the step says, with its links and its form.</param>
    private sealed record Step(string Key, string Name, string State, string Says);

    /// <summary>A page as it reads, without the marks that the framework leaves between its parts.</summary>
    private static string Read(string page) => AgentBrowser.Read(page).Replace("<!--!-->", string.Empty, StringComparison.Ordinal);

    /// <summary>The checklist of a page, which is on it once.</summary>
    private static string ChecklistOf(string page) => Assert.Single(Checklist().Matches(page)).Value;

    /// <summary>The steps of a checklist, in its order. The word of a step is also what the script of the pages is told that it says now.</summary>
    private static IReadOnlyList<Step> StepsOf(string checklist) =>
    [
        .. StepItem().Matches(checklist).Select(step =>
        {
            Assert.Equal($"{step.Groups[3].Value}.", step.Groups[4].Value);

            return new Step(step.Groups[1].Value, step.Groups[2].Value, step.Groups[3].Value, step.Groups[5].Value);
        }),
    ];

    /// <summary>The names of the forms of the checklist that a page has, in the order that they are on it.</summary>
    private static IReadOnlyList<string> FormsOn(string page) =>
        [.. Regex.Matches(page, "name=\"_handler\" value=\"(setup-[^\"]+)\"").Select(form => form.Groups[1].Value)];

    /// <summary>Whether the row of an account holds a mark.</summary>
    private static bool IsMarked(AccountsApplication application, string email, string column) =>
        application.Database.Scalar<bool>($"SELECT {column} IS NOT NULL FROM users WHERE email = '{email}'");

    /// <summary>When the row of an account says that a mark was set.</summary>
    private static DateTimeOffset MarkedAt(AccountsApplication application, string email, string column) =>
        new(application.Database.Scalar<DateTime>($"SELECT {column} FROM users WHERE email = '{email}'"), TimeSpan.Zero);

    [GeneratedRegex("<section class=\"setup-checklist\".*?</section>", RegexOptions.Singleline)]
    private static partial Regex Checklist();

    [GeneratedRegex(
        "<li class=\"setup-step\" data-key=\"([^\"]+)\">\\s*<h3>([^<]+)</h3>\\s*<p><strong data-change=\"([^\"]+)\">([^<]+)</strong>(.*?)</li>",
        RegexOptions.Singleline)]
    private static partial Regex StepItem();
}
