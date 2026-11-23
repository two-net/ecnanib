using System.Globalization;
using System.Net;
using Ecnanib.Accounts;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Experience;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// AI usage is shown per agent, provider, and day: calls, tokens, and estimated cost where
/// pricing is known (FR-RPT-3). Where pricing is not known, the cost is shown as unknown, and
/// not as zero. See "The page of the AI usage" in docs/team.md.
/// </summary>
/// <remarks>
/// The file of these tests gives the model of the agents' teams a price of 3 dollars for a
/// million tokens of a prompt and 15 for a million of an answer, and no other model one. The
/// clock stands at noon of a day, UTC.
/// </remarks>
public class UsagePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string UsagePage = "/usage";
    private const string GrokModel = "grok-test-model";

    private const string ByProvider = "The AI usage of each provider";
    private const string ByDay = "The AI usage of each day, agent, and provider";

    private static readonly string[] Figures = ["Calls", "Not counted", "Tokens in", "Tokens out", "Live searches", "Estimated cost"];

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_page_shows_the_usage_of_each_provider_and_of_each_day_agent_and_provider()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var bitcoin = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Bitcoin quarter hour");
        var ether = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Ether hour", symbol: "ETHUSDT");

        // Today the first agent called a model of Claude twice, and one of Grok that searched,
        // and the second agent called the model of Claude once. Yesterday the first did, with
        // far more tokens.
        await application.GiveCallsAsync(user.Id, bitcoin, Today.AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9).AddSeconds(3)),
            new GivenCall(Role.SentimentAnalyst, Today.AddHours(9).AddSeconds(3), 2000, 300, Provider: "grok", Model: GrokModel, LiveSearches: 2),
            new GivenCall(Role.Strategist, Today.AddHours(9).AddSeconds(9), 3000, 500),
        ]);
        await application.GiveCallsAsync(user.Id, ether, Today.AddHours(8), [new GivenCall(Role.MarketAnalyst, Today.AddHours(8), 1000, 100)]);
        await application.GiveCallsAsync(
            user.Id, bitcoin, Today.AddDays(-1).AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-1).AddHours(9), 400_000, 30_000)]);

        var page = await client.PageAsync(UsagePage);
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>AI usage</title>", page);
        Assert.Contains("<h1>AI usage</h1>", page);
        Assert.Contains("charged to your own account with its provider", read);
        Assert.Contains("The last 30 days: from 25 October 2026 until now.", read);

        // Each provider over the period. The tokens of Claude have a price, and cost 1.2 + 0.45
        // for yesterday and 0.02235 and 0.0045 for today. The model of Grok has none: what its
        // tokens cost is unknown, and is not written as nothing.
        Assert.Equal(["Provider", .. Figures], ExperienceBrowser.ColumnsOf(page, ByProvider));
        Assert.Equal(
            [
                ["Claude", "4", "0", "405200", "30750", "0", "1.68 USD"],
                ["Grok", "1", "0", "2000", "300", "2", "Unknown"],
            ],
            ExperienceBrowser.RowsOf(page, ByProvider));

        // Each day, agent, and provider, the latest day first, and the agents of a day by their
        // names. A cost of less than a cent is written to a hundredth of a cent, and not as nothing.
        Assert.Equal(["Day", "Agent", "Provider", .. Figures], ExperienceBrowser.ColumnsOf(page, ByDay));
        Assert.Equal(
            [
                ["23 November 2026", "Bitcoin quarter hour", "Claude", "2", "0", "4200", "650", "0", "0.02 USD"],
                ["23 November 2026", "Bitcoin quarter hour", "Grok", "1", "0", "2000", "300", "2", "Unknown"],
                ["23 November 2026", "Ether hour", "Claude", "1", "0", "1000", "100", "0", "0.0045 USD"],
                ["22 November 2026", "Bitcoin quarter hour", "Claude", "1", "0", "400000", "30000", "0", "1.65 USD"],
            ],
            ExperienceBrowser.RowsOf(page, ByDay));

        // The name of an agent leads to its usage alone.
        Assert.Contains($"<a href=\"/usage?agent={ether}\">Ether hour</a>", page);

        // The page says what it counts, what it leaves out, and what its estimate is.
        Assert.Contains("in a test cycle as in any other, and in Demo as in Live", read);
        Assert.Contains("The calls that write lessons are not recorded, and are not counted here.", read);
        Assert.Contains("A day is a UTC day.", read);
        Assert.Contains("A provider may charge for such a call all the same.", read);
        Assert.Contains("What a provider charges for live searches is not in it.", read);
        Assert.Contains("When an agent is deleted, its calls go with it, and so does its usage.", read);
    }

    [Fact]
    public async Task What_is_not_known_is_shown_as_unknown_and_not_as_zero()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Swing");

        // Today a call that failed, and so was not counted. Yesterday one that was counted, and
        // one that timed out. The day before, a call that was not made, which is no call.
        await application.GiveCallsAsync(
            user.Id, agent, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), null, null, RoleCallOutcome.Failed)]);
        await application.GiveCallsAsync(user.Id, agent, Today.AddDays(-1).AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddDays(-1).AddHours(9), null, null, RoleCallOutcome.TimedOut),
            new GivenCall(Role.MarketAnalyst, Today.AddDays(-1).AddHours(9).AddMinutes(1), 1_000_000, 100_000, Attempt: 2),
        ]);
        await application.GiveCallsAsync(
            user.Id, agent, Today.AddDays(-2).AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-2).AddHours(9), null, null, RoleCallOutcome.NoKey)]);

        var page = await client.PageAsync(UsagePage);

        Assert.Equal(
            [
                // Nothing was counted: the tokens and the cost are unknown, and no zero stands for them.
                ["23 November 2026", "Swing", "Claude", "1", "1", "Unknown", "Unknown", "0", "Unknown"],

                // One of two was counted: the tokens and the cost are of that one.
                ["22 November 2026", "Swing", "Claude", "2", "1", "1000000", "100000", "0", "4.50 USD"],
            ],
            ExperienceBrowser.RowsOf(page, ByDay));
        Assert.Equal([["Claude", "3", "2", "1000000", "100000", "0", "4.50 USD"]], ExperienceBrowser.RowsOf(page, ByProvider));
    }

    [Fact]
    public async Task The_page_shows_the_usage_of_the_agent_and_of_the_period_that_its_address_names()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var bitcoin = await application.GiveAgentAsync(user.Id, Now.AddDays(-40), name: "Bitcoin quarter hour");
        var ether = await application.GiveAgentAsync(user.Id, Now.AddDays(-40), name: "Ether hour", symbol: "ETHUSDT");

        await application.GiveCallsAsync(user.Id, bitcoin, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);
        await application.GiveCallsAsync(user.Id, ether, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 1000, 100)]);
        await application.GiveCallsAsync(user.Id, ether, Today.AddDays(-6), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-6), 2000, 200)]);
        await application.GiveCallsAsync(user.Id, ether, Today.AddDays(-7), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-7).AddHours(23), 3000, 300)]);
        await application.GiveCallsAsync(user.Id, bitcoin, Today.AddDays(-40), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-40))]);

        // The page starts from every agent and the last 30 days, and offers each agent by its
        // name and each period. The call of 40 days ago is before those days.
        var all = await client.PageAsync(UsagePage);

        Assert.Equal(["", bitcoin.ToString(), ether.ToString()], ExperienceBrowser.ChoicesOf(all, "agent"));
        Assert.Equal(["7", "30", "90"], ExperienceBrowser.ChoicesOf(all, "period"));
        Assert.Equal(("", "30"), (ExperienceBrowser.ChosenIn(all, "agent"), ExperienceBrowser.ChosenIn(all, "period")));
        Assert.Equal(4, ExperienceBrowser.RowsOf(all, ByDay).Count);

        // One agent, over the last 7 days: its calls alone, and none of the day before the period.
        var page = await client.PageAsync($"{UsagePage}?agent={ether}&period=7");
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>AI usage of Ether hour</title>", page);
        Assert.Contains("<h1>AI usage of Ether hour</h1>", page);
        Assert.Contains("The last 7 days: from 17 November 2026 until now.", read);
        Assert.Equal((ether.ToString(), "7"), (ExperienceBrowser.ChosenIn(page, "agent"), ExperienceBrowser.ChosenIn(page, "period")));
        Assert.Equal(
            [
                ["23 November 2026", "Ether hour", "Claude", "1", "0", "1000", "100", "0", "0.0045 USD"],
                ["17 November 2026", "Ether hour", "Claude", "1", "0", "2000", "200", "0", "0.0090 USD"],
            ],
            ExperienceBrowser.RowsOf(page, ByDay));
        Assert.Equal([["Claude", "2", "0", "3000", "300", "0", "0.01 USD"]], ExperienceBrowser.RowsOf(page, ByProvider));

        // It leads to the state of the agent, and to the usage of every agent over the same period.
        Assert.Contains($"<a href=\"/agents/{ether}/state\">State of the agent</a>", page);
        Assert.Contains("<a href=\"/usage?period=7\">AI usage of all your agents</a>", page);

        // The longest period reaches the call of 40 days ago as well, and the form sends a
        // choice of every agent as an empty one, which is none.
        var quarter = await client.PageAsync($"{UsagePage}?agent=&period=90");

        Assert.Equal(5, ExperienceBrowser.RowsOf(quarter, ByDay).Count);
        Assert.Contains("<h1>AI usage</h1>", quarter);
    }

    [Fact]
    public async Task A_user_whose_agents_made_no_call_is_told_so_and_shown_no_table()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // No agent at all, and then an agent that made no call.
        var none = await client.PageAsync(UsagePage);

        Assert.Contains("Your agents made no call to an AI model in these days.", AgentBrowser.Read(none));
        Assert.False(ExperienceBrowser.HasTable(none, ByProvider));
        Assert.False(ExperienceBrowser.HasTable(none, ByDay));
        Assert.DoesNotContain("0 USD", none);

        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Swing");
        var ofAgent = await client.PageAsync($"{UsagePage}?agent={agent}");

        Assert.Contains("This agent made no call to an AI model in these days.", AgentBrowser.Read(ofAgent));
        Assert.False(ExperienceBrowser.HasTable(ofAgent, ByDay));
    }

    [Theory]

    // A period that is none of the periods, or one of them written in another way.
    [InlineData("?period=14")]
    [InlineData("?period=030")]
    [InlineData("?period=Last30Days")]

    // An agent that is no id, an id written in another way than the page writes it, and an id
    // of no agent.
    [InlineData("?agent=swing")]
    [InlineData("?agent={1}")]
    [InlineData("?agent=6d1f0c0e-6f0e-7c1a-9e52-0a6f6d1c2b3a")]

    // A choice that is made twice.
    [InlineData("?agent={0}&agent={0}")]
    [InlineData("?period=7&period=30")]
    public async Task An_address_that_names_a_choice_which_there_is_not_is_not_found(string query)
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Swing");

        await application.GiveCallsAsync(user.Id, agent, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);

        using var answer = await client.GetAsync(
            UsagePage + string.Format(CultureInfo.InvariantCulture, query, agent, agent.ToString().ToUpperInvariant()), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task Nothing_of_the_usage_of_another_user_is_shown()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "Swing");
        var graces = await application.GiveAgentAsync(grace.Id, Now.AddDays(-9), name: "Scalp");

        await application.GiveCallsAsync(ada.Id, adas, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);
        await application.GiveCallsAsync(grace.Id, graces, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 9000, 900)]);

        await client.SignInAsync(Ada);

        // Ada reads her own usage, and nothing of Grace's: no call, no agent, and no choice of one.
        var page = await client.PageAsync(UsagePage);

        Assert.Equal([["23 November 2026", "Swing", "Claude", "1", "0", "1200", "150", "0", "0.0059 USD"]], ExperienceBrowser.RowsOf(page, ByDay));
        Assert.Equal(["", adas.ToString()], ExperienceBrowser.ChoicesOf(page, "agent"));
        Assert.DoesNotContain("Scalp", page);
        Assert.DoesNotContain(graces.ToString(), page);

        // Asked for the usage of Grace's agent, she is answered as for an agent that does not exist.
        using var answer = await client.GetAsync($"{UsagePage}?agent={graces}", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task The_bar_at_the_top_and_the_state_of_an_agent_lead_to_the_usage()
    {
        // The layout marks its elements for its own styles, so the link carries more than its address.
        const string link = "<a href=\"/usage\"[^>]*>AI usage</a>";

        using var application = Application();
        using var client = application.CreateClient();

        // A visitor could not open it, and is not shown the way.
        Assert.DoesNotMatch(link, await client.PageAsync("/"));

        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Matches(link, await client.PageAsync("/"));
        Assert.Matches(link, await client.PageAsync("/account"));
        Assert.Matches(link, await client.PageAsync(UsagePage));

        // The page of the state of an agent leads to the usage of that agent alone.
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-9), name: "Swing");

        Assert.Contains(
            $"<a href=\"/usage?agent={agent}\">AI usage of the agent</a>", await client.PageAsync(AgentBrowser.AgentStatePage(agent)));
    }

    [Theory]
    [InlineData("/usage")]
    [InlineData("/usage?period=7")]
    public async Task A_visitor_who_opens_the_page_is_sent_to_sign_in(string page)
    {
        using var application = Application();
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    /// <summary>The application at <see cref="Now"/>, with a price for the model of the agents' teams.</summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(
            new StandInMarketData(),
            new TestClock(Now),
            settings: file => file.WithJson(
                "AiProviders:Claude:Prices",
                $$"""[ { "Model": "{{StoredAgentRows.TeamModel}}", "InputPerMillionTokens": 3, "OutputPerMillionTokens": 15 } ]"""));
}
