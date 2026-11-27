using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.RiskGuard;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What a cycle could not finish is finished by a job that settles the orders with the exchange
/// every minute: an order of unknown outcome is asked about before anything else is done with it
/// (FR-BIN-6), and an open position has a stop-loss that rests, or is closed at market
/// (FR-RSK-7). On startup the application settles before a cycle of the agent starts (NFR-REL-3).
/// See "Settling" in docs/orders.md.
/// </summary>
public class OrderSettlingTests
{
    [Fact]
    public async Task The_job_runs_every_minute_and_first_as_the_work_starts_and_last_as_the_application_stops()
    {
        using var application = new OrdersApplication();
        var job = application.Application.OrderSettling;
        var now = application.Clock.GetUtcNow();

        Assert.Equal("order-settling", job.Name);
        Assert.Equal(now.AddMinutes(1), job.Schedule.NextRun(now, now));

        // A restart settles first: the scheduler runs the job before any other, whatever its
        // minute says (NFR-REL-3). And it settles once more as the application stops, when
        // the cycles have ended (NFR-REL-5). See StartupSequenceTests and GracefulShutdownTests.
        Assert.IsAssignableFrom<IRunsFirst>(job);
        Assert.IsAssignableFrom<IRunsLast>(job);

        // With nothing to settle, the run that comes first asks Binance nothing.
        var agent = await application.GiveAgentAsync();
        await application.Application.SettleOrdersAsync(now);

        Assert.Empty(application.Binance.System.Calls);

        // An entry whose outcome is unknown: the cycle was stopped as the call ended without an answer.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = _ =>
        {
            stopping.Cancel();

            return OrderFate.Lost;
        };

        await application.Orders.PlaceAsync(application.CycleOf(agent), GuardCheck.Of(GuardCases.OpenLong), stopping.Token);
        var asked = application.Binance.AskedAbout.Count;

        // An application that starts now settles first: the order is asked about by the run
        // that comes first, and not when a minute has passed.
        await application.Application.SettleOrdersAsync(now);

        Assert.True(application.Binance.AskedAbout.Count > asked);
    }

    [Fact]
    public async Task A_run_asks_Binance_nothing_while_nothing_is_to_settle_and_about_a_stop_loss_that_rests_once_in_a_while()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var calls = application.Binance.System.Calls.Count;

        // The position is protected, and its stop-loss was just placed: a run has nothing to do.
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(3));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal(calls, application.Binance.System.Calls.Count);

        // Once the stop-loss was last asked about longer ago than the time between
        // reconciliations of the configuration file, which is 4 minutes in the file of the
        // tests, it is asked about again: it may have been executed, or taken off at Binance.
        // The take-profit of its pair is asked about with it.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(2));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        var protection = (await application.OrdersOfAsync(agent.Id)).Skip(1).ToList();
        Assert.Equal([OrderPurpose.StopLoss, OrderPurpose.TakeProfit], protection.Select(order => order.Purpose));
        Assert.Equal(protection.Select(order => order.ClientOrderId), application.Binance.AskedAbout);
        Assert.All(protection, order => Assert.Equal((OrderStatus.Open, application.Clock.GetUtcNow()), (order.Status, order.CheckedAt)));

        // And not again by the next run.
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal(2, application.Binance.AskedAbout.Count);
    }

    [Fact]
    public async Task An_agent_that_cannot_be_settled_keeps_no_other_waiting()
    {
        using var application = new OrdersApplication();
        var adas = await application.GiveAgentAsync("ada@example.com");
        var graces = await application.GiveAgentAsync("grace@example.com");
        var settled = Assert.Single(application.Application.Reconciliations);

        // Both have an entry that Binance took, and whose answer never came.
        foreach (var agent in new[] { adas, graces })
        {
            using var stopping = new CancellationTokenSource();
            application.Binance.Fate = _ =>
            {
                stopping.Cancel();

                return OrderFate.TakenUnsaid;
            };

            await application.Orders.PlaceAsync(application.CycleOf(agent), GuardCheck.Of(GuardCases.OpenLong), stopping.Token);
        }

        application.Binance.Fate = null;

        // Ada has deleted her Binance key since: nothing can be asked for her agent.
        application.Application.Database.Execute($"DELETE FROM exchange_credentials WHERE user_id = '{adas.UserId}'");

        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        // The agent of Grace is settled: its entry was found, and its position is protected.
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(graces.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(graces, []), TestContext.Current.CancellationToken));

        // The agent of Ada is not, and gets no cycle: what became of its entry is still not
        // known, and nothing was sent for it.
        Assert.Equal(OrderStatus.Unknown, Assert.Single(await application.OrdersOfAsync(adas.Id)).Status);
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(adas, []), TestContext.Current.CancellationToken));
        Assert.Equal(2, application.Binance.Asked.Count(asked => !asked.Stop));
    }
}
