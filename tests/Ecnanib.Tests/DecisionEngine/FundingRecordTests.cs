using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The current funding rate and the next funding time are included in the data package, and
/// funding paid or received is recorded per position (FR-BIN-F4). This is the record: what a
/// position of the Futures market received and paid is read from the exchange when it is due,
/// and stored once, as rows that name the position. The data package is in
/// <see cref="DataPackageTests"/>. See "Funding" in docs/orders.md.
/// </summary>
public class FundingRecordTests
{
    private const string Area = "Ecnanib.DecisionEngine.PositionWatch";

    // How long after a funding time, and after a position was closed, its funding is read.
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    // The funding times of Binance that follow the start of the application's clock.
    private static readonly DateTimeOffset FirstFunding = new(2026, 11, 17, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondFunding = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThirdFunding = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task What_a_position_paid_and_received_is_stored_after_each_funding_time_and_asked_for_no_more_often()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;
        var entry = (await application.OrdersOfAsync(agent.Id))[0];

        // A position of the Futures market is due from when it was opened.
        Assert.Equal(position.OpenedAt, position.FundingDueAt);
        application.FundingTimes.Next = FirstFunding;

        // Not before the exchange has had the time to write what it paid.
        application.Clock.Set(position.OpenedAt + Wait - TimeSpan.FromSeconds(1));
        await application.WatchAsync();

        Assert.Empty(providers.FundingAsked);
        Assert.Equal(0, application.FundingTimes.Asked);

        // Then for the first time: from when its entry was begun, which is before anything of
        // it was executed, to now. Nothing was paid yet. It is read next after the funding time
        // that the exchange says with the mark price.
        application.Clock.Set(position.OpenedAt + Wait);
        await application.WatchAsync();

        Assert.Equal(
            [new AskedFunding(agent.UserId, TradingMode.Demo, "BTCUSDT", entry.CreatedAt, application.Clock.GetUtcNow())],
            providers.FundingAsked);
        Assert.Empty(await application.FundingsOfAsync(position.Id));
        Assert.Equal(FirstFunding, (await PositionAsync(application, agent)).FundingDueAt);

        // Until then it is not asked for, however often the positions are watched.
        application.Clock.Set(FirstFunding + Wait - TimeSpan.FromSeconds(1));
        await application.WatchAsync();
        await application.WatchAsync();

        Assert.Single(providers.FundingAsked);
        Assert.Equal(1, application.FundingTimes.Asked);

        // The funding time has passed: the position paid, and what it paid is stored as the
        // exchange says it.
        application.Clock.Set(FirstFunding + Wait);
        application.FundingTimes.Next = SecondFunding;
        providers.Funding = _ => Said(new ExchangeFunding("7001", -0.12m, "USDT", FirstFunding));
        await application.WatchAsync();

        Assert.Equal(new AskedFunding(agent.UserId, TradingMode.Demo, "BTCUSDT", entry.CreatedAt, FirstFunding + Wait), providers.FundingAsked[1]);
        Assert.Equal(
            [("7001", -0.12m, "USDT", FirstFunding, agent.UserId)],
            (await application.FundingsOfAsync(position.Id)).Select(Row));
        Assert.Equal(SecondFunding, (await PositionAsync(application, agent)).FundingDueAt);

        // The next one it received. It is asked for from the newest that is stored, which the
        // exchange says again, and which is stored once.
        application.Clock.Set(SecondFunding + Wait);
        application.FundingTimes.Next = ThirdFunding;
        providers.Funding = _ => Said(
            new ExchangeFunding("7001", -0.12m, "USDT", FirstFunding), new ExchangeFunding("7002", 0.05m, "USDT", SecondFunding));
        await application.WatchAsync();

        Assert.Equal(new AskedFunding(agent.UserId, TradingMode.Demo, "BTCUSDT", FirstFunding, SecondFunding + Wait), providers.FundingAsked[2]);
        Assert.Equal(
            [("7001", -0.12m, "USDT", FirstFunding, agent.UserId), ("7002", 0.05m, "USDT", SecondFunding, agent.UserId)],
            (await application.FundingsOfAsync(position.Id)).Select(Row));
        Assert.Equal(ThirdFunding, (await PositionAsync(application, agent)).FundingDueAt);
        Assert.Equal(3, providers.FundingAsked.Count);

        // It is said how many rows were stored, and never what they hold.
        Assert.Equal(
            [1, 1],
            application.Application.Events()
                .Where(said => said.Area == Area && said.Template.Contains("rows of funding were stored", StringComparison.Ordinal))
                .Select(said => said["Fundings"].GetInt32()));
    }

