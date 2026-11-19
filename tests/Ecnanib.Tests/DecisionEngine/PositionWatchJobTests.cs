using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The system checks the margin state of open positions at each reconciliation (FR-BIN-F5): the
/// job that watches the positions of the Futures market runs as the background work starts,
/// when there is a position to watch, and then at the time between reconciliations of the
/// configuration file, which is four minutes in the file of the tests. See "The margin of a
/// position" in docs/orders.md, and docs/background-work.md.
/// </summary>
public class PositionWatchJobTests
{
    [Fact]
    public void The_positions_are_watched_at_the_time_between_reconciliations_of_the_configuration_file()
    {
        using var application = new OrdersApplication();
        var job = application.Application.PositionWatch;
        var started = OrdersApplication.Start;

        Assert.Equal("position-watch", job.Name);
        Assert.Equal("every 00:04:00", job.Schedule.ToString());
        Assert.Equal(started.AddMinutes(4), job.Schedule.NextRun(started, started.AddMinutes(1)));
    }

    [Fact]
    public async Task The_positions_are_watched_as_the_background_work_starts_when_there_is_one_to_watch()
    {
        using var application = new OrdersApplication();
        var job = Assert.IsAssignableFrom<IDueAtStart>(application.Application.PositionWatch);

        // Nothing to watch: no call is made for it as the application starts.
        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // A position of the Spot market has neither margin nor funding.
        var onSpot = await application.GiveAgentAsync("grace@example.com");
        await application.PlaceAsync(onSpot, GuardCases.OpenLong);

        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // An open position of the Futures market is watched from the start: a position may have
        // come near its liquidation price while the application was down.
        var onFutures = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        await application.PlaceAsync(onFutures, GuardCases.OpenLongOnFutures);

        Assert.True(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }
}
