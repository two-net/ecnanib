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
/// The dashboard shows, per mode: total equity across agents, profit and loss for the day, and
/// open positions (FR-RPT-1). This is the equity of each agent of a user as it is read for
/// that: the capital that its user allocated to it, plus its net profit or loss to date, with
/// its open position valued at the price that a page follows, and never at one that the
/// exchange is asked for. See docs/dashboard.md, and "The equity of an agent" in docs/orders.md.
/// </summary>
/// <remarks>
/// An agent of these tests has a capital of 1000 USDT. The orders that a test gives it are of a
/// position that its stop-loss closed: it lost 10.1, paid a fee of 0.5, and paid 0.1 as funding,
/// which is 10.7 in all. The position that a test leaves open holds 0.01 that was entered at
/// 100000. The clock stands at nine in the morning of a day, UTC.
/// </remarks>
public class AgentEquityTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);
    private static readonly LivePrice BitcoinMark = new(Market.Futures, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.Mark);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_that_has_traded_nothing_has_the_capital_that_its_user_allocated_as_its_equity()
    {
        using var application = Application(out _, out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var first = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2), name: "First");
        var second = await application.GiveAgentAsync(
            ada.Id, Morning.AddDays(-1), name: "Second", symbol: "ETHUSDT", mode: TradingMode.Live, capital: 250.5m);

        var equities = await application.AgentEquities.OfUserAsync(ada.Id, Cancellation);

        // Each agent of the user, the oldest first, in the mode that it is in: no profit and no
        // loss to date, nothing made in the day, and nothing held.
        Assert.Equal(
            [
                new AgentEquity(first, TradingMode.Demo, "USDT", 1000m, Equity: 1000m, MadeToday: 0m, Position: null, Price: null),
                new AgentEquity(second, TradingMode.Live, "USDT", 250.5m, Equity: 250.5m, MadeToday: 0m, Position: null, Price: null),
            ],
            equities);

        // A user who has no agent has no equity.
        var grace = await application.RegisterVerifiedAsync(Grace);

        Assert.Empty(await application.AgentEquities.OfUserAsync(grace.Id, Cancellation));
    }

    [Fact]
    public async Task What_an_agent_lost_is_less_equity_and_is_of_the_day_that_it_lost_it_in()
    {
        using var application = Application(out var clock, out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // A position that its stop-loss closed two hours ago, in the same UTC day.
        await application.GiveOrdersAsync(ada.Id, agent, cycleId: null, Morning.AddHours(-2));

        var today = Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation));

        // What it made, its fee, and its funding are in its equity, and in the day.
        Assert.Equal((989.3m, -10.7m), (today.Equity, today.MadeToday));
        Assert.Null(today.Position);

        // The next UTC day begins with the equity that the agent has: nothing was lost in it yet.
        clock.Set(Morning.AddDays(1));

        var tomorrow = Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation));

        Assert.Equal((989.3m, 0m), (tomorrow.Equity, tomorrow.MadeToday));
    }

    [Fact]
    public async Task An_open_position_is_valued_at_the_price_that_a_page_follows_and_is_not_known_without_one()
    {
        using var application = Application(out _, out var prices);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var holds = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Holds");
        var idle = await application.GiveAgentAsync(ada.Id, Morning.AddHours(-12), name: "Idle", symbol: "ETHUSDT");

        await application.GiveOrdersAsync(ada.Id, holds, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(ada.Id, holds, Morning, holdingSeconds: 600, closed: false);

        var position = new OpenPosition(
            Market.Spot,
            TradingMode.Demo,
            "BTCUSDT",
            "USDT",
            PositionSide.Long,
            Quantity: 0.01m,
            EntryPrice: 100_000m,
            StopLoss: null,
            TakeProfit: null,
            OpenedAt: Morning.AddMinutes(-10));

        // Nobody follows the price, so the watch has none. The equity of the agent is not
        // known, and neither is its day: they are not what is settled of them, and not nothing.
        var unknown = await application.AgentEquities.OfUserAsync(ada.Id, Cancellation);

        Assert.Equal(
            [
                new AgentEquity(holds, TradingMode.Demo, "USDT", 1000m, Equity: null, MadeToday: null, position, Price: null),
                new AgentEquity(idle, TradingMode.Demo, "USDT", 1000m, Equity: 1000m, MadeToday: 0m, Position: null, Price: null),
            ],
            unknown);
        Assert.Null(unknown[0].Unrealized);

        // A page follows it by now, and the watch has read it: 0.01 that was entered at 100000
        // would make 2.5 at 100250, with the 10.7 that the agent lost before.
        var read = new WatchedPrice(100_250m, Morning.AddSeconds(-3));
        prices.Say(Bitcoin, read);

        var valued = (await application.AgentEquities.OfUserAsync(ada.Id, Cancellation))[0];

        Assert.Equal(new AgentEquity(holds, TradingMode.Demo, "USDT", 1000m, Equity: 991.8m, MadeToday: -8.2m, position, read), valued);
        Assert.Equal(2.5m, valued.Unrealized);

        // And a loss is a loss.
        prices.Say(Bitcoin, new WatchedPrice(99_400m, Morning.AddSeconds(-1)));

        var lost = (await application.AgentEquities.OfUserAsync(ada.Id, Cancellation))[0];

        Assert.Equal((983.3m, -16.7m, -6m), (lost.Equity, lost.MadeToday, lost.Unrealized));
    }

    [Fact]
    public async Task A_position_of_futures_is_valued_at_its_mark_price_and_at_no_other()
    {
        using var application = Application(out _, out var prices);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), market: Market.Futures);

        await application.GiveTradeAsync(ada.Id, agent, Morning, holdingSeconds: 600, closed: false);

        // The price that the symbol last traded at on Spot says nothing of it.
        prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning));

        Assert.Null(Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation)).Equity);

        // The mark price is what Binance values it at, and what its stop-loss is triggered on.
        prices.Say(BitcoinMark, new WatchedPrice(100_100m, Morning));

        var valued = Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation));

        Assert.Equal((1001m, Market.Futures, 100_100m), (valued.Equity, valued.Position!.Market, valued.Price!.Price));
    }

    [Fact]
    public async Task The_equity_of_an_agent_is_of_the_mode_that_the_agent_is_in_and_of_no_other()
    {
        using var application = Application(out _, out _);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1));

        // What it lost in Demo.
        await application.GiveOrdersAsync(ada.Id, agent, cycleId: null, Morning.AddHours(-2));

        Assert.Equal(989.3m, Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation)).Equity);

        // Its user switches it to Live: what it did in Demo is never combined with what it
        // does in Live (FR-BIN-11). Its result in Live starts at nothing, and so does its day.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Mode, TradingMode.Live), Cancellation);
        }

        var live = Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation));

        Assert.Equal((TradingMode.Live, 1000m, 0m), (live.Mode, live.Equity, live.MadeToday));
    }

    [Fact]
    public async Task The_equities_of_a_user_are_those_of_their_own_agents_and_reading_them_writes_nothing()
    {
        using var application = Application(out _, out var prices);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Of Ada");
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-1), name: "Of Grace");

        await application.GiveOrdersAsync(grace.Id, ofGrace, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(grace.Id, ofGrace, Morning, closed: false);
        prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning));

        var adasBefore = application.RowsOf(ada.Id);
        var gracesBefore = application.RowsOf(grace.Id);

        // Each is asked for by owner: nothing of the agent of Grace is in what Ada is told.
        var adas = Assert.Single(await application.AgentEquities.OfUserAsync(ada.Id, Cancellation));
        var graces = Assert.Single(await application.AgentEquities.OfUserAsync(grace.Id, Cancellation));

        Assert.Equal((ofAda, 1000m, null), (adas.AgentId, adas.Equity, adas.Position));
        Assert.Equal((ofGrace, 991.8m), (graces.AgentId, graces.Equity));
        Assert.Empty(await application.AgentEquities.OfUserAsync(Guid.NewGuid(), Cancellation));

        // It only reads: no mark and no equity is written for asking, which is for what
        // watches the loss limits.
        Assert.Equal(adasBefore, application.RowsOf(ada.Id));
        Assert.Equal(gracesBefore, application.RowsOf(grace.Id));
    }

    /// <summary>The application on a clock that the test moves, with the prices of the watch in the hands of the test.</summary>
    private static AccountsApplication Application(out TestClock clock, out StandInPrices prices)
    {
        var watch = new StandInPrices();

        clock = new TestClock(Morning);
        prices = watch;

        return new AccountsApplication(
            clock: clock,
            host: host => host.ConfigureServices(services => services.AddSingleton<IExchangePriceWatch>(watch)));
    }
}