    [Fact]
    public async Task The_funding_of_a_position_that_was_closed_is_read_once_more_up_to_the_execution_that_closed_it()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;
        var entry = (await application.OrdersOfAsync(agent.Id))[0];

        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        await application.WatchAsync();

        // The stop-loss is executed a second after the funding time, by the clock of the
        // exchange, and the application sees that three minutes later.
        var executedAt = FirstFunding.AddSeconds(1);
        application.Binance.ExecutedAt = executedAt;
        application.Binance.Execute(application.Binance.The(stop: true));
        application.Clock.Set(FirstFunding.AddMinutes(3));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        var closed = await PositionAsync(application, agent);
        Assert.Equal((FirstFunding.AddMinutes(3), ExitReason.StopLoss), (closed.ClosedAt, closed.ExitReason));
        Assert.Equal(FirstFunding, closed.FundingDueAt);

        // Not before the exchange has had the time to write what it paid: and the margin of a
        // position that is closed is not checked.
        var checkedBefore = providers.FuturesPositionsAsked.Count;
        application.Clock.Set(closed.ClosedAt!.Value + Wait - TimeSpan.FromSeconds(1));
        await application.WatchAsync();

        Assert.Single(providers.FundingAsked);
        Assert.Equal(checkedBefore, providers.FuturesPositionsAsked.Count);

        // Then once more: up to the execution that closed it, and not to when the application
        // saw that it was closed. What the account paid after that is not this position's:
        // it is not stored, whatever the exchange answers.
        application.Clock.Set(closed.ClosedAt.Value + Wait);
        providers.Funding = _ => Said(
            new ExchangeFunding("7001", -0.12m, "USDT", FirstFunding), new ExchangeFunding("7009", -0.4m, "USDT", executedAt.AddSeconds(1)));
        var markPricesAsked = application.FundingTimes.Asked;
        await application.WatchAsync();

        Assert.Equal(new AskedFunding(agent.UserId, TradingMode.Demo, "BTCUSDT", entry.CreatedAt, executedAt), providers.FundingAsked[1]);
        Assert.Equal([("7001", -0.12m, "USDT", FirstFunding, agent.UserId)], (await application.FundingsOfAsync(position.Id)).Select(Row));

        // No funding time is asked for: a closed position has no next one. All of its funding
        // is read now, which the position says, and it is not watched any more.
        Assert.Equal(markPricesAsked, application.FundingTimes.Asked);
        Assert.Null((await PositionAsync(application, agent)).FundingDueAt);

        application.Clock.Set(SecondFunding + Wait);
        await application.WatchAsync();

