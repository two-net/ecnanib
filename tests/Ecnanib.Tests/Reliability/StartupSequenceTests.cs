using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// On startup the application migrates the database, reconciles every running agent with the
/// exchange, then resumes scheduling. A restart never creates a duplicate order (NFR-REL-3).
/// Each test here starts the application again over what an earlier one left, with the
/// background work as it runs outside the tests, and reads the order of what it did from its
/// log. What the scheduler does with a job that runs first is in <c>BackgroundJobRunsFirstTests</c>,
/// and what settles and what reconciles do is in the tests of the orders. See "Starting" in
/// docs/reliability.md.
/// </summary>
public class StartupSequenceTests
{
    private const string Migrator = "Ecnanib.Platform.Persistence.DatabaseMigrator";
    private const string UpToDate = "The database is up to date at {Migration}";

    private const string Settling = "Ecnanib.DecisionEngine.OrderSettlingJob";
    private const string Settled = "The orders of {Agents} agents are settled with the exchange";

    private const string Reconciling = "Ecnanib.DecisionEngine.ReconciliationJob";
    private const string Reconciled =
        "{Agents} agents were reconciled with the exchange, and {NotReconciled} were not: those are tried again with the next run";

    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    [Fact]
    public async Task The_application_migrates_settles_and_reconciles_before_it_schedules_anything_and_places_no_order_twice()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;
        string entry;

        using (var before = new OrdersApplication(database: database, binance: binance))
        {
            agent = await before.GiveAgentAsync();

            // The application was stopped between sending an entry and its answer.
            entry = await UnderWay.EntryAsync(before, agent);
        }

        // It starts again, on the same database and the same Binance, with the background
        // work as it runs outside the tests, and with one more job, which is due at once.
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Hourly));
        using var after = new OrdersApplication(
            settlesOrders: true,
            database: database,
            binance: binance,
            reconciles: true,
            further: services => services.AddSingleton<IBackgroundJob>(other));

        // Started now, and not when the test first asks it for something.
        _ = after.Application.Contexts;

        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waits for what settles and what reconciles");

        // In that order: the database, the orders, the accounts, and then the schedule.
        var written = after.Application.Events();
        var migrated = Index(written, said => said.Area == Migrator && said.Template == UpToDate);
        var settled = Index(written, said => said.Area == Settling && said.Template == Settled);
        var reconciled = Index(written, said => said.Area == Reconciling && said.Template == Reconciled);
        var scheduled = Index(written, said => said.Area == SchedulerLog.Area && said.Template == SchedulerLog.FirstEnded);
        var ran = Index(written, said => said.Area == SchedulerLog.Area && said.Template == SchedulerLog.Finished && said.Text("Job") == "other");

        Assert.True(
            migrated < settled && settled < reconciled && reconciled < scheduled && scheduled < ran,
            $"Migrated at {migrated}, settled at {settled}, reconciled at {reconciled}, scheduled at {scheduled}, and the other job ran at {ran}.");

        // The run that settles ended before the one that reconciles began, and both before
        // the other job began.
        Assert.True(
            Index(written, said => said.Template == SchedulerLog.Finished && said.Text("Job") == "order-settling") < reconciled);
        Assert.Equal((1, 0), (written[reconciled]["Agents"].GetInt32(), written[reconciled]["NotReconciled"].GetInt32()));

        // What settled asked Binance about the entry by its identifier, found it executed,
        // opened the position in the records, and protected it.
        Assert.Contains(entry, binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await after.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Null(Assert.Single(await after.PositionsOfAsync(agent.Id)).ClosedAt);

        // A restart never creates a duplicate order: the entry reached Binance once, before
        // the restart, and what was placed after it is the pair that protects it, with one call.
        Assert.Equal([(false, "BUY"), (true, "SELL")], binance.Asked.Select(asked => (asked.Stop, asked.Side)));
        Assert.Single(binance.Orders, order => !order.Asked.Stop);

        // The agent is settled and reconciled, so its cycles go on.
        foreach (var answer in after.Application.Reconciliations)
        {
            Assert.True(await answer.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        }

        Assert.Empty(Trouble(after));
    }

    [Fact]
    public async Task A_start_waits_two_minutes_for_an_exchange_that_does_not_answer_and_its_agents_get_no_cycle()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;

        using (var before = new OrdersApplication(database: database, binance: binance))
        {
            agent = await before.GiveAgentAsync();
            await before.PlaceAsync(agent, GuardCases.OpenLong);
        }

        // Binance does not answer as the application starts again, five minutes later.
        var restarted = OrdersApplication.Start.AddMinutes(5);
        binance.Instead = _ => StandInSystem.Json(HttpStatusCode.ServiceUnavailable, """{ "code": -1001, "msg": "Internal error." }""");

        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Hourly));
        using var after = new OrdersApplication(
            settlesOrders: true,
            database: database,
            binance: binance,
            reconciles: true,
            further: services => services.AddSingleton<IBackgroundJob>(other));

        // The clock is set before the application starts, which it does when it is first
        // asked for something: what the agent holds is from before this start.
        after.Clock.Set(restarted);
        _ = after.Application.Contexts;

        // The run that reconciles goes on: it tries the agent once more, a quarter of a minute
        // later. Nothing else has started.
        await Wait.UntilAsync(() => StoredJob.Of(database, "reconciliation") is { FinishedAt: null }, "the run that reconciles first");

        Assert.Empty(other.Runs);
        Assert.Null(StoredJob.Of(database, "other"));

        // Two minutes after the start the other jobs start beside it: no backup, no email, and
        // no alert of the outage waits for an exchange that does not answer.
        after.Clock.Set(restarted.AddMinutes(2));
        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waited for two minutes");

        var late = Assert.Single(Trouble(after), said => said.Template == SchedulerLog.FirstLate);
        Assert.Equal(("Warning", "reconciliation"), (late.Level, late.Text("Jobs")));

        // The agent is not reconciled, so it gets no cycle, and nothing is placed for it: its
        // position is protected by what rests on the exchange (NFR-REL-2).
        var answers = after.Application.Reconciliations.ToList();
        var passes = new List<bool>();

        foreach (var answer in answers)
        {
            passes.Add(await answer.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, answers.Count);
        Assert.Contains(false, passes);
        Assert.Equal([(false, "BUY"), (true, "SELL")], binance.Asked.Select(asked => (asked.Stop, asked.Side)));
    }

    /// <summary>What the scheduler of an application wrote at the warning level or above.</summary>
    private static IReadOnlyList<LoggedEvent> Trouble(OrdersApplication application) =>
        [.. application.Application.Events().Where(said => said.Area == SchedulerLog.Area && said.Level is "Warning" or "Error" or "Fatal")];

    private static int Index(IReadOnlyList<LoggedEvent> written, Func<LoggedEvent, bool> which)
    {
        for (var index = 0; index < written.Count; index++)
        {
            if (which(written[index]))
            {
                return index;
            }
        }

        Assert.Fail("The application did not say what the test looks for.");

        return -1;
    }
}
