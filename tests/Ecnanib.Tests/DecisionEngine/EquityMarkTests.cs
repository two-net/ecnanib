using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// When an agent's loss for the UTC day reaches the daily loss limit, the system closes its
/// position and halts it until the next UTC day. When its drawdown from peak equity reaches the
/// drawdown limit, the system closes its position and halts it until the user resumes it
/// (FR-RSK-8). This is what both are counted from: the equity of an agent as the UTC day began,
/// and its peak equity, which are kept for an agent in a mode. See "The loss limits" in
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000 USDT, and buys 0.01 at 60000 for a fee of 0.6:
/// see <see cref="AgentResultTests"/> for its equity. What is done when a limit is reached is in
/// <see cref="LossLimitHaltTests"/>.
/// </remarks>
public class EquityMarkTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_first_evaluation_of_an_agent_keeps_what_its_loss_limits_are_counted_from()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Nothing is kept for an agent until its loss limits are evaluated: its first cycle
        // does, before anything is placed.
        Assert.Empty(await MarksOfAsync(application, agent.Id));

        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        // One row for the agent in its mode: the UTC day, and a result of nothing as the day
        // began and at its peak. The equity of the agent is its capital above each. The row
        // says the asset that the result is in, and the capital that it was worked out under.
        var mark = Assert.Single(await MarksOfAsync(application, agent.Id));
        Assert.Equal(
            (agent.UserId, TradingMode.Demo, Today, 0m, 0m, OrdersApplication.Start),
            (mark.UserId, mark.Mode, mark.DayBeganAt, mark.ResultAtDayStart, mark.PeakResult, mark.UpdatedAt));
        Assert.Equal(("USDT", 1000m), (mark.Asset, mark.Capital));

        // An evaluation that finds nothing new writes nothing.
        application.Clock.Set(OrdersApplication.Start.AddMinutes(5));
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));
        Assert.Equal(OrdersApplication.Start, Assert.Single(await MarksOfAsync(application, agent.Id)).UpdatedAt);
    }

    [Fact]
    public async Task The_peak_is_the_highest_that_the_equity_of_an_agent_has_been_with_unrealized_profit()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // At 62000 the position has made 20: the equity is 1019.4 with the fee, which is its peak.
        application.Binance.Price = 62000m;
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        var holds = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((1019.4m, 1000m, 1019.4m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));

        // The price comes back. The peak stays, and the drawdown is counted from it.
        application.Binance.Price = 60000m;
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        holds = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((999.4m, 1000m, 1019.4m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));
        Assert.Equal(19.4m, Assert.Single(await MarksOfAsync(application, agent.Id)).PeakResult);
    }

    [Fact]
    public async Task What_only_reads_the_equity_says_a_peak_that_is_higher_now_and_writes_nothing()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 62000m;

        // As the Risk Guard checks a plan, between two evaluations: the equity is its own peak.
        var holds = await AgentResultTests.HoldsAsync(application, agent);

        Assert.Equal((1019.4m, 1019.4m), (holds.Equity, holds.PeakEquity));
        Assert.Equal(0m, Assert.Single(await MarksOfAsync(application, agent.Id)).PeakResult);
    }

    [Fact]
    public async Task The_equity_as_the_UTC_day_began_is_the_first_that_is_worked_out_in_the_day()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // In the day the position loses 30: the day began at 1000.
        application.Binance.Price = 57000m;
        application.Clock.Set(Today.AddDays(1).AddSeconds(-1));
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        var holds = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((969.4m, 1000m), (holds.Equity, holds.EquityAtDayStart));

        // Midnight, UTC: a new day. What reads the equity says so at once, before anything is
        // written: nothing was lost in a day that has just begun.
        application.Clock.Set(Today.AddDays(1));

        holds = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((969.4m, 969.4m, 1000m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));
        Assert.Equal(Today, Assert.Single(await MarksOfAsync(application, agent.Id)).DayBeganAt);

        // The first evaluation of the day keeps it: the close of midnight starts a cycle of
        // every agent that runs, which evaluates its loss limits first.
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        var mark = Assert.Single(await MarksOfAsync(application, agent.Id));
        Assert.Equal((Today.AddDays(1), -30.6m, 0m), (mark.DayBeganAt, mark.ResultAtDayStart, mark.PeakResult));

        // What the agent loses from then on is counted from there, for the whole day.
        application.Binance.Price = 56000m;
        application.Clock.Set(Today.AddDays(1).AddHours(23));
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, agent));

        holds = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((959.4m, 969.4m, 1000m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(5000)]
    public async Task A_capital_that_its_user_changes_reaches_no_loss_limit_and_takes_an_agent_out_of_none(decimal capital)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 62000m;
        await EvaluateAsync(application, agent);

        // At 59000 the agent has lost 10.6 of the 1000 that it had as the day began, and is
        // 30 below its peak of 1019.4: 1.06 percent, and 2.94 percent.
        application.Binance.Price = 59000m;
        var before = await AgentResultTests.HoldsAsync(application, agent);
        Assert.Equal((989.4m, 1000m, 1019.4m), (before.Equity, before.EquityAtDayStart, before.PeakEquity));

        // Its user changes the capital of the agent, which applies from its next cycle.
        var changed = AgentResultTests.With(agent, capital: capital);

        // The equity is the capital as it is now, with what the agent made and lost.
        var after = await AgentResultTests.HoldsAsync(application, changed);
        Assert.Equal(capital - 10.6m, after.Equity);

        // What it has lost of its equity in the day, and from its peak, is the same share as
        // before the change. Of a capital of 200 the 10.6 would be 5.3 percent, and the daily
        // loss limit of 5 percent would be reached by the change alone.
        Assert.Equal(Share(before.Equity, before.EquityAtDayStart), Share(after.Equity, after.EquityAtDayStart));
        Assert.Equal(Share(before.Equity, before.PeakEquity), Share(after.Equity, after.PeakEquity));

        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, changed));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);

        // The marks are stored for the capital as it is now, and are worked out no further.
        var mark = Assert.Single(await MarksOfAsync(application, agent.Id));
        Assert.Equal(capital, mark.Capital);

        var kept = await AgentResultTests.HoldsAsync(application, changed);
        Assert.Equal((after.EquityAtDayStart, after.PeakEquity), (kept.EquityAtDayStart, kept.PeakEquity));

        // From there the agent loses as any agent does: 5 percent of what it had as the day
        // began halts it, of whatever capital.
        application.Binance.Price = 50000m;
        Assert.Equal(capital == 5000m ? LossLimitVerdict.Within : LossLimitVerdict.Halted, await LossLimitHaltTests.EvaluateAsync(application, changed));

        static decimal Share(decimal equity, decimal from) => Math.Round(equity / from, 9);
    }

    [Fact]
    public async Task An_agent_that_is_given_a_symbol_priced_in_another_asset_starts_its_marks_anew_in_that_asset()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 62000m;
        await EvaluateAsync(application, agent);
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        // In USDT it has made 20, less the fees, and its peak is above its capital.
        Assert.Equal(19.4m, Assert.Single(await MarksOfAsync(application, agent.Id)).PeakResult);

        // Its user stops it and gives it a symbol that is priced in BTC, with a capital in BTC.
        // What it made in USDT is no result in BTC: it has made and lost nothing there, and a
        // peak that was kept in USDT says nothing of it.
        var inBitcoin = new Agent(
            agent.Id, agent.UserId, agent.Name, agent.Mode, agent.Market, symbol: "ETHBTC", quoteAsset: "BTC", agent.Timeframe,
            allocatedCapital: 0.02m, agent.RiskPerTradePercent, agent.MinRewardToRisk, agent.LeverageCap, agent.LiquidationBuffer,
            agent.DailyLossPercent, agent.DrawdownPercent, agent.MaxEntryDistancePercent, agent.State, agent.StateCause,
            agent.StateChangedAt, agent.CreatedAt);

        var holds = await AgentResultTests.HoldsAsync(application, inBitcoin);
        Assert.Equal((0.02m, 0.02m, 0.02m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));

        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, inBitcoin));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);

        var mark = Assert.Single(await MarksOfAsync(application, agent.Id));
        Assert.Equal(("BTC", 0.02m, 0m, 0m), (mark.Asset, mark.Capital, mark.ResultAtDayStart, mark.PeakResult));
    }

    [Fact]
    public async Task An_agent_has_marks_of_its_own_in_each_mode()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await EvaluateAsync(application, agent);
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 62000m;
        await EvaluateAsync(application, agent);
        application.Binance.Price = 60000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // The same agent once it is switched to Live: its result there starts at nothing, and
        // so does what its loss limits are counted from (FR-BIN-11).
        Assert.Equal(LossLimitVerdict.Within, await EvaluateAsync(application, AgentResultTests.With(agent, mode: TradingMode.Live)));

        Assert.Equal(
            [(TradingMode.Demo, 19.4m), (TradingMode.Live, 0m)],
            (await MarksOfAsync(application, agent.Id)).Select(mark => (mark.Mode, mark.PeakResult)));
    }

    /// <summary>
    /// Evaluates the loss limits of an agent, as a cycle of it and a reconciliation do, at the
    /// moment that the clock shows: an evaluation that halts nothing waits for nothing, so the
    /// clock stays where the test set it.
    /// </summary>
    internal static Task<LossLimitVerdict> EvaluateAsync(OrdersApplication application, Agent agent) =>
        application.Application.LossLimits.EvaluateAsync(agent, TestContext.Current.CancellationToken);

    /// <summary>What is kept for an agent, in the order of the modes.</summary>
    internal static async Task<IReadOnlyList<EquityMark>> MarksOfAsync(OrdersApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return
        [
            .. (await context.Set<EquityMark>().AsNoTracking().Where(mark => mark.AgentId == agentId).ToListAsync(TestContext.Current.CancellationToken))
                .OrderBy(mark => mark.Mode),
        ];
    }
}
