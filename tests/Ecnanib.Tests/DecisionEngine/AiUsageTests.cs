using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// AI usage is shown per agent, provider, and day: calls, tokens, and estimated cost where
/// pricing is known (FR-RPT-3), and for each of the user's AI keys (FR-KEY-11). It is worked
/// out from the recorded calls of the roles (FR-TEAM-8). Where pricing is not known, the cost
/// is unknown, and not zero. See "AI usage" in docs/team.md.
/// </summary>
/// <remarks>
/// The file of these tests gives the model of the agents' teams a price of 3 dollars for a
/// million tokens of a prompt and 15 for a million of an answer, and no other model one.
/// </remarks>
public class AiUsageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // A model of Claude that the file of these tests gives no price, and one of Grok.
    private const string Unpriced = "claude-unpriced-model";
    private const string GrokModel = "grok-test-model";

    // Noon of a day, UTC: the day is "today", and has a midnight behind it.
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");
    private static readonly AiProviderCode Grok = new("grok");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Usage_is_added_up_by_day_agent_and_provider_with_a_cost_where_the_model_has_a_price()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var first = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "First");
        var second = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "Second", symbol: "ETHUSDT");

        // The first agent today: two calls of a model of Claude, and one of Grok that searched.
        await application.GiveCallsAsync(ada.Id, first, Today.AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9).AddSeconds(3)),
            new GivenCall(Role.SentimentAnalyst, Today.AddHours(9).AddSeconds(3), 2000, 300, Provider: "grok", Model: GrokModel, LiveSearches: 2),
            new GivenCall(Role.Strategist, Today.AddHours(9).AddSeconds(9), 3000, 500),
        ]);

        // The first agent yesterday, and the second agent today: one call of Claude each.
        await application.GiveCallsAsync(ada.Id, first, Today.AddDays(-1).AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-1).AddHours(9))]);
        await application.GiveCallsAsync(ada.Id, second, Today.AddHours(8), [new GivenCall(Role.MarketAnalyst, Today.AddHours(8), 1000, 100)]);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);

        // The period is today and the 29 days before.
        Assert.Equal(Today.AddDays(-29), usage.From);

        // One row for each day, agent, and provider that a call was made in, the latest day
        // first. The tokens of Claude have a price: 3 for a million in, and 15 for a million out.
        Assert.Equal(
            [
                (Today, first, "claude", new UsageFigures(2, 0, 4200, 650, 0, 0.02235m)),
                (Today, first, "grok", new UsageFigures(1, 0, 2000, 300, 2, EstimatedCost: null)),
                (Today, second, "claude", new UsageFigures(1, 0, 1000, 100, 0, 0.0045m)),
                (Today.AddDays(-1), first, "claude", new UsageFigures(1, 0, 1200, 150, 0, 0.00585m)),
            ],
            Rows(usage, first, second));
        Assert.Equal(usage.Days.OrderByDescending(day => day.Day).Select(day => day.Day), usage.Days.Select(day => day.Day));

        // And one for each provider, over the period, by the provider's code, with its name.
        Assert.Equal(
            [
                (Claude, "Claude", new UsageFigures(4, 0, 6400, 900, 0, 0.0327m)),
                (Grok, "Grok", new UsageFigures(1, 0, 2000, 300, 2, EstimatedCost: null)),
            ],
            usage.Providers.Select(provider => (provider.Provider, provider.ProviderName, provider.Figures)));
    }

    [Fact]
    public async Task A_day_is_a_UTC_day_whatever_time_zone_the_database_goes_by()
    {
        // A database whose sessions are 14 hours ahead of UTC, which is as far as a zone goes.
        // There the second before midnight UTC, midnight UTC, and ten in the morning UTC are the
        // 23rd, the 23rd, and the 24th: by the UTC day they are the 22nd, the 23rd, and the 23rd.
        var database = PostgresServer.CreateDatabase();
        database.Execute($"""ALTER DATABASE "{database.Name}" SET timezone TO 'Pacific/Kiritimati'""");

        using var application = Application(database);
        Assert.Equal("Pacific/Kiritimati", await SessionTimeZoneAsync(application));

        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9));

        await application.GiveCallsAsync(ada.Id, agent, Today.AddHours(-1), [new GivenCall(Role.MarketAnalyst, Today.AddSeconds(-1))]);
        await application.GiveCallsAsync(ada.Id, agent, Today, [new GivenCall(Role.MarketAnalyst, Today)]);
        await application.GiveCallsAsync(ada.Id, agent, Today.AddHours(10), [new GivenCall(Role.MarketAnalyst, Today.AddHours(10))]);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);

        Assert.Equal([(Today, 2), (Today.AddDays(-1), 1)], usage.Days.Select(day => (day.Day, day.Figures.Calls)));
        Assert.All(usage.Days, day => Assert.Equal(TimeSpan.Zero, day.Day.Offset));
    }

    [Fact]
    public async Task A_call_that_was_not_made_is_no_call_and_one_that_was_not_counted_is_one()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9));

        // A cycle whose Market Analyst timed out and was called once more, whose Sentiment
        // Analyst had no key to be called with, and whose Strategist was refused by the provider.
        await application.GiveCallsAsync(ada.Id, agent, Today.AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9).AddSeconds(3), null, null, RoleCallOutcome.TimedOut),
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9).AddSeconds(63), 1200, 150, Attempt: 2),
            new GivenCall(Role.SentimentAnalyst, Today.AddHours(9).AddSeconds(3), null, null, RoleCallOutcome.NoKey),
            new GivenCall(Role.Strategist, Today.AddHours(9).AddSeconds(70), null, null, RoleCallOutcome.AuthenticationFailed),
        ]);

        // Two cycles whose calls were held back, while the calls to the provider were paused,
        // and while the calls with the key were: nothing was sent, so nothing can be charged for
        // (NFR-REL-4).
        await application.GiveCallsAsync(ada.Id, agent, Today.AddHours(9).AddMinutes(15),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9).AddMinutes(15), null, null, RoleCallOutcome.ProviderPaused),
            new GivenCall(Role.SentimentAnalyst, Today.AddHours(9).AddMinutes(15), null, null, RoleCallOutcome.KeyPaused),
        ]);

        // A test cycle is charged for as any cycle is: its call counts.
        await application.GiveCallsAsync(
            ada.Id, agent, Today.AddHours(10), [new GivenCall(Role.MarketAnalyst, Today.AddHours(10).AddMinutes(20), 800, 50)], CycleKind.Test);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);

        // Four calls were made. Two of them were not counted, and may be charged for all the
        // same: they are calls, and the tokens and the cost are of the other two.
        var day = Assert.Single(usage.Days);

        Assert.Equal(new UsageFigures(Calls: 4, NotCounted: 2, InputTokens: 2000, OutputTokens: 200, LiveSearches: 0, EstimatedCost: 0.009m), day.Figures);
        Assert.Equal(day.Figures, Assert.Single(usage.Providers).Figures);
    }

    [Fact]
    public async Task Where_no_call_was_counted_the_tokens_and_the_cost_are_unknown_and_not_zero()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9));

        await application.GiveCallsAsync(
            ada.Id, agent, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), null, null, RoleCallOutcome.Failed)]);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);

        Assert.Equal(
            new UsageFigures(Calls: 1, NotCounted: 1, InputTokens: null, OutputTokens: null, LiveSearches: 0, EstimatedCost: null),
            Assert.Single(usage.Days).Figures);
    }

    [Fact]
    public async Task Under_one_provider_a_model_with_no_price_makes_the_cost_unknown_and_not_smaller()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9));

        // Two models of Claude in one day: the file gives one of them a price.
        await application.GiveCallsAsync(ada.Id, agent, Today.AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9)),
            new GivenCall(Role.Strategist, Today.AddHours(9), 5000, 700, Model: Unpriced),
        ]);

        // The day before, the priced one alone.
        await application.GiveCallsAsync(ada.Id, agent, Today.AddDays(-1), [new GivenCall(Role.MarketAnalyst, Today.AddDays(-1))]);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);

        // The tokens of both are added up. What they cost is known only where every model has
        // a price: the cost of one model alone would read as the cost of the day.
        Assert.Equal(
            [
                (Today, new UsageFigures(2, 0, 6200, 850, 0, EstimatedCost: null)),
                (Today.AddDays(-1), new UsageFigures(1, 0, 1200, 150, 0, 0.00585m)),
            ],
            usage.Days.Select(day => (day.Day, day.Figures)));
        Assert.Equal(new UsageFigures(3, 0, 7400, 1000, 0, EstimatedCost: null), Assert.Single(usage.Providers).Figures);
    }

    [Fact]
    public async Task Only_the_calls_of_the_days_of_the_period_are_counted()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-200));

        // The first moment of the first day of each period, and the last moment before it.
        foreach (var called in new[]
        {
            Today.AddDays(-6), Today.AddDays(-6).AddSeconds(-1),
            Today.AddDays(-29), Today.AddDays(-29).AddSeconds(-1),
            Today.AddDays(-89), Today.AddDays(-89).AddSeconds(-1),
        })
        {
            await application.GiveCallsAsync(ada.Id, agent, called, [new GivenCall(Role.MarketAnalyst, called)]);
        }

        var week = await application.AiUsage.OfUserAsync(ada.Id, new AiUsageFilter(AgentId: null, UsagePeriod.Last7Days), Cancellation);
        var month = await application.AiUsage.OfUserAsync(ada.Id, new AiUsageFilter(AgentId: null, UsagePeriod.Last30Days), Cancellation);
        var quarter = await application.AiUsage.OfUserAsync(ada.Id, new AiUsageFilter(AgentId: null, UsagePeriod.Last90Days), Cancellation);

        Assert.Equal((Today.AddDays(-6), 1), (week.From, week.Days.Sum(day => day.Figures.Calls)));
        Assert.Equal((Today.AddDays(-29), 3), (month.From, month.Days.Sum(day => day.Figures.Calls)));
        Assert.Equal((Today.AddDays(-89), 5), (quarter.From, quarter.Days.Sum(day => day.Figures.Calls)));

        // The earliest day of a period is its first day.
        Assert.Equal(Today.AddDays(-6), week.Days[^1].Day);
        Assert.Equal(Today.AddDays(-89), quarter.Days[^1].Day);
    }

    [Fact]
    public async Task The_usage_of_one_agent_is_of_its_calls_alone()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var first = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "First");
        var second = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "Second", symbol: "ETHUSDT");

        await application.GiveCallsAsync(ada.Id, first, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);
        await application.GiveCallsAsync(ada.Id, second, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 1000, 100)]);

        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None with { AgentId = second }, Cancellation);

        // The days and the providers alike: nothing of the other agent is added up.
        Assert.Equal([(Today, second, "claude", new UsageFigures(1, 0, 1000, 100, 0, 0.0045m))], Rows(usage, first, second));
        Assert.Equal(new UsageFigures(1, 0, 1000, 100, 0, 0.0045m), Assert.Single(usage.Providers).Figures);
    }

    [Fact]
    public async Task Nothing_of_the_calls_of_another_user_is_counted()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9));
        var graces = await application.GiveAgentAsync(grace.Id, Now.AddDays(-9));

        // Both call the same model, which the catalog has once, for everyone.
        await application.GiveCallsAsync(ada.Id, adas, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);
        await application.GiveCallsAsync(grace.Id, graces, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 9000, 900)]);
        await application.GiveCallsAsync(grace.Id, graces, Today.AddHours(10), [new GivenCall(Role.MarketAnalyst, Today.AddHours(10), 9000, 900)]);

        var ofAda = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);
        var ofGrace = await application.AiUsage.OfUserAsync(grace.Id, AiUsageFilter.None, Cancellation);

        Assert.Equal([(adas, new UsageFigures(1, 0, 1200, 150, 0, 0.00585m))], ofAda.Days.Select(day => (day.AgentId, day.Figures)));
        Assert.Equal([(graces, new UsageFigures(2, 0, 18000, 1800, 0, 0.081m))], ofGrace.Days.Select(day => (day.AgentId, day.Figures)));

        // Asked for the agent of another user, a user is answered with nothing: to whoever
        // asks, it is an agent that made no call.
        var foreign = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None with { AgentId = graces }, Cancellation);

        Assert.Empty(foreign.Days);
        Assert.Empty(foreign.Providers);

        // The usage of a key is of its user's calls, though both keys are for one provider.
        var keys = new[] { new KeyInUse(Claude, Now.AddDays(-9)) };

        Assert.Equal(1, Assert.Single(await application.AiUsage.OfKeysAsync(ada.Id, keys, Cancellation)).Figures.Calls);
        Assert.Equal(2, Assert.Single(await application.AiUsage.OfKeysAsync(grace.Id, keys, Cancellation)).Figures.Calls);

        // A user who has no agent has no usage.
        var nobody = await application.AiUsage.OfUserAsync(Guid.CreateVersion7(), AiUsageFilter.None, Cancellation);

        Assert.Empty(nobody.Days);
        Assert.Empty(nobody.Providers);
    }

    [Fact]
    public async Task The_usage_of_an_agent_goes_with_the_agent()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "Kept");
        var deleted = await application.GiveAgentAsync(ada.Id, Now.AddDays(-9), name: "Deleted", symbol: "SOLUSDT");

        await application.GiveCallsAsync(ada.Id, kept, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9))]);
        await application.GiveCallsAsync(ada.Id, deleted, Today.AddHours(9), [new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 9000, 900)]);

        Assert.Equal(2, (await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation)).Providers.Single().Figures.Calls);

        // The agent is deleted, as its user deletes one: its cycles and their calls go with it.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>().Where(agent => agent.Id == deleted).ExecuteDeleteAsync(Cancellation);
        }

        // Nothing of the usage is stored: it says what the recorded calls say, and no more.
        var usage = await application.AiUsage.OfUserAsync(ada.Id, AiUsageFilter.None, Cancellation);
        var keys = await application.AiUsage.OfKeysAsync(ada.Id, [new KeyInUse(Claude, Now.AddDays(-9))], Cancellation);

        Assert.Equal([(kept, new UsageFigures(1, 0, 1200, 150, 0, 0.00585m))], usage.Days.Select(day => (day.AgentId, day.Figures)));
        Assert.Equal(new UsageFigures(1, 0, 1200, 150, 0, 0.00585m), Assert.Single(keys).Figures);
    }

    [Fact]
    public async Task The_usage_of_a_key_is_of_the_calls_to_its_provider_since_it_was_stored_and_in_the_last_30_days()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var first = await application.GiveAgentAsync(ada.Id, Now.AddDays(-200), name: "First");
        var second = await application.GiveAgentAsync(ada.Id, Now.AddDays(-200), name: "Second", symbol: "ETHUSDT");

        // The key for Claude was replaced two days ago, at noon. A call a second before was
        // made with the key before it, and one at that moment with this one.
        var replaced = Now.AddDays(-2);

        await application.GiveCallsAsync(ada.Id, first, replaced.AddHours(-1), [new GivenCall(Role.MarketAnalyst, replaced.AddSeconds(-1), 9000, 900)]);
        await application.GiveCallsAsync(ada.Id, first, replaced, [new GivenCall(Role.MarketAnalyst, replaced)]);
        await application.GiveCallsAsync(ada.Id, second, Today.AddHours(9),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(9), 1000, 100),
            new GivenCall(Role.Strategist, Today.AddHours(9), null, null, RoleCallOutcome.TimedOut),
            new GivenCall(Role.RiskManager, Today.AddHours(9), null, null, RoleCallOutcome.NoKey),
        ]);

        // Calls that were held back while the calls were paused were not made with the key.
        await application.GiveCallsAsync(ada.Id, second, Today.AddHours(10),
        [
            new GivenCall(Role.MarketAnalyst, Today.AddHours(10), null, null, RoleCallOutcome.ProviderPaused),
            new GivenCall(Role.Strategist, Today.AddHours(10), null, null, RoleCallOutcome.KeyPaused),
        ]);

        // The key for Grok is half a year old. A call of 30 days ago is before the period, and
        // one of 29 days ago is in it.
        await application.GiveCallsAsync(
            ada.Id, first, Today.AddDays(-30), [new GivenCall(Role.SentimentAnalyst, Today.AddDays(-29).AddSeconds(-1), Provider: "grok", Model: GrokModel)]);
        await application.GiveCallsAsync(
            ada.Id, first, Today.AddDays(-29), [new GivenCall(Role.SentimentAnalyst, Today.AddDays(-29), 2000, 300, Provider: "grok", Model: GrokModel, LiveSearches: 3)]);

        var usage = await application.AiUsage.OfKeysAsync(
            ada.Id,
            [new KeyInUse(Claude, replaced), new KeyInUse(Grok, Now.AddDays(-180)), new KeyInUse(new AiProviderCode("gemini"), Now.AddDays(-180))],
            Cancellation);

        Assert.Equal(
            [
                // Of both agents, from the moment the key was stored: three calls, of which one was not counted.
                new KeyUsage(Claude, replaced, SinceSaved: true, new UsageFigures(3, 1, 2200, 250, 0, 0.01035m)),

                // From the first day of the period. Its model has no price.
                new KeyUsage(Grok, Today.AddDays(-29), SinceSaved: false, new UsageFigures(1, 0, 2000, 300, 3, EstimatedCost: null)),

                // A key that no call was made with: no calls, and nothing known of them.
                new KeyUsage(new AiProviderCode("gemini"), Today.AddDays(-29), SinceSaved: false, new UsageFigures(0, 0, null, null, 0, null)),
            ],
            usage);

        // A user who has no key is answered with none.
        Assert.Empty(await application.AiUsage.OfKeysAsync(ada.Id, [], Cancellation));
    }

    [Fact]
    public async Task The_calls_of_a_user_over_a_time_are_found_by_an_index_that_carries_what_is_added_up()
    {
        using var application = Application();
        await application.RegisterVerifiedAsync(Ada);

        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        Assert.Contains(
            context.Model.FindEntityType(typeof(RoleOutput))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(RoleOutput.UserId), nameof(RoleOutput.CalledAt)]));

        // The database has it as the model says it: it can count the calls of a period from
        // the index, without the rows, which hold what the models answered.
        var definition = Assert.Single(application.Database.Column(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'role_outputs' AND indexname = 'ix_role_outputs_user_id_called_at'"));

        Assert.Contains("(user_id, called_at) INCLUDE (cycle_id, model_id, outcome, input_tokens, output_tokens, live_searches)", definition);
    }

    /// <summary>The application at <see cref="Now"/>, with a price for the model of the agents' teams.</summary>
    private static AccountsApplication Application(TestDatabase? database = null) =>
        new(
            clock: new TestClock(Now),
            database: database,
            settings: file => file.WithJson(
                "AiProviders:Claude:Prices",
                $$"""[ { "Model": "{{StoredAgentRows.TeamModel}}", "InputPerMillionTokens": 3, "OutputPerMillionTokens": 15 } ]"""));

    /// <summary>The time zone that the sessions of the application's database go by.</summary>
    private static async Task<string> SessionTimeZoneAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Database.SqlQuery<string>($"SELECT current_setting('TimeZone') AS \"Value\"").SingleAsync(Cancellation);
    }

    /// <summary>
    /// The rows of the days as a test compares them, the latest day first, and within a day the
    /// first agent before the second.
    /// </summary>
    private static IEnumerable<(DateTimeOffset Day, Guid AgentId, string Provider, UsageFigures Figures)> Rows(
        AiUsageView usage, Guid first, Guid second) =>
        usage.Days
            .OrderByDescending(day => day.Day)
            .ThenBy(day => day.AgentId == first ? 0 : day.AgentId == second ? 1 : 2)
            .ThenBy(day => day.Provider.Value, StringComparer.Ordinal)
            .Select(day => (day.Day, day.AgentId, day.Provider.Value, day.Figures));
}
