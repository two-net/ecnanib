using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Reliability;

/// <summary>Stops an application of a test as a signal to its process does, and says when it has stopped.</summary>
internal static class Stops
{
    /// <summary>
    /// What ends when the application has stopped: its services are stopped, the scheduler
    /// among them, which has ended its runs and done what it does last by then. A test takes
    /// it before the stop begins, while the application can still be asked.
    /// </summary>
    public static Task WhenStopped(this IHostApplicationLifetime lifetime)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());

        return stopped.Task;
    }

    /// <summary>
    /// Has the application begin to stop, beside what calls this, and returns once it is
    /// stopping: for a stop that begins in the middle of a call to the exchange, where what
    /// hears of the stop must not run inside that call.
    /// </summary>
    public static void BeginToStop(this IHostApplicationLifetime lifetime)
    {
        _ = Task.Run(lifetime.StopApplication);

        Assert.True(
            SpinWait.SpinUntil(() => lifetime.ApplicationStopping.IsCancellationRequested, TimeSpan.FromSeconds(10)),
            "The application did not begin to stop.");
    }

    /// <summary>Tells the application to stop, and waits until it has stopped.</summary>
    public static async Task StopAsync(this IHostApplicationLifetime lifetime)
    {
        var stopped = lifetime.WhenStopped();

        lifetime.StopApplication();

        await stopped.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }
}

/// <summary>What an application that was cut off in the middle of an order leaves behind.</summary>
internal static class UnderWay
{
    /// <summary>
    /// Leaves an entry at the market as an application leaves it that was stopped between
    /// sending the order and its answer: Binance has the order, and the row still says that it
    /// is being sent, since longer ago than an order is on its way.
    /// </summary>
    /// <returns>The identifier that the order carries.</returns>
    public static async Task<string> EntryAsync(OrdersApplication application, Agent agent)
    {
        var id = Guid.CreateVersion7();
        var sentAt = OrdersApplication.Start.AddMinutes(-1);

        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            context.Add(new Order(
                id, agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, cycleId: Guid.CreateVersion7(), positionId: null,
                Order.ClientOrderIdOf(id), exchangeOrderId: null, OrderPurpose.Entry, OrderType.Market, OrderSide.Buy, price: null,
                stopLoss: 59000m, takeProfit: 62000m, quantity: 0.01m, OrderStatus.Sending, executedQuantity: 0m, averagePrice: null,
                exitReason: null, sends: 1, fillsRecorded: false, sentAt, sentAt, checkedAt: null, pairId: null, cancelAfter: null));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var owner = agent.UserId.ToString("N");
        await application.Application.ExchangeOrders.PlaceAsync(
            agent.UserId, agent.Market, agent.Mode, Keys.Of(owner, "demo"), Keys.Of(owner, "seal"),
            new ExchangeOrderRequest(
                Order.ClientOrderIdOf(id), agent.Symbol, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m, Price: null, Closes: false),
            TestContext.Current.CancellationToken);

        return Order.ClientOrderIdOf(id);
    }
}
