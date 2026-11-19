using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// When an agent's loss for the UTC day reaches the daily loss limit, the system closes its
/// position and halts it until the next UTC day. When its drawdown from peak equity reaches the
/// drawdown limit, the system closes its position and halts it until the user resumes it. Both
/// limits include unrealized profit and loss and are evaluated at every cycle and every
/// reconciliation (FR-RSK-8). The agent enters the Halted state, with the cause and time recorded
/// and shown to the user. See "Halting an agent" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// The agent of these tests has a capital of 1000 USDT, a daily loss limit of 5 percent, and a
/// drawdown limit of 20 percent. It buys 0.01 at 60000 on Spot for a fee of 0.6, and 0.02 on
/// Futures for a fee of 1.2: see <see cref="AgentResultTests"/> for its equity, and
/// <see cref="EquityMarkTests"/> for what its loss limits are counted from.
/// </para>
/// <para>
/// The loss limits of an agent are evaluated for the first time by its first cycle, before
/// anything of it is placed: <see cref="WatchedAsync"/> gives an agent that was.
/// </para>
/// </remarks>
public class LossLimitHaltTests
{
    private const string Area = "Ecnanib.DecisionEngine.LossLimitWatch";

    private const string Reached =
        "Agent {AgentId} reached its {LossLimit} limit, with a loss of {LossPercent} percent: it is halted, and its position is closed at market";

    private const string NotEvaluated =
        "The loss limits of agent {AgentId} are not evaluated: its equity is not known. They are evaluated again the next time";

    private const string NotClosed =
        "The position of agent {AgentId}, which is halted, could not be closed at market. It is open as it was, and is closed again with a later evaluation";

    [Fact]
    public async Task An_unrealized_loss_that_reaches_the_daily_loss_limit_closes_the_position_and_halts_the_agent_at_a_reconciliation()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // The 0.01 that it bought at 60000 is worth 50 less at 55000: with the fee of 0.6 that
        // is 5.06 percent of the 1000 that it had as the day began. No order has closed
        // anything: the loss is unrealized. Its limit is 5 percent.
        application.Binance.Price = 55000m;
        var before = application.Clock.GetUtcNow();

        await application.ReconcileAllAsync();

