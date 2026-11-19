using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each cycle is stored as a complete record: data package summary, every role output, Risk
/// Guard results, orders, and final outcome (FR-CYC-10). A cycle that ends with no decision or
/// no order is still recorded, with its outcome. See "The record of a cycle" in docs/team.md.
/// </summary>
public class CycleRecordTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    /// <summary>A quarter hour in the middle of a week, which closes a candle of 15 minutes and of no other length.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    /// <summary>A Monday at midnight in UTC: a candle of every length closes then.</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Market.Spot, Timeframe.FifteenMinutes)]
    [InlineData(Market.Futures, Timeframe.OneDay)]
    public async Task The_summary_says_what_the_data_package_said_and_keeps_no_candle_but_the_latest(Market market, Timeframe timeframe)
    {
        // An agent that holds a position, with its two protective orders resting.
        OpenOrder[] resting =
        [
            new(OpenOrderKind.StopLoss, OrderSide.Sell, 0.01m, 59500m),
            new(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.01m, 64000m),
        ];
        var holdings = new StandInHoldings(
            new AgentHoldings(Equity: 1012.3456789m, PlanAgents.Long, resting, EquityAtDayStart: 1000m, PeakEquity: 1020m));

        using var application = new PackagesApplication(Monday, holdings);
        var agent = PackageAgents.On(market, timeframe);
        var package = await application.BuildAsync(agent, Monday);

        var builtAt = Monday.AddSeconds(7);
        var summary = DataPackageSummary.Of(package, builtAt);

        // The agent as its prompt stated it: its market, its timeframe, its symbol, its equity
        // with every digit, its position, and its limits.
        Assert.Equal((builtAt, market, timeframe, "BTCUSDT", "USDT"), (summary.BuiltAt, summary.Market, summary.Timeframe, summary.Symbol, summary.QuoteAsset));
        Assert.Equal(1012.3456789m, summary.Equity);
        Assert.Equal(PlanAgents.Long, summary.Position);
        Assert.Equal(package.Agent.Limits, summary.Limits);
        Assert.Equal(resting, summary.Orders);

        // A series for the agent's own timeframe and for each higher one, in the order of the
        // package: how many candles it held, the latest of them, and the indicators at its close.
        CandleSeries[] series = [package.Candles, .. package.Context];

        Assert.Equal(series.Select(held => TimeframeContext.Code(held.Interval)), summary.Series.Select(kept => kept.Timeframe));
        Assert.Equal(timeframe.Code(), summary.Series[0].Timeframe);
        Assert.All(series.Zip(summary.Series), pair =>
        {
            var (held, kept) = pair;
            var latest = held.Candles[^1];

            Assert.Equal((held.Candles.Count, held.ComputedFrom), (kept.Candles, kept.ComputedFrom));
            Assert.Equal(
                new CandleSummary(latest.ClosedAt, latest.Open, latest.High, latest.Low, latest.Close, latest.Volume),
                kept.Latest);
            Assert.Equal(held.Indicators, kept.Indicators);
        });
        Assert.Equal(Monday, summary.Series[0].Latest!.ClosedAt);

        // What Futures adds, for an agent on Futures, and nothing of it for one on Spot.
        Assert.Equal(package.Futures, summary.Futures);
        Assert.Equal(market == Market.Futures, summary.Futures is not null);

        // It is stored as one line of JSON, with one candle for each series: the database is
        // not a market-data warehouse (NFR-DAT-6).
        var stored = summary.ToJson();

        Assert.DoesNotContain('\n', stored);
        Assert.Equal(summary.Series.Count, Regex.Count(stored, "\"closedAt\""));
        Assert.True(package.Candles.Candles.Count > 100, "The package of the test holds many candles.");
        Assert.InRange(stored.Length, 500, 5000);

        // A member of a fixed set is stored by its name, and a number with every digit that it has.
        Assert.Contains($"\"market\":\"{market}\"", stored, StringComparison.Ordinal);
        Assert.Contains($"\"timeframe\":\"{timeframe}\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"equity\":1012.3456789", stored, StringComparison.Ordinal);
        Assert.Contains("\"side\":\"Long\"", stored, StringComparison.Ordinal);

        // It says nothing of whose agent it is (FR-PRM-9): no id, and no name.
        Assert.DoesNotContain(agent.Id.ToString(), stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(agent.UserId.ToString(), stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", stored, StringComparison.Ordinal);

        // And it is read back as it was written.
        var read = DataPackageSummary.Read(stored);

        Assert.NotNull(read);
        Assert.Equal(stored, read.ToJson());
        Assert.Equal((summary.Equity, summary.Position, summary.Limits, summary.Futures), (read.Equity, read.Position, read.Limits, read.Futures));
        Assert.Equal(summary.Series, read.Series);
        Assert.Equal(summary.Orders, read.Orders);
    }

    [Fact]
    public void A_series_that_has_no_closed_candle_and_a_row_that_keeps_no_summary_are_read_as_such()
    {
        // A symbol that is new to the exchange has no closed week: the series has no candle
        // and no indicator, which is no failure.
        var none = new IndicatorValues(null, null, null, null, null, null, null, null);
        var empty = SeriesSummary.Of(new CandleSeries(CandleInterval.OneWeek, [], ComputedFrom: 0, none));

        Assert.Equal(new SeriesSummary("1W", 0, 0, Latest: null, none), empty);

        // A cycle that ended before its package was built stored none, and so did a cycle that
        // was recorded before a summary was kept. What does not read as one is none either.
        Assert.Null(DataPackageSummary.Read(null));
        Assert.Null(DataPackageSummary.Read(string.Empty));
        Assert.Null(DataPackageSummary.Read("not a summary"));
        Assert.Null(DataPackageSummary.Read("{}"));
        Assert.Null(DataPackageSummary.Read("[1,2]"));
    }

    [Fact]
    public async Task A_cycle_is_recorded_with_the_summary_of_the_data_package_that_its_team_decided_on()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        await application.RunAsync(Quarter).Within();

        var record = application.Records.Of(agent);
        var summary = DataPackageSummary.Read(record.Cycle.DataPackage);

        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.NotNull(summary);

        // The package of the cycle: the candles of 15 minutes through its close, and those of
        // the hour and of four hours, as the file of the tests sizes them.
        Assert.Equal((Market.Spot, Timeframe.FifteenMinutes, "BTCUSDT", "USDT", 1000m), (summary.Market, summary.Timeframe, summary.Symbol, summary.QuoteAsset, summary.Equity));
        Assert.Null(summary.Position);
        Assert.Null(summary.Futures);
        Assert.Empty(summary.Orders);
        Assert.Equal(["15m", "1H", "4H"], summary.Series.Select(series => series.Timeframe));
        Assert.Equal([120, 30, 30], summary.Series.Select(series => series.Candles));
        Assert.Equal(Quarter, summary.Series[0].Latest!.ClosedAt);
        Assert.All(summary.Series, series =>
        {
            Assert.True(series.Latest!.ClosedAt <= Quarter);
            Assert.True(series.ComputedFrom >= series.Candles);
            Assert.NotNull(series.Indicators.Sma20);

            // The exchange of the test gives 150 candles, which is too few for an average of 200.
            Assert.Null(series.Indicators.Sma200);
        });

        // It was built while the cycle ran, before its team was called.
        Assert.InRange(summary.BuiltAt, record.Cycle.StartedAt, record.Cycle.EndedAt);
        Assert.All(record.Outputs, output => Assert.True(output.CalledAt >= summary.BuiltAt));

        // The plan placed its entry: nothing says that it placed none.
        Assert.Null(record.Cycle.NoEntry);
    }

    /// <summary>What a cycle ends as when its team decides nothing, or nothing that an order follows from.</summary>
    public static TheoryData<string, CycleOutcome> WithoutAnOrder => new()
    {
        { "hold", CycleOutcome.Hold },
        { "veto", CycleOutcome.Vetoed },
        { "no plan", CycleOutcome.NoDecision },
        { "no market data", CycleOutcome.Failed },
    };

    [Theory]
    [MemberData(nameof(WithoutAnOrder))]
    public async Task A_cycle_that_ends_with_no_decision_or_no_order_is_still_recorded_with_its_outcome(string ends, CycleOutcome outcome)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        switch (ends)
        {
            case "hold":
                application.Models.Says(Role.Strategist, TeamAnswers.Hold);
                break;

            case "veto":
                application.Models.Says(Role.RiskManager, TeamAnswers.Review("veto"));
                break;

            case "no plan":
                // A plan that risks half of the equity is rejected, also when it is planned once more.
                application.Models.Says(Role.Strategist, TeamAnswers.Plan(riskFraction: 0.5m), TeamAnswers.Plan(riskFraction: 0.5m));
                break;

            default:
                application.Market!.Candles = asked => asked.Interval == CandleInterval.OneHour
                    ? StandInMarket.NoCandles(ExchangeCallOutcome.RateLimited)
                    : null;
                break;
        }

        await application.RunAsync(Quarter).Within();

        // One record, with how the cycle ended, for the close that it was for.
        var record = Assert.Single(application.Records.Stored);

        Assert.Equal((agent, Quarter, outcome), (record.Cycle.AgentId, record.Cycle.ClosedAt, record.Cycle.Outcome));
        Assert.True(record.Cycle.StartedAt <= record.Cycle.EndedAt);

        // Nothing went on to the Risk Guard, and nothing was handed over to be placed.
        Assert.Empty(record.Checks);
        Assert.Empty(application.Orders.Placed);
        Assert.Null(record.Cycle.NoEntry);

        // A cycle whose package was built keeps its summary, however it ends. One whose market
        // data the exchange did not give has none to keep, and called no model.
        if (outcome == CycleOutcome.Failed)
        {
            Assert.Null(record.Cycle.DataPackage);
            Assert.Empty(record.Outputs);
        }
        else
        {
            Assert.NotNull(DataPackageSummary.Read(record.Cycle.DataPackage));
            Assert.NotEmpty(record.Outputs);
        }
    }

    [Fact]
    public async Task Why_no_entry_was_placed_for_a_plan_that_the_Risk_Guard_accepted_is_kept_with_the_cycle()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var skipped = application.Agents!.Give(Guid.NewGuid());

        // What places the orders skips the entry: the account does not cover it.
        application.Orders.NoEntry = NoEntryReason.QuoteBalance;

        await application.RunAsync(Quarter).Within();

        var record = application.Records.Of(skipped);

        // The outcome stays the decision of the team, the check of the Risk Guard says that the
        // plan was accepted, and the record says why no entry followed.
        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(GuardVerdict.Accepted, record.Check.Check.Verdict);
        Assert.Single(application.Orders.Placed);
        Assert.Equal(NoEntryReason.QuoteBalance, record.Cycle.NoEntry);
    }

    [Fact]
    public async Task What_places_the_orders_says_why_it_placed_no_entry_and_nothing_where_it_placed_one()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // An agent of the Spot market does not sell to open.
        Assert.Equal(NoEntryReason.SpotLongOnly, await application.PlaceAsync(agent, GuardCases.OpenShort with { Market = Market.Spot }));
        Assert.Empty(await application.OrdersOfAsync(agent.Id));

        // An entry that is placed has a row, which says what became of it.
        Assert.Null(await application.PlaceAsync(agent, GuardCases.OpenLong));
        Assert.Contains(await application.OrdersOfAsync(agent.Id), order => order.Purpose == OrderPurpose.Entry);

        // The agent holds a position now, so a second entry is refused, and says so.
        Assert.Equal(NoEntryReason.Holding, await application.PlaceAsync(agent, GuardCases.OpenLong));

        // Every reason that a refusal of the rows gives has a name of its own in the record.
        Assert.All(Enum.GetNames<EntryRefusal>(), refusal => Assert.Contains(refusal, Enum.GetNames<NoEntryReason>()));
    }

    [Fact]
    public async Task A_recorded_cycle_is_read_as_a_complete_record()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close, market: Market.Futures);
        var cycleId = await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Reduced);

        var record = await application.RecordedCycles.FindAsync(ada.Id, agent, cycleId, TestContext.Current.CancellationToken);

        Assert.NotNull(record);

        // The cycle: which close it was for, when it ran, in which mode, and how it ended.
        Assert.Equal((cycleId, Close, TradingMode.Demo, CycleOutcome.Reduced), (record.Id, record.ClosedAt, record.Mode, record.Outcome));
        Assert.Equal((Close.AddSeconds(2), Close.AddSeconds(40)), (record.StartedAt, record.EndedAt));
        Assert.Null(record.NoEntry);

        // The summary of its data package.
        Assert.NotNull(record.DataPackage);
        Assert.Equal((Market.Futures, Timeframe.OneHour, "BTCUSDT", Close.AddSeconds(3)), (record.DataPackage.Market, record.DataPackage.Timeframe, record.DataPackage.Symbol, record.DataPackage.BuiltAt));
        Assert.Equal(["1H", "1W"], record.DataPackage.Series.Select(series => series.Timeframe));
        Assert.NotNull(record.DataPackage.Futures);

        // Every role output, in the order of the calls, each with its provider and its model
        // as users read them, the versions of the four templates of its prompt, and its answer.
        Assert.Equal(Enum.GetValues<Role>(), record.Calls.Select(call => call.Role));
        Assert.All(record.Calls, call =>
        {
            Assert.Equal((1, "Claude", StoredAgentRows.TeamModel), (call.Attempt, call.ProviderName, call.ModelIdentifier));
            Assert.Equal(
                [PromptTemplateKind.Role, PromptTemplateKind.Market, PromptTemplateKind.Timeframe, PromptTemplateKind.Profile],
                call.Templates.Select(template => template.Kind));
            Assert.Equal(
                [call.Role.ToString(), "Futures", "1H"],
                call.Templates.Take(3).Select(template => template.Subject));
            Assert.All(call.Templates, template => Assert.True(template.Version >= 1));
            Assert.Equal((Close.AddSeconds(3), 1500, 1200, 150), (call.CalledAt, call.LatencyMs, call.InputTokens, call.OutputTokens));
            Assert.Equal((RoleCallOutcome.Succeeded, RoleValidation.Valid, (string?)null), (call.Outcome, call.Validation, call.Rejection));
            Assert.Equal(TeamAnswers.Of(call.Role), call.Result);
        });

        // The Risk Guard result, with the outcome of every rule, in the order of the rules.
        var check = Assert.Single(record.Checks);

        Assert.Equal((cycleId, Close, 1, PlanAction.OpenLong, GuardVerdict.Accepted), (check.CycleId, check.ClosedAt, check.Sequence, check.Action, check.Verdict));
        Assert.Equal(Enum.GetValues<GuardRule>(), check.Rules.Select(rule => rule.Rule));

        // The orders that the cycle placed, in the order they were written, each as it is now.
        Assert.Equal(
            [(OrderPurpose.Entry, OrderType.Market, OrderSide.Buy, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderType.Stop, OrderSide.Sell, OrderStatus.Filled)],
            record.Orders.Select(order => (order.Purpose, order.Type, order.Side, order.Status)));
        Assert.All(record.Orders, order => Assert.Equal(("BTCUSDT", 0.01m, 0.01m), (order.Symbol, order.Quantity, order.ExecutedQuantity)));
        Assert.Equal(["7001", "7002"], record.Orders.Select(order => order.ExchangeOrderId));
    }

    [Fact]
    public async Task A_cycle_that_placed_no_order_is_read_with_its_outcome_and_with_why_it_placed_no_entry()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A cycle with no decision, which called no role with a valid result and checked no
        // plan: the row of the cycle alone. And one whose entry was skipped.
        var undecided = new DecisionCycle(
            Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, Close, Close.AddSeconds(2), Close.AddSeconds(9), CycleOutcome.NoDecision);

        await application.CycleRecords.StoreAsync(undecided, [], [], TestContext.Current.CancellationToken);

        var skipped = await application.GiveCycleAsync(
            ada.Id, agent, Close.AddHours(1), CycleOutcome.Approved, orders: false, noEntry: NoEntryReason.Margin);

        var none = await application.RecordedCycles.FindAsync(ada.Id, agent, undecided.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(none);
        Assert.Equal(CycleOutcome.NoDecision, none.Outcome);
        Assert.Null(none.DataPackage);
        Assert.Empty(none.Calls);
        Assert.Empty(none.Checks);
        Assert.Empty(none.Orders);
        Assert.Null(none.NoEntry);

        var noEntry = await application.RecordedCycles.FindAsync(ada.Id, agent, skipped, TestContext.Current.CancellationToken);

        Assert.NotNull(noEntry);
        Assert.Equal((CycleOutcome.Approved, NoEntryReason.Margin), (noEntry.Outcome, noEntry.NoEntry));
        Assert.Single(noEntry.Checks);
        Assert.Empty(noEntry.Orders);

        // Why no entry was placed is stored by its name, as every member of a fixed set is.
        Assert.Equal(
            ["NoDecision/", "Approved/Margin"],
            application.Database.Column("SELECT outcome || '/' || coalesce(no_entry, '') FROM decision_cycles ORDER BY closed_at"));
    }

    [Fact]
    public async Task The_cycles_of_an_agent_are_listed_latest_first_whatever_their_outcome()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        var other = await application.GiveAgentAsync(ada.Id, Close, name: "Other", symbol: "SOLUSDT");

        var none = await application.RecordedCycles.LatestAsync(ada.Id, agent, TestContext.Current.CancellationToken);

        Assert.Equal(0, none.Total);
        Assert.Empty(none.Cycles);

        // 53 cycles, an hour apart, which end in every way that a cycle ends in turn.
        var outcomes = Enum.GetValues<CycleOutcome>();

        for (var hour = 0; hour < 53; hour++)
        {
            var closedAt = Close.AddHours(hour);

            await application.CycleRecords.StoreAsync(
                new DecisionCycle(
                    Guid.CreateVersion7(), agent, ada.Id, TradingMode.Demo, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(30), outcomes[hour % outcomes.Length]),
                [],
                [],
                TestContext.Current.CancellationToken);
        }

        // And one of another agent of the same user, which is no cycle of this one.
        await application.GiveCycleAsync(ada.Id, other, Close.AddDays(30));

        var history = await application.RecordedCycles.LatestAsync(ada.Id, agent, TestContext.Current.CancellationToken);

        // The latest 50, from the latest close down, each with how it ended.
        Assert.Equal(53, history.Total);
        Assert.Equal(50, history.Cycles.Count);
        Assert.Equal(
            Enumerable.Range(3, 50).Reverse().Select(hour => (Close.AddHours(hour), outcomes[hour % outcomes.Length])),
            history.Cycles.Select(cycle => (cycle.ClosedAt, cycle.Outcome)));
        Assert.All(history.Cycles, cycle => Assert.Equal((TradingMode.Demo, TimeSpan.FromSeconds(28)), (cycle.Mode, cycle.EndedAt - cycle.StartedAt)));

        // Every outcome is among them: a cycle that failed, was stopped, or decided nothing is listed like any other.
        Assert.Equal(outcomes, history.Cycles.Select(cycle => cycle.Outcome).Distinct().Order());
    }

    [Fact]
    public async Task The_record_of_a_cycle_is_read_by_its_user_for_its_agent_and_holds_nothing_of_another_cycle()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Close);
        var adasOther = await application.GiveAgentAsync(ada.Id, Close, name: "Other", symbol: "SOLUSDT");
        var graces = await application.GiveAgentAsync(grace.Id, Close);

        var first = await application.GiveCycleAsync(ada.Id, adas, Close);
        var second = await application.GiveCycleAsync(ada.Id, adas, Close.AddHours(1));
        var ofOther = await application.GiveCycleAsync(ada.Id, adasOther, Close);
        var gracesCycle = await application.GiveCycleAsync(grace.Id, graces, Close);
        var before = application.RowsOf(grace.Id);

        var cycles = application.RecordedCycles;
        var cancellation = TestContext.Current.CancellationToken;

        // A cycle of another user is not found, whichever agent it is asked for, and neither
        // are the cycles of that user's agent (NFR-SEC-3).
        Assert.Null(await cycles.FindAsync(ada.Id, graces, gracesCycle, cancellation));
        Assert.Null(await cycles.FindAsync(ada.Id, adas, gracesCycle, cancellation));
        Assert.Equal(0, (await cycles.LatestAsync(ada.Id, graces, cancellation)).Total);
        Assert.NotNull(await cycles.FindAsync(grace.Id, graces, gracesCycle, cancellation));

        // A cycle of another agent of the same user is not found under this agent.
        Assert.Null(await cycles.FindAsync(ada.Id, adas, ofOther, cancellation));
        Assert.NotNull(await cycles.FindAsync(ada.Id, adasOther, ofOther, cancellation));

        // A cycle that does not exist is not found.
        Assert.Null(await cycles.FindAsync(ada.Id, adas, Guid.CreateVersion7(), cancellation));

        // Each record holds its own calls, checks, and orders, and none of the other cycle.
        var one = await cycles.FindAsync(ada.Id, adas, first, cancellation);
        var two = await cycles.FindAsync(ada.Id, adas, second, cancellation);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal((4, 1, 2), (one.Calls.Count, one.Checks.Count, one.Orders.Count));
        Assert.Equal((4, 1, 2), (two.Calls.Count, two.Checks.Count, two.Orders.Count));
        Assert.All(one.Calls, call => Assert.Equal(Close.AddSeconds(3), call.CalledAt));
        Assert.All(two.Calls, call => Assert.Equal(Close.AddHours(1).AddSeconds(3), call.CalledAt));
        Assert.Empty(one.Orders.Select(order => order.ClientOrderId).Intersect(two.Orders.Select(order => order.ClientOrderId)));
        Assert.Equal((first, second), (Assert.Single(one.Checks).CycleId, Assert.Single(two.Checks).CycleId));

        // Reading changes nothing.
        Assert.Equal(before, application.RowsOf(grace.Id));

        // The orders of a cycle are found by an index of their own.
        await using var context = await application.Contexts.CreateDbContextAsync(cancellation);

        Assert.Contains(
            context.Model.FindEntityType(typeof(Order))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(Order.CycleId)]));
    }
}