        Assert.Equal(2, providers.FundingAsked.Count);
        Assert.Equal(checkedBefore, providers.FuturesPositionsAsked.Count);
        Assert.False(await ((Platform.BackgroundWork.IDueAtStart)application.Application.PositionWatch)
            .IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ExchangeCallOutcome.TimedOut)]
    [InlineData(ExchangeCallOutcome.Failed)]
    [InlineData(ExchangeCallOutcome.RateLimited)]
    public async Task Funding_that_could_not_be_read_is_read_with_the_next_run(ExchangeCallOutcome outcome)
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;

        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        providers.Funding = _ => ExchangeFundings.Without(outcome);
        await application.WatchAsync();

        // Nothing is written: the position is as due as it was. It is said, with the outcome.
        Assert.Single(providers.FundingAsked);
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal("The funding of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}", said.Template);
        Assert.Equal((agent.Id.ToString(), "BTCUSDT", outcome.ToString()), (said.Text("AgentId"), said.Text("Symbol"), said.Text("ExchangeCallOutcome")));

        // The next run reads it.
        application.Clock.Set(position.OpenedAt + Wait + TimeSpan.FromMinutes(4));
        providers.Funding = _ => Said(new ExchangeFunding("7001", 0.03m, "USDT", position.OpenedAt.AddMinutes(1)));
        await application.WatchAsync();

        Assert.Equal(2, providers.FundingAsked.Count);
        Assert.Equal(providers.FundingAsked[0].From, providers.FundingAsked[1].From);
        Assert.Single(await application.FundingsOfAsync(position.Id));
        Assert.Equal(FirstFunding, (await PositionAsync(application, agent)).FundingDueAt);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("passed")]
    public async Task The_funding_of_an_open_position_is_not_read_without_a_next_funding_time_that_is_ahead(string markPrice)
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;

        // The mark price could not be read, or the funding time that it gave has passed
        // already: without the time to read it at next, the position would be read with every
        // run, and the read is the heaviest call that the service makes.
        application.FundingTimes.Next = markPrice == "passed" ? position.OpenedAt.AddMinutes(-1) : FirstFunding;
        application.FundingTimes.Outcome = markPrice == "failed" ? ExchangeCallOutcome.TimedOut : ExchangeCallOutcome.Succeeded;
        application.Clock.Set(position.OpenedAt + Wait);
        await application.WatchAsync();

        Assert.Equal(1, application.FundingTimes.Asked);
        Assert.Empty(providers.FundingAsked);
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);

        // With one, it is read.
        application.FundingTimes.Next = FirstFunding;
        application.FundingTimes.Outcome = ExchangeCallOutcome.Succeeded;
        await application.WatchAsync();

        Assert.Single(providers.FundingAsked);
        Assert.Equal(FirstFunding, (await PositionAsync(application, agent)).FundingDueAt);
    }

    [Fact]
    public async Task An_answer_of_as_many_rows_as_one_answer_holds_is_stored_and_the_rest_is_read_with_the_next_run()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;
        var first = new ExchangeFunding("7001", -0.12m, "USDT", position.OpenedAt.AddMinutes(1));
        var second = new ExchangeFunding("7002", -0.11m, "USDT", position.OpenedAt.AddMinutes(2));
        var third = new ExchangeFunding("7003", 0.02m, "USDT", position.OpenedAt.AddMinutes(3));

        // The exchange says that there may be more than it gave.
        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        providers.Funding = _ => new ExchangeFundings(ExchangeCallOutcome.Succeeded, [first, second], More: true);
        await application.WatchAsync();

        // What it gave is stored, and the position stays due. It is said.
        Assert.Equal(["7001", "7002"], (await application.FundingsOfAsync(position.Id)).Select(funding => funding.TransactionId));
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);
        Assert.Contains(
            application.Application.Events(),
            said => said.Area == Area && said.Level == "Warning" && said.Template.Contains("more than one answer of the exchange holds", StringComparison.Ordinal));

        // The next run reads on, from the newest row that is stored.
        providers.Funding = _ => Said(second, third);
        await application.WatchAsync();

        Assert.Equal(second.At, providers.FundingAsked[1].From);
        Assert.Equal(["7001", "7002", "7003"], (await application.FundingsOfAsync(position.Id)).Select(funding => funding.TransactionId));
        Assert.Equal(FirstFunding, (await PositionAsync(application, agent)).FundingDueAt);
    }

    [Fact]
    public async Task A_run_reads_the_funding_of_twenty_positions_and_the_next_runs_read_the_rest()
    {
        using var application = new OrdersApplication();
        var providers = application.Application.Providers;
        var users = new List<Guid>();

        // Twenty-one users, each with an agent that holds a position of the Futures market. A
        // funding time comes for all of them at once.
        for (var number = 1; number <= 21; number++)
        {
            var agent = await application.GiveAgentAsync($"user{number}@example.com", Market.Futures);
            await GivePositionAsync(application, agent, OrdersApplication.Start);
            users.Add(agent.UserId);
        }

        providers.FuturesPosition = _ => HeldPositions.Long(mark: 60000m);
        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(OrdersApplication.Start + Wait);
        await application.WatchAsync();

        // The margin of every one of them is checked, and the funding of twenty is read.
        Assert.Equal(users.Order(), providers.FuturesPositionsAsked.Select(asked => asked.UserId).Order());
        Assert.Equal(20, providers.FundingAsked.Select(asked => asked.UserId).Distinct().Count());

        // The next run reads the one that is left, and no other: the twenty are not due.
        await application.WatchAsync();

        Assert.Equal(users.Order(), providers.FundingAsked.Select(asked => asked.UserId).Order());

        await application.WatchAsync();

        Assert.Equal(21, providers.FundingAsked.Count);
    }

    [Fact]
    public async Task Positions_whose_funding_cannot_be_read_keep_no_other_waiting()
    {
        using var application = new OrdersApplication();
        var providers = application.Application.Providers;
        var users = new List<Guid>();

        for (var number = 1; number <= 21; number++)
        {
            var agent = await application.GiveAgentAsync($"user{number}@example.com", Market.Futures);
            await GivePositionAsync(application, agent, OrdersApplication.Start);
            users.Add(agent.UserId);
        }

        // The exchange answers no read of funding, so every position stays due.
        providers.FuturesPosition = _ => HeldPositions.Long(mark: 60000m);
        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(OrdersApplication.Start + Wait);
        providers.Funding = _ => ExchangeFundings.Without(ExchangeCallOutcome.TimedOut);
        await application.WatchAsync();

        Assert.Equal(20, providers.FundingAsked.Select(asked => asked.UserId).Distinct().Count());

        // The next run goes on where this one stopped: the one that was left comes first, and
        // then the others again.
        await application.WatchAsync();

        Assert.Equal(40, providers.FundingAsked.Count);
        Assert.Equal(users.Order(), providers.FundingAsked.Select(asked => asked.UserId).Distinct().Order());
    }

    [Fact]
    public async Task The_funding_of_a_position_that_the_account_no_longer_holds_is_not_read_until_its_close_is_seen()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;

        // Its stop-loss closed it at the exchange a moment ago, and what settles the orders has
        // not seen that yet: the exchange says that the account holds nothing in the symbol.
        providers.FuturesPosition = _ => StandInProviders.NoPosition;
        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        await application.WatchAsync();

        // Its funding is due, and is not read: up to now is not up to when it was closed, and
        // the account may hold the symbol for another agent by now. It is read once the close
        // is seen, up to the execution that closed it.
        Assert.Single(providers.FuturesPositionsAsked);
        Assert.Empty(providers.FundingAsked);
        Assert.Equal(0, application.FundingTimes.Asked);
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);
    }

    [Fact]
    public async Task A_position_of_the_Spot_market_has_no_funding_and_the_exchange_is_not_asked_about_it()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var position = await PositionAsync(application, agent);

        Assert.Null(position.FundingDueAt);

        application.Clock.Set(SecondFunding + Wait);
        await application.WatchAsync();

        Assert.Empty(application.Application.Providers.FundingAsked);
        Assert.Empty(application.Application.Providers.FuturesPositionsAsked);
        Assert.Equal(0, application.FundingTimes.Asked);
        Assert.False(await ((Platform.BackgroundWork.IDueAtStart)application.Application.PositionWatch)
            .IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_key_nothing_is_asked_and_the_funding_is_read_when_there_is_one_again()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;

        // Its user deletes the Binance key of the environment while the position is open.
        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<ExchangeCredential>().Where(key => key.UserId == agent.UserId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        await application.WatchAsync();

        Assert.Empty(providers.FuturesPositionsAsked);
        Assert.Empty(providers.FundingAsked);
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);

        // With a key again, it is read from where it was left.
        await application.Application.GiveExchangeKeyAsync(agent.UserId, application.Clock.GetUtcNow());
        await application.WatchAsync();

        Assert.Single(providers.FundingAsked);
        Assert.Equal(FirstFunding, (await PositionAsync(application, agent)).FundingDueAt);
    }

    [Fact]
    public async Task A_key_that_the_exchange_refuses_is_checked_again_and_nothing_more_is_asked_with_it()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application);
        var providers = application.Application.Providers;
        var checkedBefore = providers.Asked.Count;

        // The exchange answers the read of the margin as it answers a wrong credential.
        application.FundingTimes.Next = FirstFunding;
        application.Clock.Set(position.OpenedAt + Wait);
        providers.FuturesPosition = _ => ExchangeFuturesPosition.Without(ExchangeCallOutcome.AuthenticationFailed);
        await application.WatchAsync();

        // The check decides whether the key is wrong (FR-KEY-3), and the funding is not asked
        // for with a key that was refused a moment ago.
        Assert.Equal(checkedBefore + 1, providers.Asked.Count);
        Assert.Equal("Demo", providers.Asked[^1].About);
        Assert.Empty(providers.FundingAsked);

        // The same of a read of funding that is refused, with the next run.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        providers.FuturesPosition = _ => HeldPositions.Long(mark: 60000m);
        providers.Funding = _ => ExchangeFundings.Without(ExchangeCallOutcome.AuthenticationFailed);
        await application.WatchAsync();

        Assert.Single(providers.FundingAsked);
        Assert.Equal(checkedBefore + 2, providers.Asked.Count);
        Assert.Equal(position.OpenedAt, (await PositionAsync(application, agent)).FundingDueAt);
    }

    /// <summary>Gives a new user an agent of the Futures market that opens a long position, and gives both as they are stored.</summary>
    private static async Task<(Agent Agent, Position Position)> OpenAsync(OrdersApplication application)
    {
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The account holds the position, far from the price that it is liquidated at.
        application.Application.Providers.FuturesPosition = _ => HeldPositions.Long(mark: 60000m);

        return (agent, await PositionAsync(application, agent));
    }

    /// <summary>The one position of an agent, as it is stored now.</summary>
    private static async Task<Position> PositionAsync(OrdersApplication application, Agent agent) =>
        Assert.Single(await application.PositionsOfAsync(agent.Id));

    /// <summary>Stores an open position of the Futures market for an agent, as one that was opened at a moment.</summary>
    private static async Task GivePositionAsync(OrdersApplication application, Agent agent, DateTimeOffset openedAt)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new Position(
            Guid.CreateVersion7(), agent.Id, agent.UserId, agent.Mode, Market.Futures, agent.Symbol, PositionSide.Long, entryPrice: 60000m,
            quantity: 0.02m, stopLoss: 59000m, takeProfit: 62000m, openedAt, closedAt: null, exitReason: null, alertedAt: null,
            fundingDueAt: openedAt));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>What the exchange says that an account received and paid, all of it.</summary>
    private static ExchangeFundings Said(params ExchangeFunding[] fundings) => new(ExchangeCallOutcome.Succeeded, fundings, More: false);

    private static (string TransactionId, decimal Amount, string Asset, DateTimeOffset At, Guid UserId) Row(Funding funding) =>
        (funding.TransactionId, funding.Amount, funding.Asset, funding.At, funding.UserId);
}