        // The agent is halted, with the cause and the time (FR-AGT-10).
        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DailyLossLimitReached), (halted.State, halted.StateCause));
        Assert.InRange(halted.StateChangedAt, before, application.Clock.GetUtcNow());
        Assert.Equal(
            ["Running: UserStarted", "Halted: DailyLossLimitReached"],
            StoredAgentStateChange.Told(application.Application.Database, agent.Id));

        // Its position is closed at market, by the service: its protection was taken off the
        // exchange, and one order sold what it held.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.LossLimit, position.ExitReason);
        Assert.NotNull(position.ClosedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.Close, OrderStatus.Filled),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Single(application.Binance.Asked, asked => !asked.Stop && asked.Side == "SELL");

        // Its user is told, with the figures in plain words (FR-NTF-1).
        var told = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.AgentHalted, (Guid?)agent.Id), (told.Type, told.AgentId));
        Assert.Equal(
            "The agent reached its daily loss limit in BTCUSDT (Demo): its equity is 949.4 USDT, 5.06% below the 1000 USDT that it had " +
            "as the UTC day began, and its limit is 5%. It is halted, and its position is closed at market. The service resumes it when " +
            "the next UTC day begins.",
            told.Content);

        // The log says which limit and how much was lost, in percent, and never an amount.
        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal(Reached, said.Template);
        Assert.Equal((agent.Id.ToString(), "DailyLoss", 5.06m), (said.Text("AgentId"), said.Text("LossLimit"), said["LossPercent"].GetDecimal()));

        // A reconciliation of an agent that holds nothing asks nobody, and changes nothing.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.ReconcileAllAsync();

        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);
    }

    [Theory]
    [InlineData(55061, false)]
    [InlineData(55060, true)]
    public async Task A_loss_limit_is_reached_at_the_limit_and_not_a_little_short_of_it(decimal price, bool halted)
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // At 55060 the position has lost 49.4, which is 50 with the fee: exactly 5 percent of
        // 1000. At 55061 it is 49.99.
        application.Binance.Price = price;
        await application.ReconcileAllAsync();

        Assert.Equal(halted ? AgentState.Halted : AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.Equal(halted, Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt is not null);
        Assert.Equal(halted ? 1 : 0, (await application.NotificationsOfAsync(agent.UserId)).Count);
    }

    [Fact]
    public async Task A_change_that_the_account_event_feed_says_evaluates_the_loss_limits_at_once()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        application.Binance.Price = 55000m;

        // Something changed in the account of its user: the agent is reconciled then, and not
        // at the next time between reconciliations, and its loss limits are evaluated with it.
        feed.Say(agent.UserId, Market.Spot, "BTCUSDT");
        await application.FollowFeedAsync();

        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DailyLossLimitReached), (halted.State, halted.StateCause));
        Assert.Equal(ExitReason.LossLimit, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task A_fall_from_the_peak_that_reaches_the_drawdown_limit_closes_the_position_and_halts_the_agent_until_its_user_resumes_it()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application, Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // The 0.02 that it bought at 60000 is worth 220 less at 49000: with the fee of 1.2 its
        // equity is 778.8, which is 22.12 percent below its peak of 1000. Its drawdown limit is
        // 20 percent. Its daily loss limit is reached as well: the drawdown limit is the one
        // that is said, for it halts the agent for longer.
        application.Binance.Price = 49000m;
        await application.ReconcileAllAsync();

        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DrawdownLimitReached), (halted.State, halted.StateCause));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((ExitReason.LossLimit, true), (position.ExitReason, position.ClosedAt is not null));

        // An order that only reduces the position closed it, and nothing of its protection is left.
        Assert.Single(application.Binance.Asked, asked => !asked.Stop && asked.Side == "SELL" && asked.ReduceOnly);
        Assert.DoesNotContain(application.Binance.Orders, order => order.Rests);

        var told = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.AgentHalted, told.Type);
        Assert.Equal(
            "The agent reached its drawdown limit in BTCUSDT (Demo): its equity is 778.8 USDT, 22.12% below its peak of 1000 USDT, and " +
            "its limit is 20%. It is halted, and its position is closed at market. It stays halted until you resume it.",
            told.Content);

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal(("Drawdown", 22.12m), (said.Text("LossLimit"), said["LossPercent"].GetDecimal()));

        // What settles the orders reads the executions of the order that closed the position,
        // with its fee, within a minute.
        await application.SettleAsync(agent.Id);

        // The service does not resume it: not at the next reconciliation, and not when the
        // next UTC day begins.
        application.Clock.Set(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, await application.Application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);

        // Its user does. The agent runs again, and its drawdown is counted anew: from the
        // equity that reached the limit, and no longer from the peak that it fell from. It
        // would be halted again at its first cycle otherwise.
        await ResumeAsync(application, agent);

        var resumed = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Running, AgentStateCause.UserResumed), (resumed.State, resumed.StateCause));
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, resumed));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);

        // 220 lost, and the fees of the purchase and of the sale, which are 1.2 and 0.98.
        var holds = await AgentResultTests.HoldsAsync(application, resumed);
        Assert.Equal((777.82m, 777.82m, 778.8m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));
    }

    [Fact]
    public async Task An_agent_that_its_user_resumes_on_the_day_of_the_loss_is_halted_again_by_its_daily_loss_limit()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application, Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Binance.Price = 49000m;
        await application.ReconcileAllAsync();
        Assert.Equal(AgentStateCause.DrawdownLimitReached, (await application.AgentAsync(agent.Id)).StateCause);

        // Its user resumes it at once. Its drawdown is counted anew, and its loss of the day is
        // what it was: 22 percent of what it had as the day began.
        await ResumeAsync(application, agent);
        var resumed = await application.AgentAsync(agent.Id);
        Assert.Equal(AgentState.Running, resumed.State);

        // Its next cycle, or its next reconciliation, finds its daily loss limit reached: it
        // is halted until the next UTC day, and no user action bypasses that (FR-RSK-3).
        Assert.Equal(LossLimitVerdict.Halted, await EvaluateAsync(application, resumed));

        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DailyLossLimitReached), (halted.State, halted.StateCause));
        Assert.Equal(
            [NotificationType.AgentHalted, NotificationType.AgentHalted],
            (await application.NotificationsOfAsync(agent.UserId)).Select(told => told.Type));
    }

    [Fact]
    public async Task A_realized_loss_that_reaches_the_daily_loss_limit_halts_the_agent_at_its_next_cycle_before_its_team_is_called()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its position is closed at 54000, by its user: 60 lost, and the fees.
        application.Binance.Price = 54000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // It holds nothing, so nobody reconciles it. Its next cycle settles its orders, which
        // reads the fee of the sale, and evaluates its loss limits before anything else.
        await application.Application.GiveTeamAsync(agent.UserId, agent.Id, application.Clock.GetUtcNow());
        var running = await application.Application.RunningAgents.FindAsync(agent.Id, TestContext.Current.CancellationToken);
        var cycle = new CycleStart(
            Guid.CreateVersion7(),
            running!.Agent,
            running.Roles,
            application.Clock.GetUtcNow().AddSeconds(-30),
            new ExchangeCandles(ExchangeCallOutcome.Failed, [], null));

        await application.WhileTimePassesAsync(application.Application.Cycle.RunAsync(cycle, TestContext.Current.CancellationToken));

        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DailyLossLimitReached), (halted.State, halted.StateCause));

        // The cycle is recorded, as every cycle is, and says how it ended. No model was called:
        // an agent that is halted has no decision to make.
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var recorded = await context.Set<DecisionCycle>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((cycle.CycleId, CycleOutcome.Halted), (recorded.Id, recorded.Outcome));
        Assert.Empty(await context.Set<RoleOutput>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.Set<RiskCheck>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

        // 60 lost, with the fees of 0.6 and 0.54: 6.11 percent of the 1000 of the day's start.
        var told = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Contains("its equity is 938.86 USDT, 6.11% below the 1000 USDT that it had as the UTC day began", told.Content);

        // Nothing was open to close: the one sale is the one of its user.
        Assert.Single(application.Binance.Asked, asked => !asked.Stop && asked.Side == "SELL");
        Assert.Equal(ExitReason.UserClose, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task The_loss_of_a_day_is_counted_from_the_equity_as_that_UTC_day_began()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // On the first day the position loses 30: 3.06 percent with the fee.
        application.Binance.Price = 57000m;
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        // The next UTC day begins at 969.4. By 54500 the agent has lost 5.56 percent of its
        // capital in all, and 2.58 percent in the day: no limit is reached.
        application.Clock.Set(new DateTimeOffset(2026, 11, 18, 0, 0, 5, TimeSpan.Zero));
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        application.Binance.Price = 54500m;
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);

        // At 52000 it has lost 50 of the 969.4 in the day, which is 5.16 percent.
        application.Binance.Price = 52000m;
        Assert.Equal(LossLimitVerdict.Halted, await EvaluateAsync(application, agent));

        var told = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(
            "The agent reached its daily loss limit in BTCUSDT (Demo): its equity is 919.4 USDT, 5.16% below the 969.4 USDT that it had " +
            "as the UTC day began, and its limit is 5%. It is halted, and its position is closed at market. The service resumes it when " +
            "the next UTC day begins.",
            told.Content);
        Assert.Equal(ExitReason.LossLimit, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Theory]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Stopped)]
    public async Task An_agent_that_does_not_run_is_not_halted_and_keeps_its_position_with_its_protective_orders(AgentState state)
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user pauses it, or stops it and keeps its position, or it waits for its user.
        await MoveAsync(application, agent.Id, state);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Binance.Price = 55000m;

        await application.ReconcileAllAsync();

        // Only an agent that runs is halted (SRS 3.3): this one is as its user left it, and its
        // position is open, with the stop-loss and the take-profit that rest for it.
        Assert.Equal(state, (await application.AgentAsync(agent.Id)).State);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Equal(2, application.Binance.Orders.Count(order => order.Rests));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.DoesNotContain(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");

        // What it lost is counted all the same, from the day's start.
        var holds = await AgentResultTests.HoldsAsync(application, await application.AgentAsync(agent.Id));
        Assert.Equal((949.4m, 1000m), (holds.Equity, holds.EquityAtDayStart));

        // So once it runs again, its limit is found reached before its team is called.
        await MoveAsync(application, agent.Id, AgentState.Running);
        Assert.Equal(LossLimitVerdict.Halted, await EvaluateAsync(application, await application.AgentAsync(agent.Id)));
        Assert.Equal(ExitReason.LossLimit, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task An_agent_whose_equity_is_not_known_is_not_halted_until_the_exchange_says_the_price()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Binance.Price = 55000m;

        // The exchange does not say the price that the position is valued at.
        application.FundingTimes.Prices = ExchangeCallOutcome.TimedOut;
        await application.ReconcileAllAsync();

        // Nothing is halted on a guess, and nothing is written: the position stays open, with
        // its stop-loss on the exchange.
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotEvaluated, Assert.Single(application.Application.Events(), written => written.Area == Area).Template);
        Assert.Equal(LossLimitVerdict.NotKnown, await EvaluateAsync(application, agent));

        // The next reconciliation asks again.
        application.FundingTimes.Prices = ExchangeCallOutcome.Succeeded;
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.ReconcileAllAsync();

        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);
    }

    [Fact]
    public async Task A_position_that_cannot_be_closed_is_that_of_a_halted_agent_and_is_closed_again_with_every_reconciliation()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);

        // A first position, which its user closes at 55500: 45 lost, and the fees.
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 55500m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // A second one, at 60000 again, which is 4 down at 59600: above its stop-loss, and
        // with what was lost before 5.08 percent of the 1000 of the day's start.
        application.Binance.Price = 60000m;
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Binance.Price = 59600m;

        // Binance refuses the order that closes the position, and takes a stop-loss.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Taken : OrderFate.Refused;

        await application.ReconcileAllAsync();

        // The agent is halted first: it opens nothing more. Its position is open as it was,
        // with a stop-loss and a take-profit that rest again.
        var halted = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.Halted, AgentStateCause.DailyLossLimitReached), (halted.State, halted.StateCause));
        Assert.Null((await application.PositionsOfAsync(agent.Id))[^1].ClosedAt);
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).TakeLast(2).Select(order => (order.Purpose, order.Status)));

        // Its user is told both: that it was halted, and that its position is open still.
        var told = await application.NotificationsOfAsync(agent.UserId);
        Assert.Equal([NotificationType.AgentHalted, NotificationType.ProtectionFailure], told.Select(notification => notification.Type));
        Assert.Contains("5.08% below the 1000 USDT", told[0].Content);
        Assert.Equal(
            "The position of the halted agent in BTCUSDT (Demo) could not be closed at market. It is open as it was, and the service " +
            "tries again within minutes. Close it with \"Close position now\", or at Binance. This is said once in an hour for an agent.",
            told[1].Content);
        Assert.Equal(NotClosed, application.Application.Events().Last(written => written.Area == Area && written.Level == "Warning").Template);

        // The next reconciliation closes again, and is refused again. It is said once in an
        // hour, and the agent is halted once.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.ReconcileAllAsync();

        Assert.Null((await application.PositionsOfAsync(agent.Id))[^1].ClosedAt);
        Assert.Equal(2, (await application.NotificationsOfAsync(agent.UserId)).Count);
        Assert.Single(StoredAgentStateChange.Told(application.Application.Database, agent.Id), change => change.StartsWith("Halted", StringComparison.Ordinal));

        // Binance takes the order now: the position is closed, for the loss limit.
        application.Binance.Fate = null;
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.ReconcileAllAsync();

        var closed = (await application.PositionsOfAsync(agent.Id))[^1];
        Assert.Equal((ExitReason.LossLimit, true), (closed.ExitReason, closed.ClosedAt is not null));
        Assert.Equal(2, (await application.NotificationsOfAsync(agent.UserId)).Count);
    }

    [Fact]
    public async Task A_close_that_failed_is_not_asked_for_again_at_once_whatever_evaluates_the_agent()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await WatchedAsync(application);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 55500m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        application.Binance.Price = 60000m;
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Binance.Price = 59600m;
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Taken : OrderFate.Refused;

        // The agent is halted, and Binance refuses the order that closes its position.
        await application.ReconcileAllAsync();

        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);
        var closes = Closes();
        Assert.True(closes > 0);

        // A close that fails takes the protection of the position off and places it again,
        // which the account event feed says as a change, and a change is a reconciliation,
        // which evaluates. It asks for no close of its own: the position keeps its stop-loss.
        feed.Say(agent.UserId, Market.Spot, "BTCUSDT");
        await application.FollowFeedAsync();

        Assert.Equal(closes, Closes());
        Assert.Equal(LossLimitVerdict.Halted, await EvaluateAsync(application, await application.AgentAsync(agent.Id)));
        Assert.Equal(closes, Closes());

        // A few minutes later it is asked for again, which the next reconciliation does.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        Assert.Equal(LossLimitVerdict.Halted, await EvaluateAsync(application, await application.AgentAsync(agent.Id)));

        Assert.True(Closes() > closes);

        // The orders of the agent at the market that sell, which is what a close of a long position is.
        int Closes() => application.Binance.Asked.Count(asked => !asked.Stop && asked.Side == "SELL");
    }

    [Fact]
    public async Task An_entry_of_a_halted_agent_that_still_rests_is_taken_off_the_exchange()
    {
        using var application = new OrdersApplication();
        var agent = await WatchedAsync(application);

        // It lost 60 with a position that its user closed, and has an entry at a price that
        // rests: no position, and an order that may open one.
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 54000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        application.Binance.Price = 60000m;
        await application.PlaceAsync(agent, GuardCases.OpenLong with { EntryPrice = 59900m });
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));

        Assert.Equal(OrderStatus.Open, (await application.OrdersOfAsync(agent.Id))[^1].Status);

        // An agent that holds an order is reconciled, and its loss limits are evaluated.
        await application.ReconcileAllAsync();

        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);

        // A halted agent opens nothing: its entry is taken off, and nothing was bought.
        var entry = (await application.OrdersOfAsync(agent.Id))[^1];
        Assert.Equal((OrderPurpose.Entry, OrderStatus.Canceled, 0m), (entry.Purpose, entry.Status, entry.ExecutedQuantity));
        Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.DoesNotContain(application.Binance.Orders, order => order.Rests);
    }

    [Fact]
    public async Task The_first_close_of_the_next_UTC_day_resumes_an_agent_that_its_daily_loss_limit_halted_and_gives_it_its_cycle()
    {
        var midnight = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        using var application = new CyclesApplication(midnight, storedAgents: true);
        var ada = await application.Application.RegisterVerifiedAsync("ada@example.com");
        var grace = await application.Application.RegisterVerifiedAsync("grace@example.com");
        await application.Application.GiveKeysAsync(ada.Id, midnight.AddDays(-1));
        await application.Application.GiveKeysAsync(grace.Id, midnight.AddDays(-1));

        // Halted yesterday for its daily loss limit: the day of the halt is over.
        var yesterday = await application.Application.GiveAgentAsync(
            ada.Id, midnight.AddHours(-3), name: "Daily", state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);

        // Halted yesterday for its drawdown limit: its user resumes it, and nobody else.
        var drawdown = await application.Application.GiveAgentAsync(
            ada.Id, midnight.AddHours(-3), name: "Drawdown", state: AgentState.Halted, symbol: "SOLUSDT", cause: AgentStateCause.DrawdownLimitReached);

        // An agent of another user that runs, as on any day.
        var running = await application.Application.GiveAgentAsync(
            grace.Id, midnight.AddHours(-3), name: "Running", state: AgentState.Running, cause: AgentStateCause.UserStarted);

        await application.RunAsync(midnight).Within();

        // The halt of the first is over, and it is resumed before the agents that run are
        // read: it has its cycle of this close, as the one that ran all along has.
        Assert.Equal(new[] { yesterday, running }.Order(), application.Cycles.StartedFor.Order());
        Assert.Equal(("Running", "DailyLossHaltEnded", midnight), AgentControlTests.StateOf(application.Application, yesterday));
        Assert.Equal(("Halted", "DrawdownLimitReached", midnight.AddHours(-3)), AgentControlTests.StateOf(application.Application, drawdown));

        // It is the service's doing, and no action of a user: nothing is in the audit trail.
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gives a user an agent that runs, whose loss limits were evaluated once, as its first cycle
    /// does before anything of it is placed: it has lost nothing, and the day begins here.
    /// </summary>
    private static async Task<Agent> WatchedAsync(OrdersApplication application, Market market = Market.Spot)
    {
        var agent = await application.GiveAgentAsync(market: market);

        Assert.Equal(LossLimitVerdict.Within, await EquityMarkTests.EvaluateAsync(application, agent));

        return agent;
    }

    /// <summary>
    /// Evaluates the loss limits of an agent, as a cycle of it and a reconciliation do, and
    /// moves the clock on while a close that follows waits for a moment of it.
    /// </summary>
    internal static async Task<LossLimitVerdict> EvaluateAsync(OrdersApplication application, Agent agent)
    {
        var evaluating = application.Application.LossLimits.EvaluateAsync(agent, TestContext.Current.CancellationToken);
        await application.WhileTimePassesAsync(evaluating);

        return await evaluating;
    }

    /// <summary>The user of an agent resumes it, on the page of its state, with a team that can work.</summary>
    private static async Task ResumeAsync(OrdersApplication application, Agent agent)
    {
        await application.Application.GiveTeamAsync(agent.UserId, agent.Id, application.Clock.GetUtcNow());

        using var client = application.Application.CreateClient();
        await client.SignInAsync("ada@example.com");

        Assert.Equal(AgentBrowser.AgentStatePage(agent.Id), (await client.ResumeAgentAsync(agent.Id)).Path);
    }

    /// <summary>Stores an agent in a state, as its user's control or the service leaves it there.</summary>
    private static async Task MoveAsync(OrdersApplication application, Guid agentId, AgentState state)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<Agent>()
            .Where(agent => agent.Id == agentId)
            .ExecuteUpdateAsync(row => row.SetProperty(agent => agent.State, state), TestContext.Current.CancellationToken);
    }
}
