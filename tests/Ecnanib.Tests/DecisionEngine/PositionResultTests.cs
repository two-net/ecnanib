using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// For every closed position the system records net profit or loss, the result in R, fees,
/// funding, holding time, and exit reason, linked to the agent, market, timeframe, symbol, mode,
/// and the model that held each role (FR-EXP-1). Fees and funding payments are taken from
/// exchange data and included in every profit and loss figure (FR-BIN-10). See "The results of
/// a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000 USDT. On Spot it buys 0.01 at 60000 with its
/// stop-loss at 59000, so it risks 10, which is its R. On Futures it buys or sells 0.02 with its
/// stop-loss 1000 away, and risks 20. The Binance of the tests takes a fee of a tenth of a
/// percent: 0.6 for the 0.01 at 60000, and 1.2 for the 0.02. See <see cref="AgentResultTests"/>
/// for the equity that the same rows add up to.
/// </remarks>
public class PositionResultTests
{
    private const string Area = "Ecnanib.DecisionEngine.PositionResults";

    private static readonly TimeSpan AWhile = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_position_that_its_stop_loss_closed_has_lost_its_R_and_its_fees()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Two hours later the price reaches the stop-loss at 59000, which sells the 0.01 there.
        application.Binance.ExecutedAt = application.Binance.ExecutedAt.AddHours(2);
        application.Binance.Execute(application.Binance.The(stop: true));
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var result = await application.ResultOfAsync(position.Id);

        // 10 lost, which is what it risked, with the fees of the purchase and of the sale: 0.6
        // and 0.59. It received and paid no funding: the Spot market has none.
        Assert.NotNull(result);
        Assert.Equal(ExitReason.StopLoss, position.ExitReason);
        Assert.Equal((-10m, 1.19m, 0m, -11.19m), (result.GrossResult, result.Fees, result.Funding, result.NetResult));
        Assert.Equal((10m, -1.119m), (result.Risk, result.ResultInR));

        // It was held for the two hours between the two executions, by the clock of Binance.
        Assert.Equal((7200L, TimeSpan.FromHours(2)), (result.HoldingSeconds, result.HoldingTime));

        // All of it is what Binance said, in the asset that the symbol is priced in, and the
        // row is its user's.
        Assert.Equal((true, "USDT", agent.UserId, application.Clock.GetUtcNow()), (result.Complete, result.Asset, result.UserId, result.RecordedAt));

