using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What a cycle leaves for Experience as it is recorded: the call of each analyst that called a
/// direction, to be scored against the next 4 closed candles (FR-EXP-3), and the plan that the
/// Risk Manager vetoed, as a shadow trade with its own entry, stop-loss, and take-profit
/// (FR-EXP-5). See "What a cycle leaves for Experience" in docs/experience.md.
/// </summary>
public class CycleExperienceTests
{
    private const string Ada = "ada@example.com";

    private const string Bearish =
        """{"bias":"bearish","notableEvents":[],"confidence":0.7,"liveDataAvailable":true,"rationale":"The headlines turned."}""";

    // The close of the candle that the cycles of these tests are for, whose last price is 61310
    // in the summary of their data package.
    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private const decimal PriceAtClose = 61310m;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cycle_is_stored_with_a_call_for_each_analyst_that_was_not_neutral()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close, market: Market.Futures, mode: TradingMode.Live, timeframe: Timeframe.FourHours);
        var analyst = await application.GiveRoleAsync(ada.Id, agent, Role.MarketAnalyst, Close, "claude", "claude-other-model");

        // Its Market Analyst is bullish, and its Sentiment Analyst neutral: one call is left to score.
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Close, orders: false);

        var call = Assert.Single(await CallsAsync(application));

        // It says what the record of the cycle says, in columns of its own: who made it, for
        // whom, in which mode, on which market and timeframe, and from which price.
        Assert.Equal((cycle, ada.Id, Role.MarketAnalyst, analyst), (call.CycleId, call.UserId, call.Role, call.ModelId));
        Assert.Equal(
            (TradingMode.Live, Market.Futures, "BTCUSDT", Timeframe.FourHours),
            (call.Mode, call.Market, call.Symbol, call.Timeframe));
        Assert.Equal((Close, CallDirection.Bullish, PriceAtClose), (call.ClosedAt, call.Direction, call.Price));

        // It is due once the 4th candle after its own has closed, and nothing is said of it until then.
        Assert.Equal(Close.AddHours(16), call.DueAt);
        Assert.Null(call.Outcome);
        Assert.Null(call.LaterPrice);
        Assert.Null(call.ScoredAt);

        // A cycle whose Sentiment Analyst called a direction too leaves two.
        var both = await application.GiveCycleAsync(
            ada.Id, agent, Close.AddHours(4), orders: false, answers: role => role == Role.SentimentAnalyst ? Bearish : TeamAnswers.Of(role));

        Assert.Equal(
            [(Role.MarketAnalyst, CallDirection.Bullish), (Role.SentimentAnalyst, CallDirection.Bearish)],
            (await CallsAsync(application)).Where(left => left.CycleId == both).Select(left => (left.Role, left.Direction)));

        // And no cycle that was not vetoed leaves a shadow trade.
        Assert.Empty(await ShadowTradesAsync(application));
    }

    [Fact]
    public async Task A_call_is_left_however_its_cycle_ended()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // The analyst called a direction whatever the team made of it afterwards: a plan that
        // held, a decision that was stale, a role that gave no decision, or a veto.
        var outcomes = new[] { CycleOutcome.Hold, CycleOutcome.Stale, CycleOutcome.NoDecision, CycleOutcome.Vetoed, CycleOutcome.Reduced };

        foreach (var (outcome, hour) in outcomes.Select((outcome, hour) => (outcome, hour)))
        {
            await application.GiveCycleAsync(ada.Id, agent, Close.AddHours(hour), outcome, orders: false);
        }

        Assert.Equal(outcomes.Length, (await CallsAsync(application)).Count);
    }

    [Fact]
    public async Task A_vetoed_plan_is_stored_as_a_shadow_trade_with_its_own_prices()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        var reviewer = await application.GiveRoleAsync(ada.Id, agent, Role.RiskManager, Close, "claude", "claude-other-model");

        // A long at the market, wrong at 59000 and done at 64000, which the Risk Manager vetoed.
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Vetoed, orders: false);

        var trade = Assert.Single(await ShadowTradesAsync(application));

        // It is of the model that vetoed the plan, and says what the record of the cycle says.
        Assert.Equal((cycle, ada.Id, reviewer), (trade.CycleId, trade.UserId, trade.ModelId));
        Assert.Equal(
            (TradingMode.Demo, Market.Spot, "BTCUSDT", Timeframe.OneHour, Close),
            (trade.Mode, trade.Market, trade.Symbol, trade.Timeframe, trade.ClosedAt));

        // An entry at the market is entered at the close of the candle that the plan was made on.
        Assert.Equal(
            (ShadowSide.Long, PriceAtClose, false, 59000m, 64000m),
            (trade.Side, trade.EntryPrice, trade.Resting, trade.StopLoss, trade.TakeProfit));

        // It is looked at once the next candle has closed, and has not ended.
        Assert.Equal(Close.AddHours(1), trade.DueAt);
        Assert.Null(trade.Outcome);
        Assert.Null(trade.ResultInR);
        Assert.Null(trade.ExitedAt);
        Assert.Null(trade.EvaluatedAt);

        // An entry at a price is entered at that price, where it rests.
        var atPrice = await application.GiveCycleAsync(
            ada.Id,
            agent,
            Close.AddHours(1),
            CycleOutcome.Vetoed,
            orders: false,
            answers: role => role == Role.Strategist ? TeamAnswers.Plan(entryType: "limit", entryPrice: 60500.5m) : TeamAnswers.Of(role));

        var resting = Assert.Single(await ShadowTradesAsync(application), left => left.CycleId == atPrice);

        Assert.Equal((60500.5m, true, 59000m, 64000m), (resting.EntryPrice, resting.Resting, resting.StopLoss, resting.TakeProfit));
    }

    [Fact]
    public async Task A_vetoed_short_and_a_vetoed_reverse_are_on_the_side_that_they_would_have_opened()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close, market: Market.Futures);

        string Short(string action) => TeamAnswers.Plan(action, stopLoss: 63000m, takeProfit: 58000m);

        // A plan to open a short.
        var opened = await application.GiveCycleAsync(
            ada.Id, agent, Close, CycleOutcome.Vetoed, orders: false, answers: role => role == Role.Strategist ? Short("open_short") : TeamAnswers.Of(role));

        // A plan to reverse names no side: it enters on the other side of what the agent held,
        // which the summary of the data package says. The agent held a long, so it is a short.
        var reversed = await application.GiveCycleAsync(
            ada.Id,
            agent,
            Close.AddHours(1),
            CycleOutcome.Vetoed,
            orders: false,
            answers: role => role == Role.Strategist ? Short("reverse") : TeamAnswers.Of(role),
            position: new PositionContext(PositionSide.Long, 60000m, 0.01m, 59000m, 64000m));

        // And one that reverses a short is a long.
        var reversedShort = await application.GiveCycleAsync(
            ada.Id,
            agent,
            Close.AddHours(2),
            CycleOutcome.Vetoed,
            orders: false,
            answers: role => role == Role.Strategist ? TeamAnswers.Plan("reverse") : TeamAnswers.Of(role),
            position: new PositionContext(PositionSide.Short, 62000m, 0.01m, 63000m, 58000m));

        var sides = (await ShadowTradesAsync(application)).ToDictionary(trade => trade.CycleId, trade => (trade.Side, trade.StopLoss, trade.TakeProfit));

        Assert.Equal((ShadowSide.Short, 63000m, 58000m), sides[opened]);
        Assert.Equal((ShadowSide.Short, 63000m, 58000m), sides[reversed]);
        Assert.Equal((ShadowSide.Long, 59000m, 64000m), sides[reversedShort]);
    }

    [Fact]
    public async Task A_vetoed_plan_whose_prices_are_on_the_wrong_side_of_its_entry_has_ended_with_no_result()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A long whose stop-loss is above the price: nothing asked where it stands before the
        // veto, and the Risk Guard would have rejected the plan (FR-RSK-4). So it is no trade.
        await application.GiveCycleAsync(
            ada.Id,
            agent,
            Close,
            CycleOutcome.Vetoed,
            orders: false,
            answers: role => role == Role.Strategist ? TeamAnswers.Plan(stopLoss: 62000m, takeProfit: 64000m) : TeamAnswers.Of(role));

        var trade = Assert.Single(await ShadowTradesAsync(application));

        Assert.Equal(ShadowOutcome.Rejected, trade.Outcome);
        Assert.Null(trade.ResultInR);

        // It ended as the cycle did, and is followed over no candle.
        Assert.Equal(Close.AddSeconds(40), trade.EvaluatedAt);
    }

    [Fact]
    public async Task A_test_cycle_leaves_no_call_and_no_shadow_trade()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A user runs as many test cycles for one candle as they like: each would be the same
        // call, counted again, and none of them places an order (FR-CYC-12).
        await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Vetoed, orders: false, kind: CycleKind.Test);
        await application.GiveCycleAsync(ada.Id, agent, Close, orders: false, kind: CycleKind.Test);

        Assert.Empty(await CallsAsync(application));
        Assert.Empty(await ShadowTradesAsync(application));
        Assert.Equal(2, application.Database.Scalar<long>("SELECT count(*) FROM decision_cycles"));
    }

    [Fact]
    public async Task The_latest_valid_answer_of_a_role_is_the_one_that_is_read()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        await application.GiveCycleAsync(ada.Id, agent, Close, orders: false);

        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);
        var given = await context.Set<RoleOutput>().AsNoTracking().SingleAsync(output => output.Role == Role.MarketAnalyst, Cancellation);
        var package = await context.Set<DecisionCycle>().AsNoTracking().Select(stored => stored.DataPackage).SingleAsync(Cancellation);

        // The Market Analyst was called twice in the next cycle: its first answer was rejected,
        // and kept, and its second is valid. The first called the other direction.
        var next = new DecisionCycle(
            Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, Close.AddHours(1), Close.AddHours(1), Close.AddHours(1).AddSeconds(50),
            CycleOutcome.NoDecision, package!.Replace(Stamp(Close), Stamp(Close.AddHours(1)), StringComparison.Ordinal));

        RoleOutput[] outputs =
        [
            Copy(given, next.Id, attempt: 1, RoleValidation.Rejected, TeamAnswers.MarketReport.Replace("bullish", "bearish", StringComparison.Ordinal)),
            Copy(given, next.Id, attempt: 2, RoleValidation.Valid, TeamAnswers.MarketReport),
        ];

        await application.CycleRecords.StoreAsync(next, outputs, [], Cancellation);

        Assert.Equal(CallDirection.Bullish, Assert.Single(await CallsAsync(application), call => call.CycleId == next.Id).Direction);

        // Two valid answers of one role are not what a cycle stores. Where a record has them all
        // the same, the later one is read, and nothing fails.
        var twice = new DecisionCycle(
            Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, Close.AddHours(2), Close.AddHours(2), Close.AddHours(2).AddSeconds(50),
            CycleOutcome.NoDecision, package.Replace(Stamp(Close), Stamp(Close.AddHours(2)), StringComparison.Ordinal));

        await application.CycleRecords.StoreAsync(
            twice,
            [
                Copy(given, twice.Id, attempt: 1, RoleValidation.Valid, TeamAnswers.MarketReport),
                Copy(given, twice.Id, attempt: 2, RoleValidation.Valid, TeamAnswers.MarketReport.Replace("bullish", "bearish", StringComparison.Ordinal)),
            ],
            [],
            Cancellation);

        Assert.Equal(CallDirection.Bearish, Assert.Single(await CallsAsync(application), call => call.CycleId == twice.Id).Direction);
    }

    [Fact]
    public async Task What_cannot_be_read_leaves_no_row_and_the_cycle_is_stored_all_the_same()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // Answers that are no JSON, that lack what their schema says, and that say something
        // else than it allows: nothing of the application stores such an answer as valid. Where
        // a record holds one all the same, the cycle is stored, and nothing is made of the answer.
        string[] unreadable =
        [
            "not JSON at all",
            "{}",
            """{"bias":7}""",
            """{"bias":"sideways"}""",
            """{"action":"open_long","entryType":"limit","entryPrice":null,"stopLoss":59000,"takeProfit":64000}""",
            """{"action":"open_long","entryType":"market","entryPrice":null,"stopLoss":"low","takeProfit":64000}""",
            """{"action":"hold","entryType":null,"entryPrice":null,"stopLoss":null,"takeProfit":null}""",
            """{"action":"reverse","entryType":"market","entryPrice":null,"stopLoss":63000,"takeProfit":58000}""",
        ];

        foreach (var (answer, hour) in unreadable.Select((answer, hour) => (answer, hour)))
        {
            await application.GiveCycleAsync(ada.Id, agent, Close.AddHours(hour), CycleOutcome.Vetoed, orders: false, answers: _ => answer);
        }

        Assert.Equal(unreadable.Length, application.Database.Scalar<long>("SELECT count(*) FROM decision_cycles"));
        Assert.Equal(unreadable.Length * 4, application.Database.Scalar<long>("SELECT count(*) FROM role_outputs"));
        Assert.Empty(await CallsAsync(application));
        Assert.Empty(await ShadowTradesAsync(application));

        // A cycle that ended before its data package was built says no market, no timeframe,
        // and no price: nothing is left of it either.
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);
        var given = await context.Set<RoleOutput>().AsNoTracking().FirstAsync(output => output.Role == Role.MarketAnalyst, Cancellation);
        var early = new DecisionCycle(
            Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, Close.AddDays(1), Close.AddDays(1), Close.AddDays(1), CycleOutcome.Failed);

        await application.CycleRecords.StoreAsync(
            early, [Copy(given, early.Id, attempt: 1, RoleValidation.Valid, TeamAnswers.MarketReport)], [], Cancellation);

        Assert.Empty(await CallsAsync(application));
    }

    [Fact]
    public async Task What_a_cycle_leaves_goes_with_its_agent()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(ada.Id, Close, name: "Kept");
        var deleted = await application.GiveAgentAsync(ada.Id, Close, name: "Deleted", symbol: "SOLUSDT");
        var keptCycle = await application.GiveCycleAsync(ada.Id, kept, Close, CycleOutcome.Vetoed, orders: false);
        await application.GiveCycleAsync(ada.Id, deleted, Close, CycleOutcome.Vetoed, orders: false);

        Assert.Equal(2, (await CallsAsync(application)).Count);
        Assert.Equal(2, (await ShadowTradesAsync(application)).Count);

        // The rows hang on the cycle, which hangs on the agent: deleting the agent deletes them.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Ecnanib.Agents.Agent>().Where(agent => agent.Id == deleted).ExecuteDeleteAsync(Cancellation);
        }

        Assert.Equal(keptCycle, Assert.Single(await CallsAsync(application)).CycleId);
        Assert.Equal(keptCycle, Assert.Single(await ShadowTradesAsync(application)).CycleId);
    }

    private static async Task<IReadOnlyList<AnalystCall>> CallsAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<AnalystCall>().AsNoTracking().OrderBy(call => call.ClosedAt).ThenBy(call => call.Role).ToListAsync(Cancellation);
    }

    private static async Task<IReadOnlyList<ShadowTrade>> ShadowTradesAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<ShadowTrade>().AsNoTracking().OrderBy(trade => trade.ClosedAt).ToListAsync(Cancellation);
    }

    /// <summary>A time as the summary of a data package writes it.</summary>
    private static string Stamp(DateTimeOffset at) => System.Text.Json.JsonSerializer.Serialize(at).Trim('"');

    /// <summary>A call of the same role and model, with the same templates, for another cycle.</summary>
    private static RoleOutput Copy(RoleOutput given, Guid cycleId, int attempt, RoleValidation validation, string result) =>
        new(
            Guid.CreateVersion7(),
            cycleId,
            given.UserId,
            given.Role,
            attempt,
            given.ModelId,
            given.RoleTemplateId,
            given.MarketTemplateId,
            given.TimeframeTemplateId,
            given.ProfileTemplateId,
            given.CalledAt,
            given.LatencyMs,
            given.InputTokens,
            given.OutputTokens,
            given.LiveSearches,
            validation == RoleValidation.Valid ? RoleCallOutcome.Succeeded : RoleCallOutcome.InvalidResult,
            validation,
            validation == RoleValidation.Valid ? null : "bias: NotAllowed",
            result);
}
