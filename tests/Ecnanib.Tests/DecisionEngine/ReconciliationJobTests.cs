using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Order and position changes are tracked by reconciliation at startup and every 5 minutes
/// (FR-BIN-7): a job reconciles every agent that holds something as the background work starts,
/// and then at the time between reconciliations of the configuration file, which is four minutes
/// in the file of the tests. On startup the application reconciles every running agent with the
/// exchange, then resumes scheduling (NFR-REL-3): an agent that holds something from before the
/// start gets no cycle until it is reconciled. See "Reconciling" in docs/orders.md, "After a
/// restart" in docs/cycles.md, and docs/background-work.md.
/// </summary>
public class ReconciliationJobTests
{
    private const string Area = "Ecnanib.DecisionEngine.ReconciliationJob";

    private const string Reconciled =
        "{Agents} agents were reconciled with the exchange, and {NotReconciled} were not: those are tried again with the next run";

    [Fact]
    public void The_agents_are_reconciled_at_the_time_between_reconciliations_of_the_configuration_file()
    {
        using var application = new OrdersApplication();
        var job = application.Application.Reconciliation;
        var started = OrdersApplication.Start;

        Assert.Equal("reconciliation", job.Name);
        Assert.Equal("every 00:04:00", job.Schedule.ToString());
        Assert.Equal(started.AddMinutes(4), job.Schedule.NextRun(started, started.AddMinutes(1)));
    }

    [Fact]
    public async Task The_agents_are_reconciled_as_the_background_work_starts_when_one_holds_anything()
    {
        using var application = new OrdersApplication();
        var job = Assert.IsAssignableFrom<IDueAtStart>(application.Application.Reconciliation);

        // An agent that holds nothing has nothing that an account could differ from.
        var agent = await application.GiveAgentAsync();

        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // An open position, on either market, is reconciled from the start: its user may have
        // closed it at the exchange while the application was down.
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.True(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_entry_that_rests_is_something_that_an_agent_holds()
    {
        using var application = new OrdersApplication();
        var job = Assert.IsAssignableFrom<IDueAtStart>(application.Application.Reconciliation);
        var agent = await application.GiveAgentAsync();

        // An entry at a price below the market rests, and has opened no position.
        await application.PlaceAsync(agent, GuardCases.OpenLong with { EntryPrice = 59900m });

        Assert.Empty(await application.PositionsOfAsync(agent.Id));
        Assert.True(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_reconciles_every_agent_that_holds_something_and_corrects_what_differs()
    {
        using var application = new OrdersApplication();
        var ada = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        var grace = await application.GiveAgentAsync("grace@example.com");
        var idle = await application.GiveAgentAsync("lin@example.com");
        await application.PlaceAsync(ada, GuardCases.OpenLongOnFutures);
        await application.PlaceAsync(grace, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        var asked = application.Binance.AskedAbout.Count;

        // Ada closes her position by hand, at Binance.
        application.Binance.HoldByHand(Market.Futures, 0m);

        await application.ReconcileAllAsync();

        // Her records say so, and those of Grace are as they were.
        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(ada.Id)).ExitReason);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(grace.Id)).ClosedAt);

        // Every order of both that was not over was asked about. An agent that holds nothing was not.
        Assert.True(application.Binance.AskedAbout.Count >= asked + 4);
        Assert.Empty(await application.OrdersOfAsync(idle.Id));

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area);
        Assert.Equal(Reconciled, said.Template);
        Assert.Equal((2, 0), (said["Agents"].GetInt32(), said["NotReconciled"].GetInt32()));
    }

    [Fact]
    public async Task A_run_with_no_agent_that_holds_anything_asks_nobody()
    {
        using var application = new OrdersApplication();
        await application.GiveAgentAsync();

        await application.ReconcileAllAsync();

        Assert.Empty(application.Binance.System.Calls);
        Assert.DoesNotContain(application.Application.Events(), written => written.Area == Area);
    }