        // It is said that a result was recorded, and never what it is.
        var said = Assert.Single(application.Application.Events(), written => written.Area == Area);
        Assert.Equal(
            "The result of the position of agent {AgentId} in {Symbol}, which was closed as {ExitReason}, was recorded: complete {Complete}",
            said.Template);
        Assert.Equal((agent.Id.ToString(), "BTCUSDT", "StopLoss"), (said.Text("AgentId"), said.Text("Symbol"), said.Text("ExitReason")));
    }

    [Fact]
    public async Task A_position_that_its_take_profit_closed_has_made_a_multiple_of_its_R()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price reaches the take-profit at 62000, which sells the 0.01 there: 20 made, less
        // the fees of 0.6 and 0.62.
        application.Binance.Execute(application.Binance.TheTakeProfit());
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var result = await application.ResultOfAsync(position.Id);

        Assert.NotNull(result);
        Assert.Equal(ExitReason.TakeProfit, position.ExitReason);
        Assert.Equal((20m, 1.22m, 18.78m), (result.GrossResult, result.Fees, result.NetResult));

        // Twice what it risked, less the fees: its plan had a reward-to-risk ratio of 2.
        Assert.Equal((10m, 1.878m, true), (result.Risk, result.ResultInR, result.Complete));
    }

    [Fact]
    public async Task A_position_that_its_team_closed_says_so_and_has_its_result()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // A later cycle of the agent decides to close it, at 61000: 10 made, less 0.6 and 0.61.
        application.Binance.Price = 61000m;
        await application.PlaceAsync(agent, GuardCases.Close);
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var result = await application.ResultOfAsync(position.Id);

        Assert.NotNull(result);
        Assert.Equal(ExitReason.TeamClose, position.ExitReason);
        Assert.Equal((10m, 1.21m, 8.79m, 0.879m), (result.GrossResult, result.Fees, result.NetResult, result.ResultInR));
    }

    [Fact]
    public async Task A_short_position_of_Futures_has_its_funding_in_its_result()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var opened = application.Clock.GetUtcNow();

        application.Binance.ExecutedAt = opened;
        await application.PlaceAsync(agent, GuardCases.OpenShort);
        await application.SettleAsync(agent.Id);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        // Three hours later its user closes it at 59000: the 0.02 that it sold at 60000 are
        // bought back for 20 less, for a fee of 1.18.
        application.Clock.Set(opened.AddHours(3));
        application.Binance.ExecutedAt = opened.AddHours(3);
        application.Binance.Price = 59000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        // Its executions are stored, and its funding is not read yet: it has no result that
        // would lack the funding.
        await application.RecordResultsAsync();
        Assert.Null(await application.ResultOfAsync(position.Id));

        // What watches the positions reads its funding once more, some minutes after the close:
        // it paid 0.12, and received 0.05.
        application.Application.Providers.Funding = _ => new ExchangeFundings(
            ExchangeCallOutcome.Succeeded,
            [new ExchangeFunding("7001", -0.12m, "USDT", opened.AddHours(1)), new ExchangeFunding("7002", 0.05m, "USDT", opened.AddHours(2))],
            More: false);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.WatchAsync();
        await application.RecordResultsAsync();

        var result = await application.ResultOfAsync(position.Id);
        position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        Assert.NotNull(result);
        Assert.Equal((ExitReason.UserClose, PositionSide.Short), (position.ExitReason, position.Side));
        Assert.Equal((20m, 2.38m, -0.07m, 17.55m), (result.GrossResult, result.Fees, result.Funding, result.NetResult));

        // It risked the 1000 from its entry to its stop-loss at 61000, for the 0.02.
        Assert.Equal((20m, 0.8775m), (result.Risk, result.ResultInR));
        Assert.Equal((10800L, true), (result.HoldingSeconds, result.Complete));
    }

    [Fact]
    public async Task A_position_has_its_result_once_the_executions_of_its_orders_are_stored_and_has_it_once()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        // An open position has no result.
        await application.RecordResultsAsync();
        Assert.Null(await application.ResultOfAsync(position.Id));

        // Its user closes it. The executions of the order that closed it, with their fees, are
        // read when the orders of the agent are next settled: until then it has no result,
        // which would lack the fee of the sale.
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.RecordResultsAsync();
        Assert.Null(await application.ResultOfAsync(position.Id));

        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var result = await application.ResultOfAsync(position.Id);
        Assert.NotNull(result);
        Assert.Equal((8.79m, true), (result.NetResult, result.Complete));

        // A run that finds it recorded writes nothing: a position has one result, which is
        // never changed.
        var recordedAt = result.RecordedAt;
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.RecordResultsAsync();

        Assert.Equal(recordedAt, (await application.ResultOfAsync(position.Id))!.RecordedAt);
        Assert.Single(application.Application.Events(), written => written.Area == Area);

        var counted = Assert.Single(application.Application.Events(), written => written.Area == Area + "Job");
        Assert.Equal("The results of {Positions} closed positions were recorded", counted.Template);
        Assert.Equal(1, counted["Positions"].GetInt32());
    }

    [Fact]
    public async Task A_position_whose_executions_cannot_be_read_is_given_its_result_when_they_are_no_longer_asked_for_and_says_so()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var closedAt = position.ClosedAt!.Value;

        // The orders of the agent are not settled: the executions of the order that closed its
        // position stay unread. They are asked for until a day after the close, and the result
        // waits an hour longer.
        application.Clock.Set(closedAt.AddHours(25).AddSeconds(-1));
        await application.RecordResultsAsync();
        Assert.Null(await application.ResultOfAsync(position.Id));

        application.Clock.Set(closedAt.AddHours(25).AddSeconds(1));
        await application.RecordResultsAsync();

        // With what there is: what the order that closed it says it was executed at, and the
        // fee of the entry alone. The result says that it is not complete.
        var result = await application.ResultOfAsync(position.Id);
        Assert.NotNull(result);
        Assert.Equal((10m, 0.6m, 9.4m, false), (result.GrossResult, result.Fees, result.NetResult, result.Complete));
    }

    [Fact]
    public async Task R_is_what_the_position_risked_when_it_was_opened_whatever_became_of_its_stop_loss()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // A later cycle moves the stop-loss up, to 59800: the position says that price from then on.
        await application.PlaceAsync(agent, GuardCases.Adjust);
        Assert.Equal(59800m, Assert.Single(await application.PositionsOfAsync(agent.Id)).StopLoss);

        // The price reaches it there: 2 lost, with the fees of 0.6 and 0.598.
        application.Binance.Execute(application.Binance.Orders.Last(order => order.Asked.Stop && !order.TakeProfit && order.Rests));
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var result = await application.ResultOfAsync(position.Id);

        Assert.NotNull(result);
        Assert.Equal((ExitReason.StopLoss, -3.198m), (position.ExitReason, result.NetResult));

        // What it risked is the 10 from its entry to the stop-loss that it was opened with, and
        // not the 2 to the one that closed it: it lost a third of its R, and not more than all of it.
        Assert.Equal((10m, -0.3198m), (result.Risk, result.ResultInR));
    }

    [Fact]
    public async Task A_closed_position_is_linked_to_its_agent_market_timeframe_symbol_mode_and_the_model_that_held_each_role()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var model = await application.Application.GiveTeamAsync(agent.UserId, agent.Id, application.Clock.GetUtcNow());

        application.Binance.ExecutedAt = application.Clock.GetUtcNow();
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        // The position says the agent as it was when it was opened, and keeps saying it.
        Assert.Equal(
            (agent.Id, agent.UserId, Market.Futures, agent.Timeframe, "BTCUSDT", "USDT", TradingMode.Demo),
            (position.AgentId, position.UserId, position.Market, position.Timeframe, position.Symbol, position.QuoteAsset, position.Mode));

        // And the model that held each of the four roles then.
        Assert.Equal(
            [(Role.MarketAnalyst, model), (Role.SentimentAnalyst, model), (Role.Strategist, model), (Role.RiskManager, model)],
            (await application.RolesOfAsync(position.Id)).Select(role => (role.Role, role.ModelId)));

        // Its user gives the Strategist another model while the position is open: the roles of
        // an agent are settings, and those of the position are who decided it.
        var another = await application.Application.GiveRoleAsync(
            agent.UserId, agent.Id, Role.Strategist, application.Clock.GetUtcNow(), model: "claude-other-model");
        Assert.NotEqual(model, another);

        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.WatchAsync();
        await application.RecordResultsAsync();

        // The result hangs on the position, which says all of it: a result in Demo is one in
        // Demo for good (FR-BIN-11).
        var result = await application.ResultOfAsync(position.Id);
        Assert.NotNull(result);
        Assert.Equal((position.Id, agent.UserId), (result.PositionId, result.UserId));
        Assert.All(await application.RolesOfAsync(position.Id), role => Assert.Equal(model, role.ModelId));
        Assert.Equal(TradingMode.Demo, Assert.Single(await application.PositionsOfAsync(agent.Id)).Mode);
    }

    [Fact]
    public async Task A_position_of_an_agent_whose_roles_have_no_model_names_none()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(await application.RolesOfAsync(Assert.Single(await application.PositionsOfAsync(agent.Id)).Id));
    }

    [Fact]
    public async Task The_fees_of_a_result_are_what_they_were_worth_whatever_asset_Binance_took_them_in()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The account pays its fees with BNB, which trades at 500: 0.0012 BNB for the purchase,
        // which is worth 0.6, and 0.00122 BNB for the sale at 61000, which is worth 0.61.
        application.Binance.FeeAsset = "BNB";
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // The job that settles the orders values the fee of the purchase, which was stored
        // before the position had its stop-loss, and reads the executions of the sale.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));
        await application.RecordResultsAsync();

        var result = await application.ResultOfAsync(Assert.Single(await application.PositionsOfAsync(agent.Id)).Id);

        // As with fees in USDT: every fee is in the result, and the result is complete.
        Assert.NotNull(result);
        Assert.Equal((10m, 1.21m, 8.79m, true), (result.GrossResult, result.Fees, result.NetResult, result.Complete));
    }

    [Fact]
    public async Task A_result_with_a_fee_that_nothing_values_waits_while_the_fee_is_asked_about_and_then_says_that_it_is_not_complete()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // Binance takes its fees in an asset that the configuration file names no symbol for.
        application.Binance.FeeAsset = "DOGE";
        application.Binance.ExecutedAt = application.Clock.GetUtcNow();
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);

        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        application.Clock.Set(position.ClosedAt!.Value.AddMinutes(5));
        await application.WatchAsync();

        // Everything is stored, but for what the fee of the order that closed it is worth, which
        // is asked about for an hour: a price that could not be read is read again by then.
        await application.RecordResultsAsync();
        Assert.Null(await application.ResultOfAsync(position.Id));

        application.Clock.Set(position.ClosedAt.Value.AddHours(1).AddSeconds(1));
        await application.RecordResultsAsync();

        // The 0.02 made 20, and no fee is counted: nothing says what they were worth.
        var result = await application.ResultOfAsync(position.Id);
        Assert.NotNull(result);
        Assert.Equal((20m, 0m, 20m, false), (result.GrossResult, result.Fees, result.NetResult, result.Complete));
    }

    [Fact]
    public async Task A_position_that_was_closed_at_Binance_by_no_order_of_the_agent_has_a_result_that_is_an_estimate()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var opened = application.Clock.GetUtcNow();

        application.Binance.ExecutedAt = opened;
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Clock.Set(opened.AddHours(1));

        // Its user closes the position at Binance by hand, at 50000. No order of the agent did
        // it, so no execution says what it was sold at, or for which fee.
        application.Binance.Price = 50000m;
        application.Binance.HoldByHand(Market.Futures, 0m);
        Assert.True(await application.ReconcileAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.ClosedAtExchange, position.ExitReason);

        application.Clock.Set(position.ClosedAt!.Value.AddMinutes(5));
        await application.WatchAsync();
        await application.RecordResultsAsync();

        // It counts as closed at the price when the reconciliation found it: 200 lost, with the
        // fee of the entry. That is ten times what it risked.
        var result = await application.ResultOfAsync(position.Id);
        Assert.NotNull(result);
        Assert.Equal((-200m, 1.2m, -201.2m), (result.GrossResult, result.Fees, result.NetResult));
        Assert.Equal((20m, -10.06m), (result.Risk, result.ResultInR));

        // It was held until that was found. And the result says that it is no measured one.
        Assert.Equal((long)(position.ClosedAt.Value - opened).TotalSeconds, result.HoldingSeconds);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task The_results_of_the_positions_of_an_agent_add_up_to_what_the_agent_made()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // One position that its user closes with a profit, and one that its stop-loss closes.
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        application.Binance.Price = 60000m;
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Execute(application.Binance.Orders.Last(order => order.Asked.Stop && !order.TakeProfit && order.Rests));
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        var positions = await application.PositionsOfAsync(agent.Id);
        var results = new List<PositionResult>();

        foreach (var position in positions)
        {
            results.Add(Assert.IsType<PositionResult>(await application.ResultOfAsync(position.Id)));
        }

        // 8.79 made, and 11.19 lost: by the sums that the equity of the agent is worked out with.
        Assert.Equal([8.79m, -11.19m], results.Select(result => result.NetResult));
        Assert.Equal(1000m + results.Sum(result => result.NetResult), (await AgentResultTests.HoldsAsync(application, agent)).Equity);
    }
}
