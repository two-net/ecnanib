using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// Shutdown is graceful: in-flight cycles finish or abort safely, and no entry is left without
/// protection (NFR-REL-5). Each test here tells an application to stop, as a signal to its
/// process does, with the background work as it runs outside the tests, and reads what it left
/// once it has stopped, through an application that starts over the same database and the same
/// Binance. A run that places the orders of a plan stands for a cycle: it hands what stops it on
/// to what places the orders, as a cycle does. That the cycles themselves are stopped and waited
/// for is in <c>CycleOverloadTests</c>, and what the scheduler does last is in
/// <c>BackgroundJobRunsLastTests</c>. See "Stopping" in docs/reliability.md.
/// </summary>
public class GracefulShutdownTests
{
    private const string Orders = "Ecnanib.DecisionEngine.AgentOrders";
    private const string TakenOff = "The entry of agent {AgentId} in {Symbol} that still rests is taken off the exchange: {EntryTakenOffReason}";

    private const string Settling = "Ecnanib.DecisionEngine.OrderSettlingJob";
    private const string SettledLast =
        "The orders of {Agents} agents are settled with the exchange as the application stops, and their entries that rest are taken off";
    private const string AgentLeft =
        "Agent {AgentId} is not settled with the exchange as the application stops: it has an order of unknown outcome, an entry that rests, or an open position without a stop-loss that rests";
    private const string Left =
        "{NotSettled} of {Agents} agents are not settled with the exchange as the application stops. The next start settles them first";

    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    /// <summary>A plan to open a long position on Spot at 59900, below the price of 60000, where the entry rests.</summary>
    private static readonly GuardCase AtAPrice = GuardCases.OpenLong with { EntryPrice = 59900m };

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task An_entry_that_is_on_its_way_as_the_application_stops_is_protected_when_the_application_has_stopped(Market market)
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        var cycle = new Cycle();
        Agent agent;
        IReadOnlyList<LoggedEvent> written;

        using (var application = new OrdersApplication(
            settlesOrders: true, database: database, binance: binance, further: services => services.AddSingleton<IBackgroundJob>(cycle.Job)))
        {
            agent = await application.GiveAgentAsync(market: market);
            var lifetime = application.Application.Lifetime;
            var stopped = lifetime.WhenStopped();

            // The application is told to stop while Binance has the entry, which it executes.
            binance.Fate = asked =>
            {
                if (!asked.Stop)
                {
                    lifetime.BeginToStop();
                }

                return OrderFate.Taken;
            };

            cycle.Places(application, agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);

            await application.WhileTimePassesAsync(stopped);
            written = application.Application.Events();
        }

