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
/// What the roles answered in a cycle is kept for the days of <c>Retention:TranscriptDays</c>,
/// and the record of a cycle for those of <c>Retention:DecisionDays</c>. A scheduled job deletes
/// the older ones in small batches, found by an index. Orders, fills, positions, and lessons are
/// kept indefinitely (NFR-DAT-5). See docs/retention.md.
/// </summary>
public class RecordRetentionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.DecisionEngine.RecordRetentionJob";
    private const string Summary = "{Calls} calls of roles and {Cycles} records of cycles were deleted, which were kept no longer";

    // What the file of these tests keeps each for, which is not what the tracked file says.
    private const int CallsKeptDays = 30;
    private const int CyclesKeptDays = 180;

    // What hangs on a cycle, by a key that deletes it with the cycle.
    private static readonly string[] OfACycle = ["role_outputs", "risk_checks", "analyst_calls", "shadow_trades"];

    // What is kept indefinitely, and what hangs on it.
    private static readonly string[] KeptForGood =
        ["orders", "fills", "positions", "position_results", "position_roles", "fundings", "lessons"];

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_records_of_cycles_that_are_kept_no_longer_are_deleted_by_a_job_every_hour()
    {
        using var application = Application();
        var job = application.RecordRetention;

        // The application registers it with the background work. The scheduler of a test does
        // not run it: see EcnanibApplicationFactory.
        Assert.Equal("record-retention", job.Name);
        Assert.Equal("every 01:00:00", job.Schedule.ToString());
        Assert.Equal(Now.AddHours(1), job.Schedule.NextRun(Now, Now.AddMinutes(30)));
    }

    [Fact]
    public async Task A_call_of_a_role_is_deleted_when_it_is_older_than_the_calls_are_kept()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-200));
        var agentOfGrace = await application.GiveAgentAsync(grace.Id, Now.AddDays(-200));
        var limit = Now.AddDays(-CallsKeptDays);

        // A cycle with one call each, of every user: a call goes by when it was made.
        var recent = await CycleWithCallAsync(application, ada.Id, agent, Now.AddHours(-1));
        var atTheLimit = await CycleWithCallAsync(application, ada.Id, agent, limit);
        var past = await CycleWithCallAsync(application, ada.Id, agent, limit.AddSeconds(-1));
        var ofGrace = await CycleWithCallAsync(application, grace.Id, agentOfGrace, limit.AddDays(-20));

        await application.DeleteAgedRecordsAsync();

        // One that is exactly as old as they are kept stays, and one that is a second older goes.
        Assert.Equal(new[] { recent, atTheLimit }.Order(), (await CyclesWithCallsAsync(application)).Order());

        // The cycles are all younger than the records of cycles are kept: each is still there,
        // without what its roles answered where that went.
        Assert.Equal(
            new[] { recent, atTheLimit, past, ofGrace }.Order(),
            (await CyclesAsync(application)).Order());

        // The job says how many went, once.
        var summary = Assert.Single(application.Summaries(Area));

        Assert.Equal(Summary, summary.Template);
        Assert.Equal("Information", summary.Level);
        Assert.Equal(2, summary.Count("Calls"));
        Assert.Equal(0, summary.Count("Cycles"));

        // A run that finds nothing deletes nothing, and says nothing.
        await application.DeleteAgedRecordsAsync();

        Assert.Equal(2, application.Count("role_outputs"));
        Assert.Single(application.Summaries(Area));
    }

    [Fact]
    public async Task The_record_of_a_cycle_is_deleted_with_what_hangs_on_it_when_it_is_older_than_records_are_kept()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-400));
        var limit = Now.AddDays(-CyclesKeptDays);

        // Two cycles of long ago, each with all that a cycle leaves: one whose plan was
        // executed, and one whose plan was vetoed, which leaves a shadow trade. And two that
        // are kept: one that is exactly as old as records are kept, and one of yesterday.
        var executed = await application.GiveCycleAsync(ada.Id, agent, limit.AddDays(-30));
        var vetoed = await application.GiveCycleAsync(ada.Id, agent, limit.AddSeconds(-1), CycleOutcome.Vetoed);
        var atTheLimit = await application.GiveCycleAsync(ada.Id, agent, limit, CycleOutcome.Vetoed);
        var recent = await application.GiveCycleAsync(ada.Id, agent, Now.AddDays(-1));

        // Each of the two that go has a row in every table that hangs on a cycle, but for the
        // shadow trade, which only the vetoed one has.
        foreach (var table in OfACycle.Except(["shadow_trades"]))
        {
            Assert.True(application.Count(table, $"cycle_id = '{executed}'") > 0, $"The executed cycle has no row in {table}.");
            Assert.True(application.Count(table, $"cycle_id = '{vetoed}'") > 0, $"The vetoed cycle has no row in {table}.");
        }

        Assert.Equal(1, application.Count("shadow_trades", $"cycle_id = '{vetoed}'"));
        Assert.True(application.Count("risk_check_rules") > 0);

        var keptForGood = KeptForGood.ToDictionary(table => table, table => application.Count(table));
        var rulesOfTheKept = application.Count(
            "risk_check_rules", $"check_id IN (SELECT id FROM risk_checks WHERE cycle_id IN ('{atTheLimit}', '{recent}'))");

        Assert.All(keptForGood, stored => Assert.True(stored.Value > 0, $"Nothing is stored in {stored.Key}."));

        await application.DeleteAgedRecordsAsync();

        // One that is exactly as old as they are kept stays, and one that is a second older goes.
        Assert.Equal(new[] { atTheLimit, recent }.Order(), (await CyclesAsync(application)).Order());

        // What hung on the two went with them, and the rules of their checks with the checks.
        foreach (var table in OfACycle)
        {
            Assert.Equal(0, application.Count(table, $"cycle_id IN ('{executed}', '{vetoed}')"));
        }

        Assert.Equal(rulesOfTheKept, application.Count("risk_check_rules"));

        // The two that stay keep what hangs on them, but for the calls of the one of long ago,
        // which are older than the calls are kept.
        Assert.Equal(0, application.Count("role_outputs", $"cycle_id = '{atTheLimit}'"));
        Assert.True(application.Count("role_outputs", $"cycle_id = '{recent}'") > 0);
        Assert.Equal(2, application.Count("risk_checks"));
        Assert.Equal(1, application.Count("shadow_trades", $"cycle_id = '{atTheLimit}'"));
        Assert.True(application.Count("analyst_calls", $"cycle_id = '{atTheLimit}'") > 0);

        // Orders, fills, positions, and lessons are kept indefinitely, and so is what hangs on
        // a position: every row is still there.
        Assert.Equal(keptForGood, KeptForGood.ToDictionary(table => table, table => application.Count(table)));

        // An order goes on naming the cycle that placed it, which is gone.
        Assert.Equal(2, application.Count("orders", $"cycle_id = '{executed}'"));
        Assert.Equal(2, application.Count("orders", $"cycle_id = '{vetoed}'"));

        var summary = Assert.Single(application.Summaries(Area));

        Assert.Equal(Summary, summary.Template);
        Assert.Equal(2, summary.Count("Cycles"));
    }

    [Fact]
    public async Task A_call_is_not_kept_longer_than_its_cycle()
    {
        // A file that keeps the calls for longer than the records of the cycles.
        using var application = Application(callsKeptDays: 400);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-400));

        var past = await CycleWithCallAsync(application, ada.Id, agent, Now.AddDays(-CyclesKeptDays - 1));
        var kept = await CycleWithCallAsync(application, ada.Id, agent, Now.AddDays(-CyclesKeptDays + 1));

        await application.DeleteAgedRecordsAsync();

        // The call of the cycle that went is younger than the calls are kept, and went with it.
        Assert.Equal([kept], await CyclesAsync(application));
        Assert.Equal([kept], await CyclesWithCallsAsync(application));
        Assert.Equal(0, application.Count("role_outputs", $"cycle_id = '{past}'"));

        var summary = Assert.Single(application.Summaries(Area));

        // It went by the key of its cycle, and is not counted as a call that was deleted.
        Assert.Equal(0, summary.Count("Calls"));
        Assert.Equal(1, summary.Count("Cycles"));
    }

    [Fact]
    public async Task The_records_of_cycles_are_deleted_in_small_batches()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Now.AddDays(-400));
        var recent = await CycleWithCallAsync(application, ada.Id, agent, Now.AddHours(-1));

        // A full batch of cycles and some more, each for a close of its own, all older than
        // records are kept: the cycle of an hour ago as many times over, of long ago.
        const int aged = AgedRows.BatchSize + 100;

        application.Database.Execute(
            $"""
            INSERT INTO decision_cycles (id, agent_id, user_id, mode, closed_at, started_at, ended_at, outcome, data_package, no_entry, kind)
            SELECT gen_random_uuid(), agent_id, user_id, mode,
                   closed_at - make_interval(days => {CyclesKeptDays}, mins => n),
                   started_at - make_interval(days => {CyclesKeptDays}, mins => n),
                   ended_at - make_interval(days => {CyclesKeptDays}, mins => n),
                   outcome, data_package, no_entry, kind
            FROM decision_cycles CROSS JOIN generate_series(1, {aged}) AS n
            WHERE id = '{recent}'
            """);

        await application.DeleteAgedRecordsAsync();

        Assert.Equal([recent], await CyclesAsync(application));
        Assert.Equal(aged, Assert.Single(application.Summaries(Area)).Count("Cycles"));

        // A full batch and what was left, each a statement of its own with a limit.
        var statements = application.DeletesFrom("decision_cycles");

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement => Assert.Contains("LIMIT", statement));

        // The calls were asked for once: there was none to delete, so one batch said so.
        Assert.Single(application.DeletesFrom("role_outputs"));
    }

    [Fact]
    public async Task The_calls_and_the_cycles_of_every_user_are_found_by_their_time()
    {
        using var application = Application();

        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        Assert.Contains(
            context.Model.FindEntityType(typeof(RoleOutput))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(RoleOutput.CalledAt)]));
        Assert.Contains(
            context.Model.FindEntityType(typeof(DecisionCycle))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(DecisionCycle.ClosedAt)]));

        // The database has them as the model says them, of every row: the job asks by no owner.
        Assert.EndsWith(
            "USING btree (called_at)",
            application.IndexDefinition("role_outputs", "ix_role_outputs_called_at"));
        Assert.EndsWith(
            "USING btree (closed_at)",
            application.IndexDefinition("decision_cycles", "ix_decision_cycles_closed_at"));
    }

    /// <summary>The application at <see cref="Now"/>, with what its file keeps the calls and the cycles for.</summary>
    private static AccountsApplication Application(int callsKeptDays = CallsKeptDays) =>
        new(
            clock: new TestClock(Now),
            settings: file => file
                .With("Retention:TranscriptDays", callsKeptDays)
                .With("Retention:DecisionDays", CyclesKeptDays));

    /// <summary>
    /// Gives an agent a cycle with one call, which was made at a moment: the cycle is for the
    /// close of that moment, and has nothing else.
    /// </summary>
    private static Task<Guid> CycleWithCallAsync(AccountsApplication application, Guid userId, Guid agentId, DateTimeOffset calledAt) =>
        application.GiveCallsAsync(userId, agentId, calledAt, [new GivenCall(Role.MarketAnalyst, calledAt)]);

    /// <summary>The ids of the cycles that are stored, of every user.</summary>
    private static async Task<IReadOnlyList<Guid>> CyclesAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<DecisionCycle>().AsNoTracking().Select(cycle => cycle.Id).ToListAsync(Cancellation);
    }

    /// <summary>The ids of the cycles that a call is stored for, of every user.</summary>
    private static async Task<IReadOnlyList<Guid>> CyclesWithCallsAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<RoleOutput>().AsNoTracking().Select(output => output.CycleId).Distinct().ToListAsync(Cancellation);
    }
}
