using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-UI-03, FR-EXP-08 and NFR-SEC-04: an agent's chart marks each of its cycles, with how it ended and what the
/// Strategist proposed, for the chart's own environment, pair, timeframe and window, and only for its own user.
/// </summary>
public sealed class CycleMarkerTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task A_chart_lists_its_cycles_with_their_outcome_and_proposal()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        var first = app.LastClose;
        await app.RunAsync(user, agent);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);

        var markers = await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(first.AddHours(-1), first.AddHours(2)), Cancellation);

        Assert.Equal(
            [
                (first, (CycleOutcome?)CycleOutcome.AwaitingApproval, (TradeAction?)TradeAction.Buy, TradingEnvironment.Demo),
                (first.AddHours(1), CycleOutcome.Hold, TradeAction.Hold, TradingEnvironment.Demo),
            ],
            markers.Select(marker => (marker.CandleClose, marker.Outcome, marker.Action, marker.Environment)));
    }

    [Fact]
    public async Task Only_the_charts_own_window_environment_pair_and_user_count()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        var close = app.LastClose;
        await app.RunAsync(user, agent);

        Assert.Single(await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(close, close), Cancellation));
        Assert.Empty(await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(close.AddMinutes(1), close.AddHours(1)), Cancellation));
        Assert.Empty(await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(close, close) with { Environment = TradingEnvironment.Live }, Cancellation));
        Assert.Empty(await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(close, close) with { Symbol = "ETHUSDT" }, Cancellation));
        Assert.Empty(await app.Traces.ListCyclesAsync(user.Id, agent.Id, Range(close, close) with { Timeframe = Timeframe.FourHours }, Cancellation));
        Assert.Empty(await app.Traces.ListCyclesAsync(Guid.NewGuid(), agent.Id, Range(close, close), Cancellation));
    }

    private static CycleRange Range(DateTimeOffset from, DateTimeOffset to) =>
        new(TradingEnvironment.Demo, "BTCUSDT", Timeframe.OneHour, from, to);
}
