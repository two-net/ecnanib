using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.DecisionEngine;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Retention;

/// <summary>
/// The equity of an agent over time is thinned to one row for a day after 90 days: a scheduled
/// job deletes the other rows of such a day in small batches, found by an index (NFR-DAT-5).
/// See docs/retention.md.
/// </summary>
public class EquityThinningTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.DecisionEngine.EquityThinningJob";
    private const string Summary = "{Snapshots} rows of the equity of agents over time were deleted, of days that keep one row each";

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    // The start of the first day of which every row is kept: 90 days before the day of Now.
    // Every day before it keeps one row.
    private static readonly DateTimeOffset FirstWholeDay = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_equity_of_the_agents_is_thinned_by_a_job_every_hour()
    {
        using var application = Application();
        var job = application.EquityThinning;

        // The application registers it with the background work. The scheduler of a test does
        // not run it: see EcnanibApplicationFactory.
        Assert.Equal("equity-thinning", job.Name);
        Assert.Equal("every 01:00:00", job.Schedule.ToString());
        Assert.Equal(Now.AddHours(1), job.Schedule.NextRun(Now, Now.AddMinutes(30)));

        // The day that the tests count from is the one that the job counts from.
        Assert.Equal(new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-90), FirstWholeDay);
    }

    [Fact]
    public async Task A_day_of_longer_ago_than_90_days_keeps_the_row_that_it_ended_with()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.Id, FirstWholeDay.AddDays(-60));
        var agentOfGrace = await application.GiveAgentAsync(grace.Id, FirstWholeDay.AddDays(-60));

        var longAgo = FirstWholeDay.AddDays(-30);
        var quiet = FirstWholeDay.AddDays(-10);
        var lastThinned = FirstWholeDay.AddDays(-1);

        await application.GiveEquityAsync(
            ada.Id,
            agent,

            // A day of long ago with three rows: the latest stays.
            (longAgo.AddHours(1), 1001m),
            (longAgo.AddHours(12), 1002m),
            (longAgo.AddHours(23), 1003m),

            // A day with one row keeps it.
            (quiet.AddHours(5), 1010m),

            // The last day that is thinned, from its first moment to its last second.
            (lastThinned, 1020m),
            (lastThinned.AddDays(1).AddSeconds(-1), 1021m),

            // The first day of which every row is kept, from its first moment on.
            (FirstWholeDay, 1030m),
            (FirstWholeDay.AddHours(1), 1031m),
            (FirstWholeDay.AddHours(2), 1032m),

            // And what is recent.
            (Now.AddHours(-2), 1040m),
            (Now.AddHours(-1), 1041m));

        // Another agent, of another user, on the same day of long ago: its latest row is its
        // own, and is earlier than rows of the first agent that went.
        await application.GiveEquityAsync(
            grace.Id,
            agentOfGrace,
            (longAgo.AddHours(2), 2001m),
            (longAgo.AddHours(3), 2002m));

        await application.ThinEquityAsync();

        Assert.Equal(
            [1003m, 1010m, 1021m, 1030m, 1031m, 1032m, 1040m, 1041m],
            (await StoredAsync(application, agent)).Select(row => row.Equity));
        Assert.Equal([2002m], (await StoredAsync(application, agentOfGrace)).Select(row => row.Equity));

        // The job says how many went, once.
        var summary = Assert.Single(application.Summaries(Area));

        Assert.Equal(Summary, summary.Template);
        Assert.Equal("Information", summary.Level);
        Assert.Equal(4, summary.Count("Snapshots"));

        // A run that finds nothing deletes nothing, and says nothing.
        await application.ThinEquityAsync();

        Assert.Equal(9, application.Count("equity_snapshots"));
        Assert.Single(application.Summaries(Area));
    }

    [Fact]
    public async Task A_day_keeps_a_row_for_each_mode_and_each_asset_of_an_agent()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, FirstWholeDay.AddDays(-60));
        var day = FirstWholeDay.AddDays(-30);

        // An agent that traded in two modes on a day, and whose equity changed its asset on it:
        // what it did in one is never combined with what it did in the other (FR-BIN-11).
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            context.AddRange(
                new EquitySnapshot(agent, ada.Id, TradingMode.Demo, day.AddHours(10), "USDT", 1000m, 1001m),
                new EquitySnapshot(agent, ada.Id, TradingMode.Demo, day.AddHours(12), "USDT", 1000m, 1002m),
                new EquitySnapshot(agent, ada.Id, TradingMode.Live, day.AddHours(9), "USDT", 1000m, 2001m),
                new EquitySnapshot(agent, ada.Id, TradingMode.Live, day.AddHours(11), "USDT", 1000m, 2002m),
                new EquitySnapshot(agent, ada.Id, TradingMode.Demo, day.AddHours(13), "USDC", 1000m, 3001m),
                new EquitySnapshot(agent, ada.Id, TradingMode.Demo, day.AddHours(14), "USDC", 1000m, 3002m));

            await context.SaveChangesAsync(Cancellation);
        }

        await application.ThinEquityAsync();

        Assert.Equal(
            [(TradingMode.Live, "USDT", 2002m), (TradingMode.Demo, "USDT", 1002m), (TradingMode.Demo, "USDC", 3002m)],
            (await StoredAsync(application, agent)).Select(row => (row.Mode, row.Asset, row.Equity)));
    }

    [Fact]
    public async Task A_later_run_thins_the_days_that_have_come_of_age_since()
    {
        var clock = new TestClock(Now);

        using var application = Application(clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, FirstWholeDay.AddDays(-60));

        // Two days of which every row is kept as yet.
        var soon = FirstWholeDay.AddDays(1);
        var later = FirstWholeDay.AddDays(3);

        await application.GiveEquityAsync(
            ada.Id,
            agent,
            (soon.AddHours(1), 1001m),
            (soon.AddHours(2), 1002m),
            (soon.AddHours(3), 1003m),
            (later.AddHours(1), 1011m),
            (later.AddHours(2), 1012m));

        await application.ThinEquityAsync();

        Assert.Equal(5, application.Count("equity_snapshots"));
        Assert.Empty(application.Summaries(Area));

        // Two days on, the first of them is of longer ago than 90 days, and the second is not.
        clock.Set(Now.AddDays(2));
        await application.ThinEquityAsync();

        Assert.Equal([1003m, 1011m, 1012m], (await StoredAsync(application, agent)).Select(row => row.Equity));

        // Within the same day no further day comes of age: a run of the next hour asks for nothing.
        var asked = application.DeletesFrom("equity_snapshots").Count;

        clock.Set(Now.AddDays(2).AddHours(1));
        await application.ThinEquityAsync();

        Assert.Equal(asked, application.DeletesFrom("equity_snapshots").Count);

        // Two more days on, the second is of age as well.
        clock.Set(Now.AddDays(4));
        await application.ThinEquityAsync();

        Assert.Equal([1003m, 1012m], (await StoredAsync(application, agent)).Select(row => row.Equity));
        Assert.Equal([2, 1], application.Summaries(Area).Select(summary => summary.Count("Snapshots")));
    }

    [Fact]
    public async Task An_instance_that_starts_anew_goes_through_every_day_again()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, FirstWholeDay.AddDays(-60));
        var day = FirstWholeDay.AddDays(-30);

        await application.GiveEquityAsync(ada.Id, agent, (day.AddHours(1), 1001m), (day.AddHours(23), 1003m));
        await application.ThinEquityAsync();

        Assert.Equal([1003m], (await StoredAsync(application, agent)).Select(row => row.Equity));

        // Rows of a day that this instance has gone through, as a run that was cut short would
        // have left them. The service writes no row into the past, so the instance does not
        // read the day again.
        await application.GiveEquityAsync(ada.Id, agent, (day.AddHours(2), 1002m));
        await application.ThinEquityAsync();

        Assert.Equal([1002m, 1003m], (await StoredAsync(application, agent)).Select(row => row.Equity));

        // An instance that starts remembers nothing, and goes through every day.
        using var restarted = new AccountsApplication(clock: new TestClock(Now), database: application.Database);

        await restarted.ThinEquityAsync();

        Assert.Equal([1003m], (await StoredAsync(restarted, agent)).Select(row => row.Equity));
    }

    [Fact]
    public async Task The_rows_of_a_day_are_deleted_in_small_batches()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, FirstWholeDay.AddDays(-60));
        var day = FirstWholeDay.AddDays(-1);

        // A full batch of rows and some more on one day, a second apart: more than an agent
        // leaves in a day, which is one row in a quarter of an hour at the most.
        const int recorded = AgedRows.BatchSize + 100;

        application.Database.Execute(
            $"""
            INSERT INTO equity_snapshots (agent_id, user_id, mode, at, asset, capital, equity)
            SELECT '{agent}', '{ada.Id}', 'Demo', '{day:O}'::timestamptz + make_interval(secs => n), 'USDT', 1000, 1000 + n
            FROM generate_series(1, {recorded}) AS n
            """);

        await application.ThinEquityAsync();

        // The latest of them stays.
        var kept = Assert.Single(await StoredAsync(application, agent));

        Assert.Equal(day.AddSeconds(recorded), kept.At);
        Assert.Equal(recorded - 1, Assert.Single(application.Summaries(Area)).Count("Snapshots"));

        // A full batch and what was left, each a statement of its own with a limit.
        var statements = application.DeletesFrom("equity_snapshots");

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement => Assert.Contains("LIMIT", statement));
    }

    [Fact]
    public async Task The_rows_of_every_agent_are_found_by_their_time()
    {
        using var application = Application();

        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        Assert.Contains(
            context.Model.FindEntityType(typeof(EquitySnapshot))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(EquitySnapshot.At)]));

        // The database has it as the model says it, of every row: the job goes by the day, and
        // the key of the table starts with an agent.
        Assert.EndsWith("USING btree (at)", application.IndexDefinition("equity_snapshots", "ix_equity_snapshots_at"));
    }

    /// <summary>The application at <see cref="Now"/>, or at what a clock of the test says.</summary>
    private static AccountsApplication Application(TestClock? clock = null) => new(clock: clock ?? new TestClock(Now));

    /// <summary>The rows of the equity of an agent that are stored, in the order of time.</summary>
    private static async Task<IReadOnlyList<EquitySnapshot>> StoredAsync(AccountsApplication application, Guid agentId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<EquitySnapshot>()
            .AsNoTracking()
            .Where(snapshot => snapshot.AgentId == agentId)
            .OrderBy(snapshot => snapshot.At)
            .ToListAsync(Cancellation);
    }
}