        // The call ran to its answer, and the stop-loss was sent before the cycle ended. What
        // was left then, the take-profit of Futures among it, was done as the application
        // stopped: the position has its protection resting on the exchange.
        using var after = new OrdersApplication(database: database, binance: binance);

        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await after.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Null(Assert.Single(await after.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.All(binance.Orders.Where(order => order.Asked.Stop), order => Assert.Equal("NEW", order.Status));
        Assert.Single(binance.Orders, order => !order.Asked.Stop);

        // The run was stopped and waited for, and then the orders were settled once more.
        Assert.Single(written, said => said.Template == SchedulerLog.StoppedRun && said.Text("Job") == Cycle.Name);
        Assert.Single(written, said => said.Template == SchedulerLog.DidLast && said.Text("Job") == "order-settling");
        Assert.DoesNotContain(written, said => said.Area == Settling && said.Level != "Information");
        Assert.DoesNotContain(written, said => said.Area == SchedulerLog.Area && said.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task An_entry_of_unknown_outcome_is_asked_about_and_protected_before_the_application_has_stopped()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        var cycle = new Cycle();
        Agent agent;
        IReadOnlyList<LoggedEvent> written;

        using (var application = new OrdersApplication(
            settlesOrders: true, database: database, binance: binance, further: services => services.AddSingleton<IBackgroundJob>(cycle.Job)))
        {
            agent = await application.GiveAgentAsync();
            var lifetime = application.Application.Lifetime;
            var stopped = lifetime.WhenStopped();

            // Binance takes the entry, and its answer never comes, as the application is told
            // to stop: the cycle that is being stopped waits for nothing, and ends with an
            // entry of which it is not known whether Binance has it.
            binance.Fate = asked =>
            {
                if (asked.Stop)
                {
                    return OrderFate.Taken;
                }

                lifetime.BeginToStop();

                return OrderFate.TakenUnsaid;
            };

            cycle.Places(application, agent, GuardCases.OpenLong);

            await application.WhileTimePassesAsync(stopped);
            written = application.Application.Events();
        }

        // What settles last asked Binance about it, found it executed, and protected what it
        // bought: nothing waits for the next start.
        using var after = new OrdersApplication(database: database, binance: binance);
        var orders = await after.OrdersOfAsync(agent.Id);

        Assert.Contains(orders[0].ClientOrderId, binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            orders.Select(order => (order.Purpose, order.Status)));
        Assert.Null(Assert.Single(await after.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Equal([(false, "BUY"), (true, "SELL")], binance.Asked.Select(asked => (asked.Stop, asked.Side)));

        Assert.Equal(1, Assert.Single(written, said => said.Area == Settling && said.Template == SettledLast)["Agents"].GetInt32());
        Assert.DoesNotContain(written, said => said.Area == Settling && said.Level != "Information");
    }

    [Fact]
    public async Task An_entry_that_never_reached_the_exchange_is_written_off_before_the_application_has_stopped()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        var cycle = new Cycle();
        Agent agent;
        IReadOnlyList<LoggedEvent> written;

        using (var application = new OrdersApplication(
            settlesOrders: true, database: database, binance: binance, further: services => services.AddSingleton<IBackgroundJob>(cycle.Job)))
        {
            agent = await application.GiveAgentAsync();
            var lifetime = application.Application.Lifetime;
            var stopped = lifetime.WhenStopped();

            // The call ends without an answer, and Binance never took the order.
            binance.Fate = _ =>
            {
                lifetime.BeginToStop();

                return OrderFate.Lost;
            };

            cycle.Places(application, agent, GuardCases.OpenLong);

            // Binance's word that it has no such order counts ten seconds after the call: what
            // settles last waits for that moment, and asks once more.
            await application.WhileTimePassesAsync(stopped);
            written = application.Application.Events();
        }

        using var after = new OrdersApplication(database: database, binance: binance);
        var entry = Assert.Single(await after.OrdersOfAsync(agent.Id));

        // It was not placed, and is never sent again: the next cycle decides anew (NFR-REL-2).
        Assert.Equal((OrderPurpose.Entry, OrderStatus.NotPlaced), (entry.Purpose, entry.Status));
        Assert.True(binance.AskedAbout.Count(asked => asked == entry.ClientOrderId) >= 2);
        Assert.Empty(await after.PositionsOfAsync(agent.Id));
        Assert.Empty(binance.Orders);
        Assert.DoesNotContain(written, said => said.Area == Settling && said.Level != "Information");
    }

    [Fact]
    public async Task An_entry_that_rests_is_taken_off_the_exchange_as_the_application_stops()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;
        IReadOnlyList<LoggedEvent> written;

        using (var application = new OrdersApplication(settlesOrders: true, database: database, binance: binance))
        {
            agent = await application.GiveAgentAsync();
            await application.PlaceAsync(agent, AtAPrice);

            Assert.Equal("NEW", binance.The(stop: false).Status);

            await application.Application.Lifetime.StopAsync();
            written = application.Application.Events();
        }

        // Left to rest, it could be executed while the service is down, and what it bought
        // would have no stop-loss until the next start. So none rests on.
        using var after = new OrdersApplication(database: database, binance: binance);
        var entry = Assert.Single(await after.OrdersOfAsync(agent.Id));

        Assert.Equal((OrderPurpose.Entry, OrderStatus.Canceled), (entry.Purpose, entry.Status));
        Assert.Equal("CANCELED", binance.The(stop: false).Status);
        Assert.Empty(await after.PositionsOfAsync(agent.Id));
        Assert.Equal("Stopping", Assert.Single(written, said => said.Area == Orders && said.Template == TakenOff).Text("EntryTakenOffReason"));
        Assert.DoesNotContain(written, said => said.Area == Settling && said.Level != "Information");
    }

    [Fact]
    public async Task What_an_entry_that_rests_has_bought_is_protected_as_the_application_stops()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;

        using (var application = new OrdersApplication(settlesOrders: true, database: database, binance: binance))
        {
            agent = await application.GiveAgentAsync();
            await application.PlaceAsync(agent, AtAPrice);

            // Binance executes a part of the entry, and nothing has told the service yet.
            binance.Execute(binance.The(stop: false), 0.005m);

            await application.Application.Lifetime.StopAsync();
        }

        // The rest of the entry was taken off, and the part is a position with its pair.
        using var after = new OrdersApplication(database: database, binance: binance);
        var position = Assert.Single(await after.PositionsOfAsync(agent.Id));

        Assert.Equal((0.005m, 59900m, null), (position.Quantity, position.EntryPrice, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Canceled),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            (await after.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal("CANCELED", binance.Orders[0].Status);
        Assert.All(binance.Orders.Skip(1), order => Assert.Equal("NEW", order.Status));
    }

    [Fact]
    public async Task An_exchange_that_does_not_answer_keeps_the_application_from_stopping_for_no_longer_and_the_next_start_settles_first()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;
        IReadOnlyList<LoggedEvent> written;

        using (var application = new OrdersApplication(settlesOrders: true, database: database, binance: binance))
        {
            agent = await application.GiveAgentAsync();
            await application.PlaceAsync(agent, AtAPrice);

            // Binance does not answer as the application stops.
            binance.Instead = _ => StandInSystem.Json(HttpStatusCode.ServiceUnavailable, """{ "code": -1001, "msg": "Internal error." }""");

            var stopping = System.Diagnostics.Stopwatch.StartNew();
            await application.Application.Lifetime.StopAsync();

            // Nothing is waited for that time does not change: the application has stopped
            // long before the minute and a half that the file of the tests gives it.
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(30), $"The application took {stopping.Elapsed} to stop.");
            written = application.Application.Events();
        }

        // The entry rests still, and the log says which agent was left, and how many.
        Assert.Equal("NEW", binance.The(stop: false).Status);
        Assert.Equal(agent.Id.ToString(), Assert.Single(written, said => said.Template == AgentLeft).Text("AgentId"));

        var left = Assert.Single(written, said => said.Template == Left);
        Assert.Equal(("Warning", 1, 1), (left.Level, left["NotSettled"].GetInt32(), left["Agents"].GetInt32()));

        // Binance answers again, and the application starts: it settles first, which asks
        // about the entry before anything else is scheduled.
        binance.Instead = null;
        var asked = binance.AskedAbout.Count;
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Hourly));

