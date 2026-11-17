using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What a trade plan may do. Allowed actions on Spot are open long, hold, close, and adjust
/// protection, and Futures adds open short and reverse (FR-CYC-4). An agent holds at most one
/// position, and adding to an open position is not supported (FR-CYC-5). Adjust protection may
/// move the stop-loss only in the direction that reduces risk, and the take-profit in either
/// direction (FR-CYC-6). A plan to open a position always includes a stop-loss price and a
/// take-profit price (FR-CYC-7). See "What a plan may do" in docs/team.md.
/// </summary>
public class PlanRuleTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    private static readonly Lazy<TestDatabase> Migrated = new(() =>
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        return database;
    });

    /// <summary>The six actions of a plan, as an answer names them.</summary>
    private static readonly string[] Actions = ["open_long", "open_short", "hold", "close", "reverse", "adjust_protection"];

    private IRoleResults Results => application.Services.GetRequiredService<IRoleResults>();

    /// <summary>
    /// Every action, on each market, for an agent that holds nothing and for one that holds a
    /// long position, with the rule that it breaks there, or none.
    /// </summary>
    public static TheoryData<Market, bool, string, RejectionRule?> ActionsOfAnAgent => new()
    {
        // On Spot, with no position: an agent opens long or holds. It has no short to open,
        // and nothing to close, to reverse, or to adjust.
        { Market.Spot, false, "open_long", null },
        { Market.Spot, false, "open_short", RejectionRule.NotOnMarket },
        { Market.Spot, false, "hold", null },
        { Market.Spot, false, "close", RejectionRule.NoPosition },
        { Market.Spot, false, "reverse", RejectionRule.NotOnMarket },
        { Market.Spot, false, "adjust_protection", RejectionRule.NoPosition },

        // On Spot, with a position: it holds, closes, or adjusts, and adds nothing.
        { Market.Spot, true, "open_long", RejectionRule.PositionOpen },
        { Market.Spot, true, "open_short", RejectionRule.NotOnMarket },
        { Market.Spot, true, "hold", null },
        { Market.Spot, true, "close", null },
        { Market.Spot, true, "reverse", RejectionRule.NotOnMarket },
        { Market.Spot, true, "adjust_protection", null },

        // Futures adds open short, with no position.
        { Market.Futures, false, "open_long", null },
        { Market.Futures, false, "open_short", null },
        { Market.Futures, false, "hold", null },
        { Market.Futures, false, "close", RejectionRule.NoPosition },
        { Market.Futures, false, "reverse", RejectionRule.NoPosition },
        { Market.Futures, false, "adjust_protection", RejectionRule.NoPosition },

        // And reverse, with one: the way to the other side is a reverse, and no second position.
        { Market.Futures, true, "open_long", RejectionRule.PositionOpen },
        { Market.Futures, true, "open_short", RejectionRule.PositionOpen },
        { Market.Futures, true, "hold", null },
        { Market.Futures, true, "close", null },
        { Market.Futures, true, "reverse", null },
        { Market.Futures, true, "adjust_protection", null },
    };

    [Theory]
    [MemberData(nameof(ActionsOfAnAgent))]
    public void A_plan_has_an_action_that_its_market_allows_for_an_agent_that_holds_at_most_one_position(
        Market market, bool holdsPosition, string action, RejectionRule? rule)
    {
        var agent = PlanAgents.On(market, holdsPosition ? PlanAgents.Long : null);
        var plan = Results.ReadPlan(PlanOf(action), agent);

        if (rule is null)
        {
            Assert.True(plan.IsValid, $"A plan to {action} on {market} was rejected: {plan.Rejection}.");
            Assert.Contains(plan.Value!.Action, PlanActions.AllowedOn(market));
        }
        else
        {
            // It is rejected whole, by the action, in the service's own words.
            Assert.Null(plan.Value);
            Assert.Equal(new RoleRejection(rule.Value, "action"), plan.Rejection);
        }
    }

    [Fact]
    public void Allowed_actions_on_Spot_are_open_long_hold_close_and_adjust_protection_and_Futures_adds_open_short_and_reverse()
    {
        Assert.Equal(
            [PlanAction.OpenLong, PlanAction.Hold, PlanAction.Close, PlanAction.AdjustProtection],
            PlanActions.AllowedOn(Market.Spot));
        Assert.Equal(
            [PlanAction.OpenShort, PlanAction.Reverse],
            PlanActions.AllowedOn(Market.Futures).Except(PlanActions.AllowedOn(Market.Spot)));
        Assert.Equal(Enum.GetValues<PlanAction>(), PlanActions.AllowedOn(Market.Futures));

        // Every action of the theory above is one of the schema, and every market is asked.
        Assert.Equal(Actions.Length, Enum.GetValues<PlanAction>().Length);
        Assert.Equal(
            Enum.GetValues<Market>().Length * 2 * Actions.Length,
            ActionsOfAnAgent.Count);
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public void The_module_of_a_market_names_the_actions_that_a_plan_is_held_to(Market market)
    {
        // The Strategist is told the allowed actions by the module of its agent's market, and
        // its plan is held to the same list: neither says an action that the other does not.
        var module = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Market, market.ToString());

        // The one sentence that lists them: "The allowed actions are open long, hold, ... ."
        const string Lists = "The allowed actions are ";
        var from = module.Text.IndexOf(Lists, StringComparison.Ordinal);

        Assert.True(from >= 0, $"The module of {market} does not list its actions.");
        Assert.Equal(from, module.Text.LastIndexOf(Lists, StringComparison.Ordinal));

        var sentence = module.Text[(from + Lists.Length)..];
        var named = sentence[..sentence.IndexOf('.', StringComparison.Ordinal)]
            .Split(", ")
            .Select(action => action.StartsWith("and ", StringComparison.Ordinal) ? action[4..] : action)
            .ToList();

        Assert.Equal(PlanActions.AllowedOn(market).Select(InWords), named);

        // And outside that sentence the module of Spot names neither action that Futures adds.
        if (market == Market.Spot)
        {
            Assert.DoesNotContain("open short", module.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reverse", module.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_agent_holds_at_most_one_position_and_adding_to_an_open_position_is_not_supported()
    {
        var adds = new RoleRejection(RejectionRule.PositionOpen, "action");

        // A long is not added to, and neither is a short: on either side, at the market or at a limit.
        Assert.All(new[] { PlanAgents.Long, PlanAgents.Short }, position =>
        {
            var agent = PlanAgents.On(Market.Futures, position);

            Assert.Equal(adds, Results.ReadPlan(TeamAnswers.Plan("open_long"), agent).Rejection);
            Assert.Equal(adds, Results.ReadPlan(TeamAnswers.Plan("open_short"), agent).Rejection);
            Assert.Equal(adds, Results.ReadPlan(TeamAnswers.Plan("open_long", "limit", entryPrice: 59800m), agent).Rejection);

            // The position is reversed, closed, and adjusted, and held.
            Assert.True(Results.ReadPlan(TeamAnswers.Plan("reverse"), agent).IsValid);
            Assert.True(Results.ReadPlan(TeamAnswers.Close, agent).IsValid);
            Assert.True(Results.ReadPlan(TeamAnswers.Hold, agent).IsValid);
        });

        // An agent with none opens one.
        Assert.True(Results.ReadPlan(TeamAnswers.Plan("open_long"), PlanAgents.On(Market.Futures)).IsValid);
        Assert.True(Results.ReadPlan(TeamAnswers.Plan("open_short"), PlanAgents.On(Market.Futures)).IsValid);
    }

    /// <summary>
    /// A new stop-loss, for a long whose stop-loss rests at 59500 and for a short whose rests at
    /// 61000, with whether it adds risk.
    /// </summary>
    public static TheoryData<string, PositionSide, decimal, bool> StopLosses => new()
    {
        { "a long, raised", PositionSide.Long, 60500m, false },
        { "a long, raised by the least", PositionSide.Long, 59500.01m, false },
        { "a long, where it is", PositionSide.Long, 59500m, false },
        { "a long, lowered by the least", PositionSide.Long, 59499.99m, true },
        { "a long, lowered", PositionSide.Long, 58000m, true },

        { "a short, lowered", PositionSide.Short, 60200m, false },
        { "a short, lowered by the least", PositionSide.Short, 60999.99m, false },
        { "a short, where it is", PositionSide.Short, 61000m, false },
        { "a short, raised by the least", PositionSide.Short, 61000.01m, true },
        { "a short, raised", PositionSide.Short, 63000m, true },
    };

    [Theory]
    [MemberData(nameof(StopLosses))]
    public void Adjust_protection_moves_the_stop_loss_only_in_the_direction_that_reduces_risk(
        string what, PositionSide side, decimal stopLoss, bool addsRisk)
    {
        var agent = PlanAgents.On(Market.Futures, side == PositionSide.Long ? PlanAgents.Long : PlanAgents.Short);

        // With the stop-loss alone, and with a take-profit beside it.
        Assert.All(new decimal?[] { null, 66000m }, takeProfit =>
        {
            var plan = Results.ReadPlan(Adjust(stopLoss, takeProfit), agent);

            if (addsRisk)
            {
                Assert.False(plan.IsValid, $"The stop-loss of {what}, was moved to where it adds risk.");
                Assert.Equal(new RoleRejection(RejectionRule.AddsRisk, "stopLoss"), plan.Rejection);
            }
            else
            {
                Assert.True(plan.IsValid, $"The stop-loss of {what}, was not moved: {plan.Rejection}.");
                Assert.Equal((PlanAction.AdjustProtection, stopLoss, takeProfit), (plan.Value!.Action, plan.Value.StopLoss, plan.Value.TakeProfit));
            }
        });
    }

    [Fact]
    public void The_take_profit_moves_in_either_direction()
    {
        // The take-profit of the long rests at 64000, and that of the short at 56000.
        Assert.All(new[] { PlanAgents.Long, PlanAgents.Short }, position =>
        {
            var agent = PlanAgents.On(Market.Futures, position);

            Assert.All(new[] { 50000m, 55999m, 56001m, 63999m, 64001m, 70000m }, takeProfit =>
            {
                var plan = Results.ReadPlan(Adjust(stopLoss: null, takeProfit), agent);

                Assert.True(plan.IsValid, $"A take-profit at {takeProfit} of a {position.Side} was rejected: {plan.Rejection}.");
                Assert.Equal(takeProfit, plan.Value!.TakeProfit);
            });
        });
    }

    [Fact]
    public void A_position_with_no_stop_loss_takes_any()
    {
        // None rests on the exchange: whatever is placed reduces the risk of having none.
        Assert.All(new[] { PlanAgents.Long, PlanAgents.Short }, position =>
        {
            var unprotected = PlanAgents.On(Market.Futures, position with { StopLoss = null });

            Assert.All(new[] { 50000m, 59000m, 61500m, 70000m }, stopLoss =>
                Assert.True(Results.ReadPlan(Adjust(stopLoss), unprotected).IsValid));
        });
    }

    [Fact]
    public void A_plan_to_open_a_position_always_includes_a_stop_loss_price_and_a_take_profit_price()
    {
        // Open long, open short, and reverse, each for an agent that may have it (FR-CYC-7).
        Assert.All(new[] { "open_long", "open_short", "reverse" }, action =>
        {
            var agent = PlanAgents.For(TeamAnswers.Plan(action));

            Assert.Equal(
                new RoleRejection(RejectionRule.Required, "stopLoss"),
                Results.ReadPlan(TeamAnswers.Plan(action, stopLoss: null), agent).Rejection);
            Assert.Equal(
                new RoleRejection(RejectionRule.Required, "takeProfit"),
                Results.ReadPlan(TeamAnswers.Plan(action, takeProfit: null), agent).Rejection);
            Assert.Equal(
                new RoleRejection(RejectionRule.Required, "stopLoss"),
                Results.ReadPlan(TeamAnswers.Plan(action, stopLoss: null, takeProfit: null), agent).Rejection);

            // A price is above zero: a stop-loss at nothing is none.
            Assert.Equal(
                new RoleRejection(RejectionRule.OutOfRange, "stopLoss"),
                Results.ReadPlan(TeamAnswers.Plan(action, stopLoss: 0m), agent).Rejection);

            var plan = Results.ReadPlan(TeamAnswers.Plan(action), agent).Value!;

            Assert.True(plan.OpensPosition);
            Assert.Equal((59000m, 64000m), (plan.StopLoss, plan.TakeProfit));
        });
    }

    public static TheoryData<string, Market, bool, string, string> PlansThatBreakARule => new()
    {
        { "a short on Spot", Market.Spot, false, TeamAnswers.Plan("open_short"), "action: NotOnMarket" },
        { "a second position", Market.Futures, true, TeamAnswers.Plan("open_long"), "action: PositionOpen" },
        { "a close of nothing", Market.Spot, false, TeamAnswers.Close, "action: NoPosition" },
        { "a stop-loss that is lowered under a long", Market.Spot, true, Adjust(58000m), "stopLoss: AddsRisk" },
    };

    [Theory]
    [MemberData(nameof(PlansThatBreakARule))]
    public async Task A_plan_that_does_what_a_plan_may_not_do_is_no_valid_result_and_nothing_goes_on(
        string what, Market market, bool holdsPosition, string answered, string rejection)
    {
        using var cycles = new CyclesApplication(
            Quarter, buildsPackages: true, holdings: holdsPosition ? PlanAgents.Holding(PlanAgents.Long) : null);
        var models = cycles.Models;
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: market);

        // The Strategist plans it, and plans it again when it is called once more.
        models.Says(Role.Strategist, answered, answered);

        await cycles.RunAsync(Quarter).Within();

        // It is called once more, as for any result that is invalid, and the Risk Manager is
        // not asked about a plan that is none (FR-TEAM-9).
        Assert.Equal(2, models.CallsOf(Role.Strategist).Count);
        Assert.Empty(models.CallsOf(Role.RiskManager));

        // The cycle ends with no decision, so nothing goes on, and the record says why.
        var record = cycles.Records.Of(agent);

        Assert.True(CycleOutcome.NoDecision == record.Cycle.Outcome, $"A cycle that planned {what} ended as {record.Cycle.Outcome}.");
        Assert.All(record.Of(Role.Strategist), output =>
            Assert.Equal((RoleValidation.Rejected, rejection, answered), (output.Validation, output.Rejection, output.Result)));
        Assert.Empty(cycles.Written(TeamLog.Area, TeamLog.Decided));

        var rejected = cycles.Written(TeamLog.RolesArea, TeamLog.Rejected);

        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, written => Assert.Equal(rejection, $"{written.Text("RejectedField")}: {written.Text("RejectionRule")}"));
    }

    [Fact]
    public async Task A_plan_that_is_allowed_the_second_time_goes_on()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        // On Spot, with no position: a short first, and then a long.
        cycles.Models.Says(Role.Strategist, TeamAnswers.Plan("open_short"), TeamAnswers.Plan("open_long"));

        await cycles.RunAsync(Quarter).Within();

        var record = cycles.Records.Of(agent);

        Assert.Equal(CycleOutcome.Approved, record.Cycle.Outcome);
        Assert.Equal(
            [(RoleValidation.Rejected, "action: NotOnMarket"), (RoleValidation.Valid, null)],
            record.Of(Role.Strategist).Select(output => (output.Validation, output.Rejection)));
    }

    [Fact]
    public async Task The_plan_is_held_to_the_position_that_the_prompt_of_the_Strategist_states()
    {
        // The agent holds a short, which the data package of its cycle says.
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: PlanAgents.Holding(PlanAgents.Short));
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        // Its stop-loss rests at 61000: lowering it reduces the risk of a short.
        cycles.Models.Says(Role.Strategist, Adjust(60400m));

        await cycles.RunAsync(Quarter).Within();

        var prompt = Assert.Single(cycles.Models.CallsOf(Role.Strategist)).Request.Prompt;

        Assert.Contains("Position: short, quantity 0.01, entry price 60000, stop-loss 61000, take-profit 56000", prompt, StringComparison.Ordinal);
        Assert.Equal(CycleOutcome.Unreviewed, cycles.Records.Of(agent).Cycle.Outcome);
    }

    [Fact]
    public void The_instructions_of_the_Strategist_say_what_a_plan_may_do()
    {
        // A plan is validated against it, so the Strategist is told (NFR-SEC-7).
        var instructions = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Role, nameof(Role.Strategist));

        Assert.True(instructions.Version >= 5, $"The instructions of the Strategist are in version {instructions.Version}.");

        var result = Assert.Single(instructions.Text.Split('\n'), paragraph => paragraph.StartsWith("Result:", StringComparison.Ordinal));

        Assert.Contains("is one that the market of this prompt allows", result, StringComparison.Ordinal);
        Assert.Contains("The agent holds one position at most, and never adds to one", result, StringComparison.Ordinal);
        Assert.Contains("with no position, the action is open_long, hold, or on Futures open_short", result, StringComparison.Ordinal);
        Assert.Contains("with an open position it is hold, close, adjust_protection, or on Futures reverse", result, StringComparison.Ordinal);
        Assert.Contains(
            "A new \"stopLoss\" only reduces risk: at or above the current stop-loss of a long position, and at or below that of a short one.",
            result,
            StringComparison.Ordinal);
        Assert.Contains("\"takeProfit\" may move either way.", result, StringComparison.Ordinal);
    }

    /// <summary>A plan with an action, and with the fields that the action needs.</summary>
    private static string PlanOf(string action) => action switch
    {
        "hold" => TeamAnswers.Hold,
        "close" => TeamAnswers.Close,
        "adjust_protection" => TeamAnswers.Adjust,
        _ => TeamAnswers.Plan(action),
    };

    /// <summary>A plan that adjusts protection: a new stop-loss, a new take-profit, or both.</summary>
    private static string Adjust(decimal? stopLoss, decimal? takeProfit = null) =>
        TeamAnswers.Plan("adjust_protection", entryType: null, stopLoss: stopLoss, takeProfit: takeProfit, riskFraction: null);

    /// <summary>An action as a module names it: <c>open long</c>, <c>adjust protection</c>.</summary>
    private static string InWords(PlanAction action) => action switch
    {
        PlanAction.OpenLong => "open long",
        PlanAction.OpenShort => "open short",
        PlanAction.Hold => "hold",
        PlanAction.Close => "close",
        PlanAction.Reverse => "reverse",
        PlanAction.AdjustProtection => "adjust protection",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action."),
    };
}
