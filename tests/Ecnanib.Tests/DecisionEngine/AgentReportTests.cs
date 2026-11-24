using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.LiveUpdates;
using Ecnanib.WebUi.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The agent screen shows an equity curve, the trade list, and performance figures: return, win
/// rate, average R, profit factor, largest drawdown, fees, and funding (FR-RPT-2). This is how
/// one agent has performed as it is read for that: its figures and its equity over time in the
/// mode that it is in, and its closed trades, each with its mode (FR-BIN-11). See
/// docs/agent-screen.md.
/// </summary>
/// <remarks>
/// An agent of these tests has a capital of 1000 USDT. The orders that a test gives it are of a
/// position of 0.01 that was entered at 100000 and that its stop-loss closed at 98990: it lost
/// 10.1, paid a fee of 0.5, and paid 0.1 as funding, which is 10.7 in all and -1.07 R, and its
/// equity was recorded as 989.3 a minute after. A trade that a test gives it has a result and
/// no orders: 10 of the asset for each R. The clock stands at nine in the morning of a day, UTC.
/// </remarks>
public class AgentReportTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_that_has_traded_nothing_has_its_capital_as_its_equity_and_no_trade_and_no_curve()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        var report = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(report);
        Assert.Equal(new AgentEquity(agent, TradingMode.Demo, "USDT", 1000m, Equity: 1000m, MadeToday: 0m, Position: null, Price: null), report.Equity);

        // Nothing made and nothing lost, of no trade, for no fee and no funding.
        var figures = report.Figures;

        Assert.Equal((0m, 0m, 0, 0m, true, 0m), (figures.NetResult, figures.ReturnPercent, figures.Closed, figures.Fees, figures.FeesValued, figures.Funding));
        Assert.Equal((0, null, null, null), (figures.Record.Trades, figures.Record.WinRate, figures.Record.AverageR, figures.Record.ProfitFactor));

        Assert.Equal(EquityCurve.None, report.Curve);

        // The first page of its trades is always there, and holds none.
        Assert.Equal((0, 1, 1), (report.Trades.Total, report.Trades.Page, report.Trades.Pages));
        Assert.Empty(report.Trades.Trades);
    }

    [Fact]
    public async Task The_figures_of_an_agent_are_its_return_what_its_trades_came_to_in_R_its_fees_and_its_funding()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // A position that its stop-loss closed, with its orders, and a trade that made 2 R after it.
        var lost = await application.GiveOrdersAsync(ada.Id, agent, cycleId: null, Morning.AddHours(-2));
        var won = await application.GiveTradeAsync(ada.Id, agent, Morning.AddHours(-1), resultInR: 2m);

        // And one whose result is not recorded yet: it is closed, and is in no measure.
        var unrecorded = await application.GiveTradeAsync(ada.Id, agent, Morning.AddMinutes(-5), recorded: false);

        var report = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(report);

        // Its equity is its capital with what its orders settled: the 10.7 that it lost. Its
        // return is that in percent of the capital that it is allocated.
        Assert.Equal((989.3m, -10.7m), (report.Equity.Equity, report.Equity.MadeToday));
        Assert.Equal((-10.7m, -1.07m), (report.Figures.NetResult, report.Figures.ReturnPercent));

        // Its fees and its funding, each on its own: a fee is a cost, and funding that it paid is below zero.
        Assert.Equal((0.5m, true, -0.1m), (report.Figures.Fees, report.Figures.FeesValued, report.Figures.Funding));

        // What its trades came to in R, by the rule of a track record: -1.07 R and 2 R.
        var record = report.Figures.Record;

        Assert.Equal(3, report.Figures.Closed);
        Assert.Equal((2, 1, 1, 0.5m, 0.465m), (record.Trades, record.Wins, record.Losses, record.WinRate, record.AverageR));
        Assert.Equal(1.8692m, Math.Round(record.ProfitFactor!.Value, 4));

        // Its equity as it was recorded, once: 989.3, which is a fall of 1.07 percent from the capital that it began with.
        var recordedAt = Morning.AddHours(-2).AddMinutes(31);

        Assert.Equal(1, report.Curve.Recorded);
        Assert.Equal(new EquityPoint(recordedAt, 989.3m, 1000m), Assert.Single(report.Curve.Points));
        Assert.Equal(new EquityDrawdown(1.07m, 10.7m, PeakAt: null, recordedAt), report.Curve.LargestDrawdown);

        // Its trades, latest close first. The one with orders says how much it held and the
        // price that it was closed at. One that has no orders says neither, and one whose
        // result is not recorded says no result.
        Assert.Equal((3, 1, 1), (report.Trades.Total, report.Trades.Page, report.Trades.Pages));
        Assert.Equal(
            [
                new AgentTrade(
                    unrecorded,
                    TradingMode.Demo,
                    Market.Spot,
                    "BTCUSDT",
                    "USDT",
                    PositionSide.Long,
                    Quantity: null,
                    EntryPrice: 100_000m,
                    ExitPrice: null,
                    OpenedAt: Morning.AddMinutes(-65),
                    ClosedAt: Morning.AddMinutes(-5),
                    ExitReason.TakeProfit,
                    Result: null,
                    Estimate: false),
                new AgentTrade(
                    won,
                    TradingMode.Demo,
                    Market.Spot,
                    "BTCUSDT",
                    "USDT",
                    PositionSide.Long,
                    Quantity: null,
                    EntryPrice: 100_000m,
                    ExitPrice: null,
                    OpenedAt: Morning.AddHours(-2),
                    ClosedAt: Morning.AddHours(-1),
                    ExitReason.TakeProfit,
                    new AgentTradeResult(NetResult: 20m, Fees: 0m, Funding: 0m, ResultInR: 2m, TimeSpan.FromHours(1)),
                    Estimate: false),
                new AgentTrade(
                    lost,
                    TradingMode.Demo,
                    Market.Spot,
                    "BTCUSDT",
                    "USDT",
                    PositionSide.Long,
                    Quantity: 0.01m,
                    EntryPrice: 100_000m,
                    ExitPrice: 98_990m,
                    OpenedAt: Morning.AddHours(-2),
                    ClosedAt: Morning.AddMinutes(-90),
                    ExitReason.StopLoss,
                    new AgentTradeResult(NetResult: -10.7m, Fees: 0.5m, Funding: -0.1m, ResultInR: -1.07m, TimeSpan.FromMinutes(30)),
                    Estimate: false),
            ],
            report.Trades.Trades);
    }

    [Fact]
    public async Task The_equity_and_the_return_of_an_agent_that_holds_a_position_are_not_known_without_its_price()
    {
        using var application = Application(out var prices);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        await application.GiveOrdersAsync(ada.Id, agent, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(ada.Id, agent, Morning, holdingSeconds: 600, closed: false);

        // Nobody follows the price: the equity is not known, and neither is the return. They
        // are not what is settled of them. What is settled is said all the same.
        var unknown = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(unknown);
        Assert.NotNull(unknown.Equity.Position);
        Assert.Equal((null, null, null), (unknown.Equity.Equity, unknown.Figures.NetResult, unknown.Figures.ReturnPercent));
        Assert.Equal((0.5m, -0.1m, 1), (unknown.Figures.Fees, unknown.Figures.Funding, unknown.Figures.Record.Trades));

        // The open position is no trade yet.
        Assert.Equal(1, unknown.Trades.Total);

        // A page follows it by now: 0.01 that was entered at 100000 would make 2.5 at 100250,
        // with the 10.7 that the agent lost before.
        prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning.AddSeconds(-3)));

        var valued = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(valued);
        Assert.Equal((991.8m, -8.2m, -0.82m), (valued.Equity.Equity, valued.Figures.NetResult, valued.Figures.ReturnPercent));
    }

    [Fact]
    public async Task The_figures_and_the_curve_are_of_the_mode_that_the_agent_is_in_and_its_trades_each_say_their_mode()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // What it did in Demo.
        var inDemo = await application.GiveOrdersAsync(ada.Id, agent, cycleId: null, Morning.AddHours(-2));

        // Its user switches it to Live: what it did in Demo is never combined with what it
        // does in Live (FR-BIN-11).
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Mode, TradingMode.Live), Cancellation);
        }

        var live = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(live);

        // In Live it has made and lost nothing, of no trade, and no equity was recorded for it.
        Assert.Equal((TradingMode.Live, 1000m), (live.Equity.Mode, live.Equity.Equity));
        Assert.Equal((0m, 0m, 0, 0m, 0m), (live.Figures.NetResult, live.Figures.ReturnPercent, live.Figures.Closed, live.Figures.Fees, live.Figures.Funding));
        Assert.Equal(0, live.Figures.Record.Trades);
        Assert.Equal(EquityCurve.None, live.Curve);

        // The trade that it closed in Demo is on the list of its trades, and says Demo.
        var listed = Assert.Single(live.Trades.Trades);

        Assert.Equal((inDemo, TradingMode.Demo), (listed.PositionId, listed.Mode));

        // A trade in Live counts, and the one in Demo still does not.
        await application.GiveTradeAsync(ada.Id, agent, Morning.AddMinutes(-10), resultInR: 1.5m);

        var traded = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(traded);
        Assert.Equal((1, 1, 1.5m), (traded.Figures.Closed, traded.Figures.Record.Trades, traded.Figures.Record.AverageR));
        Assert.Equal([TradingMode.Live, TradingMode.Demo], traded.Trades.Trades.Select(trade => trade.Mode));
    }

    [Fact]
    public async Task The_curve_of_an_agent_is_its_recorded_equity_in_its_mode_and_its_asset_in_the_order_of_time()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-3));
        var other = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-3), name: "Other", symbol: "SOLUSDT");

        // Written out of order: up to 1100, down to 990, and back to 1050.
        await application.GiveEquityAsync(
            ada.Id,
            agent,
            (Morning.AddDays(-1), 990m),
            (Morning.AddDays(-3), 1000m),
            (Morning.AddHours(-1), 1050m),
            (Morning.AddDays(-2), 1100m));

        // What says nothing of it: the equity of another agent, its own in the other mode, and
        // its own in an asset that its symbol was priced in before.
        await application.GiveEquityAsync(ada.Id, other, (Morning.AddDays(-2), 5m));

        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            context.Add(new EquitySnapshot(agent, ada.Id, TradingMode.Live, Morning.AddDays(-2).AddHours(1), "USDT", 1000m, 7m));
            context.Add(new EquitySnapshot(agent, ada.Id, TradingMode.Demo, Morning.AddDays(-2).AddHours(2), "USDC", 1000m, 9m));
            await context.SaveChangesAsync(Cancellation);
        }

        var curve = (await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation))!.Curve;

        Assert.Equal(4, curve.Recorded);
        Assert.Equal(
            [
                new EquityPoint(Morning.AddDays(-3), 1000m, 1000m),
                new EquityPoint(Morning.AddDays(-2), 1100m, 1000m),
                new EquityPoint(Morning.AddDays(-1), 990m, 1000m),
                new EquityPoint(Morning.AddHours(-1), 1050m, 1000m),
            ],
            curve.Points);
        Assert.Equal((990m, 1100m), (curve.Lowest!.Equity, curve.Highest!.Equity));

        // The fall from 1100 to 990 is 10 percent of the peak.
        Assert.Equal(new EquityDrawdown(10m, 110m, Morning.AddDays(-2), Morning.AddDays(-1)), curve.LargestDrawdown);
    }

    [Fact]
    public async Task A_trade_says_the_price_that_its_orders_closed_it_at_and_none_where_no_order_of_the_agent_did()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // Closed in two parts: 0.01 at 101000, and 0.02 at 102500. What it holds grew less with
        // the first, so the position itself says 0.02.
        var inParts = await GivePositionAsync(
            application,
            ada.Id,
            agent,
            closedAt: Morning.AddHours(-3),
            held: 0.02m,
            ExitReason.TeamClose,
            madeAtExchange: null,
            (OrderPurpose.Entry, 0.03m, 100_000m),
            (OrderPurpose.Close, 0.01m, 101_000m),
            (OrderPurpose.Close, 0.02m, 102_500m));

        // Closed at Binance, by no order of the agent: what it made there is an amount on the position.
        var atExchange = await GivePositionAsync(
            application,
            ada.Id,
            agent,
            closedAt: Morning.AddHours(-2),
            held: 0.03m,
            ExitReason.ClosedAtExchange,
            madeAtExchange: 12m,
            (OrderPurpose.Entry, 0.03m, 100_000m));

        // One of the two orders that closed it says no price that it was executed at.
        var unpriced = await GivePositionAsync(
            application,
            ada.Id,
            agent,
            closedAt: Morning.AddHours(-1),
            held: 0.01m,
            ExitReason.UserClose,
            madeAtExchange: null,
            (OrderPurpose.Entry, 0.03m, 100_000m),
            (OrderPurpose.StopLoss, 0.02m, 99_000m),
            (OrderPurpose.Close, 0.01m, null));

        var trades = (await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation))!.Trades.Trades;

        Assert.Equal([unpriced, atExchange, inParts], trades.Select(trade => trade.PositionId));

        // Each held what was executed of its entry, whatever was left of it when it was closed.
        Assert.All(trades, trade => Assert.Equal(0.03m, trade.Quantity));

        // The average of the two parts, by what each executed: (0.01 * 101000 + 0.02 * 102500) / 0.03.
        Assert.Equal((102_000m, ExitReason.TeamClose, false), (trades[2].ExitPrice, trades[2].ExitReason, trades[2].Estimate));

        // No order closed it: no price is said, and its result is an estimate.
        Assert.Equal((null, ExitReason.ClosedAtExchange, true), (trades[1].ExitPrice, trades[1].ExitReason, trades[1].Estimate));

        // An average of the orders that say a price would look right, and is not: none is said.
        Assert.Null(trades[0].ExitPrice);
    }

    [Fact]
    public async Task The_trades_of_an_agent_are_read_a_page_at_a_time_latest_close_first()
    {
        using var application = Application(out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // 27 trades, a minute apart, the last one a minute ago.
        var ids = await application.GiveTradesAsync(ada.Id, agent, count: 27, latest: Morning.AddMinutes(-1), roles: []);

        var reports = application.AgentReports;
        var first = (await reports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation))!.Trades;
        var second = (await reports.OfAgentAsync(ada.Id, agent, tradesPage: 2, Cancellation))!.Trades;

        Assert.Equal((27, 1, 2, AgentTrades.PageSize), (first.Total, first.Page, first.Pages, first.Trades.Count));
        Assert.Equal((27, 2, 2, 2), (second.Total, second.Page, second.Pages, second.Trades.Count));

        // The latest close first, across the pages, each trade once.
        Assert.Equal(ids.Reverse(), first.Trades.Concat(second.Trades).Select(trade => trade.PositionId));

        // A page after the last holds none, and says how many pages there are. The figures are of every trade whatever the page.
        var third = await reports.OfAgentAsync(ada.Id, agent, tradesPage: 3, Cancellation);

        Assert.NotNull(third);
        Assert.Equal((27, 3, 2), (third.Trades.Total, third.Trades.Page, third.Trades.Pages));
        Assert.Empty(third.Trades.Trades);
        Assert.Equal(27, third.Figures.Record.Trades);

        // There is no page before the first.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reports.OfAgentAsync(ada.Id, agent, tradesPage: 0, Cancellation));
    }

    [Fact]
    public async Task The_report_of_an_agent_is_read_by_its_user_alone_and_reading_it_writes_nothing()
    {
        using var application = Application(out var prices);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Of Ada");
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-1), name: "Of Grace");

        await application.GiveOrdersAsync(grace.Id, ofGrace, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(grace.Id, ofGrace, Morning, closed: false);
        prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning));

        var adasBefore = application.RowsOf(ada.Id);
        var gracesBefore = application.RowsOf(grace.Id);

        // An agent of another user is not found, like one that does not exist (NFR-SEC-3).
        Assert.Null(await application.AgentReports.OfAgentAsync(ada.Id, ofGrace, tradesPage: 1, Cancellation));
        Assert.Null(await application.AgentReports.OfAgentAsync(ada.Id, Guid.NewGuid(), tradesPage: 1, Cancellation));
        Assert.Null(await application.AgentReports.OfAgentAsync(Guid.NewGuid(), ofGrace, tradesPage: 1, Cancellation));

        // Each reads their own, and nothing of the other's is in it.
        var adas = await application.AgentReports.OfAgentAsync(ada.Id, ofAda, tradesPage: 1, Cancellation);
        var graces = await application.AgentReports.OfAgentAsync(grace.Id, ofGrace, tradesPage: 1, Cancellation);

        Assert.NotNull(adas);
        Assert.NotNull(graces);
        Assert.Equal((ofAda, 1000m, 0, 0), (adas.Equity.AgentId, adas.Equity.Equity, adas.Trades.Total, adas.Curve.Recorded));
        Assert.Equal((ofGrace, 991.8m, 1, 1), (graces.Equity.AgentId, graces.Equity.Equity, graces.Trades.Total, graces.Curve.Recorded));

        // It only reads: no equity is recorded for asking, which is for what watches the loss limits.
        Assert.Equal(adasBefore, application.RowsOf(ada.Id));
        Assert.Equal(gracesBefore, application.RowsOf(grace.Id));
    }

    /// <summary>The application on a clock that stands at the morning, with the prices of the watch in the hands of the test.</summary>
    private static AccountsApplication Application(out StandInPrices prices)
    {
        var watch = new StandInPrices();

        prices = watch;

        return new AccountsApplication(
            clock: new TestClock(Morning),
            host: host => host.ConfigureServices(services => services.AddSingleton<IExchangePriceWatch>(watch)));
    }

    /// <summary>
    /// Gives an agent a closed position with the orders that the test says, each executed in
    /// full at its price, stored as the application stores them. It has no result: a test of
    /// what the orders of a trade say needs none.
    /// </summary>
    /// <returns>The id of the position.</returns>
    private static async Task<Guid> GivePositionAsync(
        AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        decimal held,
        ExitReason exitReason,
        decimal? madeAtExchange,
        params (OrderPurpose Purpose, decimal Executed, decimal? AveragePrice)[] orders)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        var position = new Position(
            Guid.CreateVersion7(),
            agentId,
            userId,
            TradingMode.Demo,
            Market.Spot,
            "BTCUSDT",
            "USDT",
            Timeframe.OneHour,
            PositionSide.Long,
            entryPrice: 100_000m,
            quantity: held,
            stopLoss: 99_000m,
            takeProfit: null,
            openedAt: closedAt.AddHours(-1),
            closedAt,
            exitReason,
            alertedAt: null,
            fundingDueAt: null,
            madeAtExchange);

        // The position first: its orders name it.
        context.Add(position);
        await context.SaveChangesAsync(Cancellation);

        foreach (var (purpose, executed, averagePrice) in orders)
        {
            var id = Guid.CreateVersion7();

            context.Add(new Order(
                id,
                agentId,
                userId,
                TradingMode.Demo,
                Market.Spot,
                "BTCUSDT",
                cycleId: null,
                position.Id,
                Order.ClientOrderIdOf(id),
                exchangeOrderId: null,
                purpose,
                purpose == OrderPurpose.StopLoss ? OrderType.Stop : OrderType.Market,
                purpose == OrderPurpose.Entry ? OrderSide.Buy : OrderSide.Sell,
                price: purpose == OrderPurpose.StopLoss ? averagePrice : null,
                stopLoss: purpose == OrderPurpose.Entry ? 99_000m : null,
                takeProfit: null,
                quantity: executed,
                OrderStatus.Filled,
                executedQuantity: executed,
                averagePrice,
                exitReason: null,
                sends: 1,
                fillsRecorded: true,
                createdAt: closedAt.AddHours(-1),
                updatedAt: closedAt,
                checkedAt: closedAt,
                pairId: null,
                cancelAfter: null));
        }

        await context.SaveChangesAsync(Cancellation);

        return position.Id;
    }
}