        using var after = new OrdersApplication(
            settlesOrders: true, database: database, binance: binance, further: services => services.AddSingleton<IBackgroundJob>(other));
        _ = after.Application.Contexts;

        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waits for what settles first");

        var entry = Assert.Single(await after.OrdersOfAsync(agent.Id));
        Assert.Contains(entry.ClientOrderId, binance.AskedAbout.Skip(asked));
    }

    [Fact]
    public async Task An_instance_that_stands_by_takes_nothing_off_as_it_stops()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();

        using var running = new OrdersApplication(settlesOrders: true, database: database, binance: binance);
        var agent = await running.GiveAgentAsync();
        await running.PlaceAsync(agent, AtAPrice);

        using (var standingBy = new OrdersApplication(settlesOrders: true, database: database, binance: binance))
        {
            // Started now, and not when the test first asks it for something.
            _ = standingBy.Application.Contexts;

            await Wait.UntilAsync(
                () => standingBy.Application.Events().Any(said => said.Area == SchedulerLog.Area && said.Template == SchedulerLog.StandingBy),
                "the second instance standing by");

            await standingBy.Application.Lifetime.StopAsync();

            Assert.DoesNotContain(standingBy.Application.Events(), said => said.Template == SchedulerLog.DidLast);
        }

        // The instance that runs the background work goes on, and so does the entry.
        Assert.Equal("NEW", binance.The(stop: false).Status);
        Assert.Equal(OrderStatus.Open, Assert.Single(await running.OrdersOfAsync(agent.Id)).Status);
    }

    /// <summary>
    /// What stands for a cycle that has decided: a run of a job of the scheduler, which places
    /// the orders of a plan with what stops the run, and lets the stop out once it has ended.
    /// </summary>
    private sealed class Cycle
    {
        public const string Name = "cycle";

        private readonly TaskCompletionSource<Func<CancellationToken, Task>> _plan = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Cycle() =>
            Job = new RecordingJob(Name, BackgroundJobSchedule.Every(Hourly), async (_, stop) =>
            {
                var place = await _plan.Task.WaitAsync(stop);

                await place(stop);

                stop.ThrowIfCancellationRequested();
            });

        public RecordingJob Job { get; }

        /// <summary>Has the run place the orders of a plan for an agent, now.</summary>
        public void Places(OrdersApplication application, Agent agent, GuardCase plan)
        {
            var cycle = application.CycleOf(agent);
            var accepted = GuardCheck.Of(plan);
            var orders = application.Orders;

            _plan.SetResult(stop => orders.PlaceAsync(cycle, accepted, stop));
        }
    }
}
