using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The Risk Guard is applied after the Risk Manager and before any order, and neither AI output
/// nor a user action can bypass it for a trade (FR-RSK-3). These run cycles as the application
/// runs them, and hold what goes on of each plan to the fixed rules, with the facts as they are
/// when the plan is checked (FR-RSK-4), and to the outcome of every rule in the record of the
/// cycle (FR-RSK-9). See docs/risk-guard.md.
/// </summary>
public class RiskGuardCycleTests
{
    /// <summary>A close of a candle of 15 minutes, and of no longer one.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    /// <summary>A close of a candle of 15 minutes and of one of an hour.</summary>
    private static readonly DateTimeOffset Hour = new(2026, 11, 18, 11, 0, 0, TimeSpan.Zero);

    /// <summary>A plan to open a short position: its stop-loss stands above the price, and its take-profit below.</summary>
    private static readonly string OpenShort = TeamAnswers.Plan("open_short", stopLoss: 61000m, takeProfit: 56000m);

    /// <summary>A plan to reverse a long position, which opens a short one.</summary>
    private static readonly string Reverse = TeamAnswers.Plan("reverse", stopLoss: 61000m, takeProfit: 56000m);

    /// <summary>The rules that need the size of an order, which nothing computes yet (FR-RSK-5).</summary>
    private static readonly GuardRule[] NeedASize = [GuardRule.QuantityStep, GuardRule.MinimumValue];

    public static TheoryData<string, string, string?, bool, Market, CycleOutcome, PlanAction[]> WhatGoesOn => new()
    {
        { "a plan that the Risk Manager approved", TeamAnswers.Plan(), TeamAnswers.Review(), false, Market.Spot, CycleOutcome.Approved, [PlanAction.OpenLong] },
        {
            "a plan that the Risk Manager reduced",
            TeamAnswers.Plan(), TeamAnswers.Review("reduce", 0.004m), false, Market.Spot, CycleOutcome.Reduced, [PlanAction.OpenLong]
        },
        { "a plan to open short", OpenShort, TeamAnswers.Review(), false, Market.Futures, CycleOutcome.Approved, [PlanAction.OpenShort] },
        { "a plan to close", TeamAnswers.Close, null, true, Market.Spot, CycleOutcome.Unreviewed, [PlanAction.Close] },
        { "a plan to adjust protection", TeamAnswers.Adjust, null, true, Market.Spot, CycleOutcome.Unreviewed, [PlanAction.AdjustProtection] },

        // Nothing of these goes on, so there is nothing to check.
        { "a plan to hold", TeamAnswers.Hold, null, false, Market.Spot, CycleOutcome.Hold, [] },
        { "a plan that the Risk Manager vetoed", TeamAnswers.Plan(), TeamAnswers.Review("veto"), false, Market.Spot, CycleOutcome.Vetoed, [] },
    };

    [Theory]
    [MemberData(nameof(WhatGoesOn))]
    public async Task Every_plan_that_goes_on_is_checked_by_the_Risk_Guard_and_a_plan_that_does_not_is_not(
        string what, string plan, string? review, bool holdsAPosition, Market market, CycleOutcome outcome, PlanAction[] checkedPlans)
    {
        using var cycles = new CyclesApplication(
            Quarter, buildsPackages: true, holdings: holdsAPosition ? PlanAgents.Holding(PlanAgents.Long) : null);
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: market);

        cycles.Models.Says(Role.Strategist, plan);

        if (review is not null)
        {
            cycles.Models.Says(Role.RiskManager, review);
        }

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        // The outcome of the cycle is the decision of its team, whatever the Risk Guard made of it.
        Assert.True(outcome == record.Cycle.Outcome, $"The cycle of {what} ended as {record.Cycle.Outcome}.");
        Assert.Equal(checkedPlans, record.Checks.Select(check => check.Check.Action));

        // Each check is of this cycle and of its user, and says the outcome of every rule.
        Assert.All(record.Checks, check =>
        {
            Assert.Equal((record.Cycle.Id, record.Cycle.UserId), (check.Check.CycleId, check.Check.UserId));
            Assert.Equal(Enum.GetValues<GuardRule>(), check.Rules.Select(rule => rule.Rule));
            Assert.All(check.Rules, rule => Assert.Equal((check.Check.Id, check.Check.UserId), (rule.CheckId, rule.UserId)));
        });