    [Fact]
    public async Task An_agent_that_cannot_be_reconciled_keeps_no_other_waiting()
    {
        using var application = new OrdersApplication();
        var adas = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        var graces = await application.GiveAgentAsync("grace@example.com");
        await application.PlaceAsync(adas, GuardCases.OpenLongOnFutures);
        await application.PlaceAsync(graces, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // Ada has deleted her Binance key since: nothing can be asked for her agent.
        application.Application.Database.Execute($"DELETE FROM exchange_credentials WHERE user_id = '{adas.UserId}'");
        var began = application.Clock.GetUtcNow();

        await application.ReconcileAllAsync();

        // The agent of Grace was reconciled, and that of Ada was not: it was tried once more
        // before the run ended, and is tried again with the next.
        var said = Assert.Single(application.Application.Events(), written => written.Area == Area);
        Assert.Equal((1, 1), (said["Agents"].GetInt32(), said["NotReconciled"].GetInt32()));
        Assert.True(application.Clock.GetUtcNow() >= began + TimeSpan.FromSeconds(15));

        var protection = (await application.OrdersOfAsync(graces.Id)).Skip(1).ToList();
        Assert.All(protection, order => Assert.True(order.CheckedAt >= began));
        Assert.Null(Assert.Single(await application.PositionsOfAsync(adas.Id)).ClosedAt);
    }

    [Fact]
    public async Task An_agent_that_was_not_reconciled_is_tried_once_more_before_the_run_ends()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // The limiter holds the first read of the account back, and the next is answered.
        var reads = 0;
        application.Application.Providers.FuturesPosition = _ => Interlocked.Increment(ref reads) == 1
            ? ExchangeFuturesPosition.Without(ExchangeCallOutcome.RateLimited)
            : application.Binance.Position();

        await application.ReconcileAllAsync();

        Assert.Equal(2, reads);

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area);
        Assert.Equal((1, 0), (said["Agents"].GetInt32(), said["NotReconciled"].GetInt32()));
    }

    [Fact]
    public async Task After_a_restart_an_agent_that_holds_a_position_gets_no_cycle_until_it_is_reconciled()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;

        using (var before = new OrdersApplication(database: database, binance: binance))
        {
            agent = await before.GiveAgentAsync(market: Market.Futures);
            await before.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        }

        // While the application is down its user closes the position by hand, and Binance does
        // not answer as the application starts again, a few minutes later.
        binance.HoldByHand(Market.Futures, 0m);
        binance.Instead = _ => StandInSystem.Json(HttpStatusCode.ServiceUnavailable, """{ "code": -1001, "msg": "Internal error." }""");

        using var after = new OrdersApplication(database: database, binance: binance, reconciles: true);
        after.Clock.Set(OrdersApplication.Start.AddMinutes(5));
        var running = new RunningAgent(agent, []);

        // The orders of the agent are settled, as far as the rows say. But the application has
        // not reconciled the agent since it started: its records are from before, and a cycle
        // would decide on a position that the account no longer holds.
        var answers = after.Application.Reconciliations.ToList();
        Assert.Equal(2, answers.Count);
        Assert.False(await AllAsync(answers, running));

        // Binance answers again, and the agent is reconciled: the records say that the position
        // is closed, and the agent gets its cycles.
        binance.Instead = null;
        await after.ReconcileAllAsync();

        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await after.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.True(await AllAsync(answers, running));
    }

    [Fact]
    public async Task An_agent_that_holds_nothing_from_before_the_start_waits_for_nothing()
    {
        using var application = new OrdersApplication(reconciles: true);
        var idle = await application.GiveAgentAsync("ada@example.com");
        var trading = await application.GiveAgentAsync("grace@example.com");
        var answers = application.Application.Reconciliations.ToList();

        // An agent that holds nothing has nothing to reconcile.
        Assert.True(await AllAsync(answers, new RunningAgent(idle, [])));

        // What an agent comes to hold after the start is what this application placed, and saw
        // placed: its next cycle does not wait for the job.
        await application.PlaceAsync(trading, GuardCases.OpenLong);

        Assert.True(await AllAsync(answers, new RunningAgent(trading, [])));
    }

    private static async Task<bool> AllAsync(IEnumerable<IReconciliation> answers, RunningAgent agent)
    {
        foreach (var answer in answers)
        {
            if (!await answer.HasReconciledAsync(agent, TestContext.Current.CancellationToken))
            {
                return false;
            }
        }

        return true;
    }
}
