using Ecnanib.Agents;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// For every closed position the system records net profit or loss, the result in R, fees,
/// funding, holding time, and exit reason (FR-EXP-1): the job that records the results runs
/// every minute, and as the background work starts when a closed position has no result. See
/// "The results of a position" in docs/orders.md, and docs/background-work.md.
/// </summary>
public class PositionResultsJobTests
{
    [Fact]
    public void The_results_are_recorded_every_minute()
    {
        using var application = new OrdersApplication();
        var job = application.Application.PositionResults;
        var started = OrdersApplication.Start;

        Assert.Equal("position-results", job.Name);
        Assert.Equal("every 00:01:00", job.Schedule.ToString());
        Assert.Equal(started.AddMinutes(1), job.Schedule.NextRun(started, started.AddSeconds(30)));
    }

    [Fact]
    public async Task The_results_are_recorded_as_the_background_work_starts_when_a_closed_position_has_none()
    {
        using var application = new OrdersApplication();
        var job = Assert.IsAssignableFrom<IDueAtStart>(application.Application.PositionResults);

        // Nothing to record: no position at all, and then one that is open.
        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // Its user closes it. The executions of the order that closed it are not stored yet,
        // so its result is not due: what settles the orders comes first.
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // Once they are, a position that was closed while the application did not run has its
        // result first.
        await application.SettleAsync(agent.Id);

        Assert.True(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        await application.RecordResultsAsync();

        Assert.False(await job.IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }
}