        // The facts are read for a plan that is checked, and for no other.
        var asked = checkedPlans.Length > 0 ? 1 : 0;

        Assert.Equal((asked, asked), (cycles.Market!.AskedPrices, cycles.Market.AskedSymbols));
        Assert.Equal(0, cycles.SummaryOf(Quarter)["Failed"].GetInt32());
    }

    [Fact]
    public async Task A_cycle_with_no_decision_has_nothing_that_goes_on_to_the_Risk_Guard()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        // A plan that Spot does not allow, twice: the Strategist gave no valid result.
        cycles.Models.Says(Role.Strategist, OpenShort, OpenShort);

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        Assert.Equal(CycleOutcome.NoDecision, record.Cycle.Outcome);
        Assert.Empty(record.Checks);
        Assert.Equal(0, cycles.Market!.AskedPrices);
    }

    [Fact]
    public async Task The_Risk_Guard_checks_a_plan_after_the_Risk_Manager_and_calls_no_model()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var models = cycles.Models;
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        models.Hold(Role.RiskManager);

        var run = cycles.RunAsync(Quarter);

        // While the Risk Manager reviews the plan, nothing of the plan is checked: no fact is read.
        await models.StartedAsync(Role.RiskManager);

        Assert.Equal((0, 0), (cycles.Market!.AskedPrices, cycles.Market.AskedSymbols));

        models.Release(Role.RiskManager);
        await run.Within();

        // The review is over, and the plan is checked: on the price and the rules of the
        // symbol as they are now.
        Assert.Equal((1, 1), (cycles.Market.AskedPrices, cycles.Market.AskedSymbols));
        Assert.Single(cycles.Records.Of(agent).Checks);

        // The four calls of the team, of which the Risk Manager's is the last: the Risk Guard
        // made none.
        Assert.Equal(4, models.Calls.Count);
        Assert.Equal(Role.RiskManager, models.Roles[^1]);
    }

    [Theory]
    [InlineData("open_long", Market.Spot)]
    [InlineData("open_long", Market.Futures)]
    [InlineData("open_short", Market.Futures)]
    public async Task A_plan_to_open_a_position_is_rejected_while_nothing_computes_the_size_of_its_order(string action, Market market)
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: market);

        cycles.Models.Says(Role.Strategist, action == "open_short" ? OpenShort : TeamAnswers.Plan());

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);
        var check = record.Check;

        // The team decided, and its decision stands in the record. The Risk Guard rejected the plan.
        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal((1, GuardVerdict.Rejected), (check.Check.Sequence, check.Check.Verdict));
        Assert.Equal(Quarter, check.Check.CheckedAt);

        // The rules that need the size of the order are not known to hold, and on Futures the
        // one that needs its leverage. Every other rule that the plan is held to holds.
        GuardRule[] unknown = market == Market.Futures ? [.. NeedASize, GuardRule.LiquidationBuffer] : NeedASize;
        GuardRule[] notAsked = market == Market.Futures
            ? [GuardRule.ProtectionTightens]
            : [GuardRule.ProtectionTightens, GuardRule.LiquidationBuffer];

        Assert.Equal(unknown, RulesThat(check, RuleOutcome.Unknown));
        Assert.Equal(notAsked, RulesThat(check, RuleOutcome.NotApplied));
        Assert.Empty(RulesThat(check, RuleOutcome.Failed));

        // The limits are those of the agent's own settings, and the stale limit that of its
        // timeframe in the file of the tests.
        Assert.Equal(
            [(GuardRule.RewardToRisk, 1.75m), (GuardRule.EntryDistance, 0.8m), (GuardRule.DailyLossLimit, 5m), (GuardRule.DrawdownLimit, 20m), (GuardRule.NotStale, 100m)],
            check.Rules
                .Where(rule => rule.Rule is GuardRule.RewardToRisk or GuardRule.EntryDistance or GuardRule.DailyLossLimit or GuardRule.DrawdownLimit or GuardRule.NotStale)
                .Select(rule => (rule.Rule, rule.Threshold!.Value)));

        // A rejection is a decision, and no failure: it is said once, with the rules that
        // stand in the way, and with no price and no number of the plan.
        var rejected = Assert.Single(cycles.Written(PlanChecksLog.Area));

        Assert.Equal((PlanChecksLog.Rejected, "Information", agent.ToString()), (rejected.Template, rejected.Level, rejected.Text("AgentId")));
        Assert.Equal(string.Join(", ", unknown.Select(rule => $"{rule}: Unknown")), rejected.Text("GuardRules"));
        Assert.True(rejected.Has("CycleId"));
        Assert.Equal((1, 0), (cycles.SummaryOf(Quarter)["Ended"].GetInt32(), cycles.SummaryOf(Quarter)["Failed"].GetInt32()));
    }

    [Theory]
    [InlineData("an entry too far from the current price", new[] { GuardRule.EntryDistance })]
    [InlineData("a reward-to-risk ratio below the minimum", new[] { GuardRule.RewardToRisk })]
    [InlineData("a stop-loss on the wrong side of the entry", new[] { GuardRule.StopLossSide, GuardRule.RewardToRisk })]
    [InlineData("a price off the price step", new[] { GuardRule.PriceStep })]
    [InlineData("a position that opened meanwhile", new[] { GuardRule.HeldPosition })]
    [InlineData("a loss of the day at the limit", new[] { GuardRule.DailyLossLimit })]
    [InlineData("a drawdown at the limit", new[] { GuardRule.DrawdownLimit })]
    [InlineData("a decision that is stale by now", new[] { GuardRule.NotStale })]
    [InlineData("no valid Binance key", new[] { GuardRule.ModePermitted, GuardRule.MarketPermitted })]
    [InlineData("a key that may not trade on the market", new[] { GuardRule.MarketPermitted })]
    [InlineData("a symbol that the exchange takes no orders in", new[] { GuardRule.SymbolPermitted })]
    [InlineData("a symbol that the market does not have", new[] { GuardRule.SymbolPermitted })]
    [InlineData("a global halt", new[] { GuardRule.NoGlobalHalt })]
    public async Task A_plan_to_open_a_position_is_held_to_the_facts_as_they_are_when_it_is_checked(string what, GuardRule[] broken)
    {
        // What the agent holds as the cycle starts: nothing, and it has lost nothing.
        var holdings = new StandInHoldings(new AgentHoldings(Equity: 1000m, Position: null, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m));
        var halt = new StandInHalt();

        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: holdings, halt: halt);
        var market = cycles.Market!;
        var agent = cycles.Agents!.Give(Guid.NewGuid());
        var plan = TeamAnswers.Plan();

        switch (what)
        {
            case "an entry too far from the current price":
                // A limit entry at 60000, and a price that is 600 above it: the agent allows 0.8 percent.
                plan = TeamAnswers.Plan(entryType: "limit", entryPrice: 60000m);
                market.Price = 60600m;
                break;

            case "a reward-to-risk ratio below the minimum":
                // 1500 of reward against 1000 of risk: the agent asks for 1.75.
                plan = TeamAnswers.Plan(takeProfit: 61500m);
                break;

            case "a stop-loss on the wrong side of the entry":
                // A long that enters at the market, at 60000, with its stop-loss above that.
                plan = TeamAnswers.Plan(stopLoss: 60500m);
                break;

            case "a price off the price step":
                market.Symbols = _ => [StandInMarket.Listed("BTCUSDT", priceStep: 7m)];
                break;

            case "a position that opened meanwhile":
                // The team was told that the agent holds nothing. By the time its plan is
                // checked, the agent holds a position.
                market.AsThePriceIsRead = () => holdings.Holds = holdings.Holds with { Position = PlanAgents.Long };
                break;

            case "a loss of the day at the limit":
                // 50 of 1000 since the day began, which is the 5 percent of the agent.
                market.AsThePriceIsRead = () => holdings.Holds = holdings.Holds with { Equity = 950m };
                break;

            case "a drawdown at the limit":
                // 200 below a peak of 1000, which is the 20 percent of the agent, on a day with no loss.
                market.AsThePriceIsRead = () => holdings.Holds = holdings.Holds with { Equity = 800m, EquityAtDayStart = 800m };
                break;

            case "a decision that is stale by now":
                // In time when the Risk Manager answered, and one second past the limit of 100
                // seconds when the facts have been read.
                market.AsThePriceIsRead = () => cycles.Clock.Set(Quarter.AddSeconds(101));
                break;

            case "no valid Binance key":
                cycles.Team!.KeyChecks.Block = StartBlock.NotValid;
                break;

            case "a key that may not trade on the market":
                cycles.Team!.KeyChecks.Block = StartBlock.SpotTradingMissing;
                break;

            case "a symbol that the exchange takes no orders in":
                market.Symbols = _ => [StandInMarket.Listed("BTCUSDT", trading: false)];
                break;

            case "a symbol that the market does not have":
                market.Symbols = _ => [StandInMarket.Listed("SOLUSDT")];
                break;

            case "a global halt":
                halt.On = true;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(what), what, "No such fact.");
        }

        cycles.Models.Says(Role.Strategist, plan);

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);
        var check = record.Check;

        // The Risk Manager approved the plan, and the team's decision was in time. The fixed
        // rules reject it all the same: nothing that a model answered stands above them.
        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Empty(cycles.Written(TeamLog.Area, TeamLog.Stale));
        Assert.True(check.Check.Verdict == GuardVerdict.Rejected, $"A plan with {what} was accepted.");
        Assert.Equal(broken, RulesThat(check, RuleOutcome.Failed));

        // It is said once, and names each rule that failed.
        var rejected = Assert.Single(cycles.Written(PlanChecksLog.Area, PlanChecksLog.Rejected));

        Assert.All(broken, rule => Assert.Contains($"{rule}: Failed", rejected.Text("GuardRules"), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Timeframe.FifteenMinutes, 100, RuleOutcome.Passed)]
    [InlineData(Timeframe.FifteenMinutes, 101, RuleOutcome.Failed)]
    [InlineData(Timeframe.OneHour, 200, RuleOutcome.Passed)]
    [InlineData(Timeframe.OneHour, 201, RuleOutcome.Failed)]
    public async Task The_Risk_Guard_holds_a_decision_to_the_stale_limit_of_the_timeframe_of_its_agent(
        Timeframe timeframe, int checkedAfterSeconds, RuleOutcome outcome)
    {
        using var cycles = new CyclesApplication(Hour, buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid(), timeframe);

        // The team decides at the close. The plan is checked so many seconds after it.
        cycles.Market!.AsThePriceIsRead = () => cycles.Clock.Set(Hour.AddSeconds(checkedAfterSeconds));

        await cycles.RunAsync(Hour).Within();

        var record = cycles.Records.Of(agent);
        var stale = record.Check.Rules.Single(rule => rule.Rule == GuardRule.NotStale);

        // The limits of the file of the tests: 100 seconds for 15m, and 200 for 1H. At the
        // limit itself the decision is in time.
        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal((outcome, checkedAfterSeconds), (stale.Outcome, stale.Measured));
        Assert.Equal(timeframe == Timeframe.FifteenMinutes ? 100m : 200m, stale.Threshold);
        Assert.Equal(Hour.AddSeconds(checkedAfterSeconds), record.Check.Check.CheckedAt);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("adjust protection")]
    public async Task A_plan_that_only_closes_a_position_or_adjusts_protection_goes_on_whatever_stops_an_entry(string what)
    {
        // Everything that rejects a plan which opens a position: a global halt, no valid key, a
        // symbol that the exchange takes no orders in, both loss limits, and a decision that is
        // checked long after the stale limit.
        var holdings = new StandInHoldings(new AgentHoldings(
            Equity: 500m, PlanAgents.Long, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m));

        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: holdings, halt: new StandInHalt(on: true));
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        cycles.Team!.KeyChecks.Block = StartBlock.NoKey;
        cycles.Market!.Symbols = _ => [StandInMarket.Listed("BTCUSDT", trading: false)];
        cycles.Market.AsThePriceIsRead = () => cycles.Clock.Set(Quarter.AddMinutes(10));
        cycles.Models.Says(Role.Strategist, what == "close" ? TeamAnswers.Close : TeamAnswers.Adjust);

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);
        var check = record.Check;

        // So that risk can always be reduced: it is held to the rules of the exchange, to the
        // position that the agent holds, and to protection that only tightens, and to nothing else.
        Assert.Equal(CycleOutcome.Unreviewed, record.Cycle.Outcome);
        Assert.Equal(GuardVerdict.Accepted, check.Check.Verdict);
        Assert.Equal(
            what == "close"
                ? [GuardRule.PriceStep, GuardRule.QuantityStep, GuardRule.HeldPosition]
                : [GuardRule.PriceStep, GuardRule.QuantityStep, GuardRule.HeldPosition, GuardRule.ProtectionTightens],
            RulesThat(check, RuleOutcome.Passed));
        Assert.Empty(RulesThat(check, RuleOutcome.Failed));
        Assert.Empty(RulesThat(check, RuleOutcome.Unknown));

        var accepted = Assert.Single(cycles.Written(PlanChecksLog.Area));

        Assert.Equal(
            (PlanChecksLog.Accepted, "Information", what == "close" ? "Close" : "AdjustProtection"),
            (accepted.Template, accepted.Level, accepted.Text("PlanAction")));
    }

    [Theory]
    [InlineData("a stop-loss that was moved up meanwhile", GuardRule.ProtectionTightens)]
    [InlineData("a position that was closed meanwhile", GuardRule.HeldPosition)]
    public async Task An_adjustment_is_held_to_the_position_that_the_agent_holds_when_it_is_checked(string what, GuardRule broken)
    {
        // The team is told of a long position whose stop-loss rests at 59500, and plans to
        // move it up to 60500, which reduces risk.
        var holdings = PlanAgents.Holding(PlanAgents.Long);

        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: holdings);
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        cycles.Models.Says(Role.Strategist, TeamAnswers.Adjust);
        cycles.Market!.AsThePriceIsRead = () => holdings.Holds = holdings.Holds with
        {
            // By the time the plan is checked, the stop-loss rests above where the plan
            // moves it to, so the move would add risk. Or the position is gone.
            Position = what == "a position that was closed meanwhile" ? null : PlanAgents.Long with { StopLoss = 60800m },
        };

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        // A plan that was valid for the position that the team was told of (FR-CYC-6) is
        // asked again of the position as it is.
        Assert.Equal(CycleOutcome.Unreviewed, record.Cycle.Outcome);
        Assert.Equal(GuardVerdict.Rejected, record.Check.Check.Verdict);
        Assert.Equal([broken], RulesThat(record.Check, RuleOutcome.Failed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Of_a_reverse_that_the_Risk_Guard_rejects_the_close_alone_is_checked_and_goes_on(bool halted)
    {
        using var cycles = new CyclesApplication(
            Quarter, buildsPackages: true, holdings: PlanAgents.Holding(PlanAgents.Long), halt: new StandInHalt(halted));
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        cycles.Models.Says(Role.Strategist, Reverse);

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        // The Risk Manager approved the reverse. What it opens is rejected, as every plan that
        // opens a position is while its order has no size, and with a halt for that as well.
        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(
            [(1, PlanAction.Reverse, GuardVerdict.Rejected), (2, PlanAction.Close, GuardVerdict.Accepted)],
            record.Checks.Select(check => (check.Check.Sequence, check.Check.Action, check.Check.Verdict)));

        var reverse = record.Checks[0];

        Assert.Equal([.. NeedASize, GuardRule.LiquidationBuffer], RulesThat(reverse, RuleOutcome.Unknown));
        Assert.Equal(halted ? [GuardRule.NoGlobalHalt] : [], RulesThat(reverse, RuleOutcome.Failed));

        // The close reduces risk: it is checked as a plan to close is, on the same facts, and
        // goes on. Open positions are still managed while a halt is on (FR-OPS-3).
        var close = record.Checks[1];

        Assert.Equal([GuardRule.PriceStep, GuardRule.QuantityStep, GuardRule.HeldPosition], RulesThat(close, RuleOutcome.Passed));
        Assert.Equal(13, RulesThat(close, RuleOutcome.NotApplied).Count);
        Assert.Equal(reverse.Check.CheckedAt, close.Check.CheckedAt);

        // The facts were read once for both, and no model was called for either.
        Assert.Equal((1, 1), (cycles.Market!.AskedPrices, cycles.Market.AskedSymbols));
        Assert.Equal(4, cycles.Models.Calls.Count);
        Assert.Equal(
            [(PlanChecksLog.Rejected, "Reverse"), (PlanChecksLog.Accepted, "Close")],
            cycles.Written(PlanChecksLog.Area).Select(written => (written.Template, written.Text("PlanAction")!)));
    }

    [Fact]
    public async Task Of_a_reverse_whose_position_is_gone_nothing_goes_on()
    {
        var holdings = PlanAgents.Holding(PlanAgents.Long);

        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: holdings);
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        cycles.Models.Says(Role.Strategist, Reverse);

        // The stop-loss of the position was hit while the team was called.
        cycles.Market!.AsThePriceIsRead = () => holdings.Holds = holdings.Holds with { Position = null };

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        // There is nothing to reverse, and nothing to close.
        Assert.Equal(
            [(PlanAction.Reverse, GuardVerdict.Rejected), (PlanAction.Close, GuardVerdict.Rejected)],
            record.Checks.Select(check => (check.Check.Action, check.Check.Verdict)));
        Assert.All(record.Checks, check => Assert.Equal([GuardRule.HeldPosition], RulesThat(check, RuleOutcome.Failed)));
    }

    [Fact]
    public async Task An_exchange_that_gives_no_answer_stops_an_entry_and_no_close()
    {
        // An agent that holds nothing, whose team plans to open, and one that holds a position,
        // whose team plans to close it.
        foreach (var (plan, holds, verdict, outcome) in new[]
        {
            (TeamAnswers.Plan(), (PositionContext?)null, GuardVerdict.Rejected, CycleOutcome.Approved),
            (TeamAnswers.Close, PlanAgents.Long, GuardVerdict.Accepted, CycleOutcome.Unreviewed),
        })
        {
            using var cycles = new CyclesApplication(
                Quarter, buildsPackages: true, holdings: holds is null ? null : PlanAgents.Holding(holds));
            var agent = cycles.Agents!.Give(Guid.NewGuid());

            // The exchange says neither the price nor the rules of its symbols.
            cycles.Market!.PriceOutcome = ExchangeCallOutcome.TimedOut;
            cycles.Market.SymbolsOutcome = ExchangeCallOutcome.Failed;
            cycles.Models.Says(Role.Strategist, plan);

            await cycles.RunAsync(Quarter).Within();

            var record = cycles.Records.Of(agent);
            var check = record.Check;

            // The cycle did not fail: what could not be asked is not known, which is a reason
            // to open nothing, and no reason to keep a position open.
            Assert.Equal((outcome, verdict), (record.Cycle.Outcome, check.Check.Verdict));
            Assert.Equal(0, cycles.SummaryOf(Quarter)["Failed"].GetInt32());
            Assert.Empty(RulesThat(check, RuleOutcome.Failed));

            Assert.Equal(
                verdict == GuardVerdict.Rejected
                    ?
                    [
                        GuardRule.StopLossSide, GuardRule.RewardToRisk, GuardRule.EntryDistance, GuardRule.PriceStep,
                        GuardRule.QuantityStep, GuardRule.MinimumValue, GuardRule.SymbolPermitted,
                    ]
                    : [GuardRule.QuantityStep],
                RulesThat(check, RuleOutcome.Unknown));

            // Each read that gave nothing is a warning, with how it ended.
            Assert.Equal(
                [("the price", "TimedOut", "Warning"), ("the rules of the symbols", "Failed", "Warning")],
                cycles.Written(PlanChecksLog.Area, PlanChecksLog.Missing)
                    .Select(written => (written.Text("Missing")!, written.Text("ExchangeCallOutcome")!, written.Level)));
        }
    }

    /// <summary>The rules of a check that have an outcome, in the order of the rules.</summary>
    private static IReadOnlyList<GuardRule> RulesThat(RiskCheckRecord check, RuleOutcome outcome) =>
        [.. check.Rules.Where(rule => rule.Outcome == outcome).Select(rule => rule.Rule)];
}
