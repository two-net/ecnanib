using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Agent equity is the capital the user allocated to an agent, plus the agent's net profit or
/// loss to date, and it is recorded over time: every evaluation of the loss limits of an agent
/// records the equity that it found, when that is due. See "The equity of an agent over time" in
/// docs/orders.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000 USDT, and buys 0.01 at 60000 for a fee of 0.6:
/// see <see cref="AgentResultTests"/> for its equity. The clock of the application starts at
/// 10:15:30, which is half a minute into a quarter hour.
/// </remarks>
public class EquitySnapshotTests
{
    private static readonly DateTimeOffset Start = OrdersApplication.Start;

    [Fact]
    public async Task The_first_evaluation_of_an_agent_records_its_equity_and_one_that_finds_the_same_records_nothing()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Nothing is recorded for an agent until its loss limits are evaluated.
        Assert.Empty(await application.SnapshotsOfAsync(agent.Id));

        Assert.Equal(LossLimitVerdict.Within, await EquityMarkTests.EvaluateAsync(application, agent));

        // Its capital, for it has made no profit and no loss: at the moment of the evaluation,
        // in the mode that it is in and the asset that its symbol is priced in.
        var snapshot = Assert.Single(await application.SnapshotsOfAsync(agent.Id));
        Assert.Equal(
            (agent.UserId, TradingMode.Demo, Start, "USDT", 1000m, 1000m),
            (snapshot.UserId, snapshot.Mode, snapshot.At, snapshot.Asset, snapshot.Capital, snapshot.Equity));

        // An agent that trades nothing has the equity that it had: however often it is
        // evaluated, and whatever quarter hour it is.
        application.Clock.Set(Start.AddMinutes(5));
        await EquityMarkTests.EvaluateAsync(application, agent);
        application.Clock.Set(Start.AddHours(3));
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Single(await application.SnapshotsOfAsync(agent.Id));
    }

    [Fact]
    public async Task An_equity_that_a_price_moves_is_recorded_once_in_a_quarter_hour()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EquityMarkTests.EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price moves within the quarter hour of the first row: an evaluation finds another
        // equity, and records none. A row for each evaluation would be many.
        application.Clock.Set(Start.AddMinutes(5));
        application.Binance.Price = 61000m;
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Single(await application.SnapshotsOfAsync(agent.Id));

        // The next quarter hour begins at 10:30: the first evaluation in it records the equity,
        // with what the open position would make at the price then, and the fee of its entry.
        var half = new DateTimeOffset(2026, 11, 17, 10, 30, 0, TimeSpan.Zero);
        application.Clock.Set(half);
        await EquityMarkTests.EvaluateAsync(application, agent);

        // And no later one of that quarter hour, wherever the price goes.
        application.Binance.Price = 62000m;
        application.Clock.Set(half.AddMinutes(5));
        await EquityMarkTests.EvaluateAsync(application, agent);
        application.Clock.Set(half.AddMinutes(15).AddSeconds(-1));
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Equal(
            [(Start, 1000m), (half, 1009.4m)],
            (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => (snapshot.At, snapshot.Equity)));

        // Until the next one begins.
        application.Clock.Set(half.AddMinutes(15));
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Equal(
            [(Start, 1000m), (half, 1009.4m), (half.AddMinutes(15), 1019.4m)],
            (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => (snapshot.At, snapshot.Equity)));
    }

    [Fact]
    public async Task Every_change_of_the_equity_of_an_agent_that_holds_nothing_is_recorded()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EquityMarkTests.EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user closes the position at 61000, within the quarter hour of the first row: 10
        // made, less the fee of the entry. The agent holds nothing, so its equity is settled,
        // and each change of it is a row.
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        application.Clock.Set(Start.AddMinutes(2));
        await EquityMarkTests.EvaluateAsync(application, agent);

        // The fee of the sale is stored when the orders of the agent are next settled.
        await application.SettleAsync(agent.Id);
        application.Clock.Set(Start.AddMinutes(3));
        await EquityMarkTests.EvaluateAsync(application, agent);

        // Nothing changes from here: the price moves nothing that is held.
        application.Binance.Price = 50000m;
        application.Clock.Set(Start.AddMinutes(4));
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Equal(
            [(Start, 1000m), (Start.AddMinutes(2), 1009.4m), (Start.AddMinutes(3), 1008.79m)],
            (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => (snapshot.At, snapshot.Equity)));
    }

    [Fact]
    public async Task A_row_says_the_capital_beside_the_equity_so_that_a_capital_that_is_raised_is_no_profit()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EquityMarkTests.EvaluateAsync(application, agent);

        // Its user allocates twice as much.
        application.Clock.Set(Start.AddMinutes(1));
        await EquityMarkTests.EvaluateAsync(application, AgentResultTests.With(agent, capital: 2000m));

        Assert.Equal(
            [(1000m, 1000m), (2000m, 2000m)],
            (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => (snapshot.Capital, snapshot.Equity)));
    }

    [Fact]
    public async Task What_an_agent_did_in_Demo_is_no_part_of_its_equity_over_time_in_Live()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);
        await EquityMarkTests.EvaluateAsync(application, agent);

        // The same agent once it is switched to Live: its equity there starts at its capital,
        // and has rows of its own (FR-BIN-11).
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await EquityMarkTests.EvaluateAsync(application, AgentResultTests.With(agent, mode: TradingMode.Live));

        Assert.Equal(
            [(TradingMode.Demo, 1008.79m), (TradingMode.Live, 1000m)],
            (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => (snapshot.Mode, snapshot.Equity)));
    }

    [Fact]
    public async Task An_equity_that_is_not_known_is_not_recorded()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EquityMarkTests.EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The exchange does not say the price that the open position is valued at.
        application.Clock.Set(Start.AddHours(1));
        application.Binance.Price = 50000m;
        application.FundingTimes.Prices = ExchangeCallOutcome.TimedOut;

        Assert.Equal(LossLimitVerdict.NotKnown, await EquityMarkTests.EvaluateAsync(application, agent));
        Assert.Single(await application.SnapshotsOfAsync(agent.Id));

        // Once it says it again, the equity is recorded as it is then.
        application.FundingTimes.Prices = ExchangeCallOutcome.Succeeded;
        application.Binance.Price = 60500m;
        application.Clock.Set(Start.AddHours(1).AddMinutes(1));
        await EquityMarkTests.EvaluateAsync(application, agent);

        Assert.Equal([1000m, 1004.4m], (await application.SnapshotsOfAsync(agent.Id)).Select(snapshot => snapshot.Equity));
    }

    [Fact]
    public async Task Recording_the_result_of_a_position_records_the_equity_of_its_agent_after_the_last_fee()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Nothing has evaluated the loss limits of the agent, and nothing does from here: it
        // gets no cycle in this test, and an agent that holds nothing is not reconciled.
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);
        Assert.Empty(await application.SnapshotsOfAsync(agent.Id));

        // The result of its position is recorded once the fee of the sale is stored, and the
        // loss limits of the agent are evaluated with it: so its equity after the trade is
        // recorded, whoever else would have evaluated them.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.RecordResultsAsync();

        var snapshot = Assert.Single(await application.SnapshotsOfAsync(agent.Id));
        Assert.Equal((application.Clock.GetUtcNow(), 1000m, 1008.79m), (snapshot.At, snapshot.Capital, snapshot.Equity));
    }
}
