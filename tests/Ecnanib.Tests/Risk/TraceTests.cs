using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-10: every evaluation records each rule's inputs, result and reason. FR-RISK-01: the same inputs always
/// give the same evaluation, whatever the server's culture.
/// </summary>
public sealed class TraceTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<string> Cases() => [.. Scenarios().Keys];

    private static Dictionary<string, (OrderIntent Intent, RiskInputs Inputs)> Scenarios()
    {
        var spotHolding = Spot() with { Position = new OpenPosition(PositionSide.Long, 2m, 95m, Stop: 93m), BaseHeld = 2m };
        var futuresHolding = Futures() with { Position = new OpenPosition(PositionSide.Long, 0.5m, 990m, Stop: 980m) };

        return new()
        {
            ["spot buy"] = (SpotBuy(), Spot()),
            ["spot add"] = (SpotBuy(), spotHolding),
            ["spot sell"] = (new OrderIntent { Action = IntentAction.Sell, CloseFraction = 0.5m }, spotHolding),
            ["futures long"] = (FuturesLong(), Futures()),
            ["futures short limit"] = (FuturesShort() with { EntryType = EntryType.Limit, LimitPrice = 1001m }, Futures()),
            ["futures close"] = (new OrderIntent { Action = IntentAction.Close }, futuresHolding),
            ["adjust stop"] = (new OrderIntent { Action = IntentAction.AdjustStop, StopPrice = 990m }, futuresHolding),
            ["adjust target"] = (new OrderIntent { Action = IntentAction.AdjustTarget, TargetPrice = 1050m }, futuresHolding),
            ["cancel"] = (new OrderIntent { Action = IntentAction.CancelPending }, Futures() with { HasPendingEntry = true }),
            ["failing entry"] = (SpotBuy() with { StopPrice = 101m }, Spot() with { Record = TradingRecord.None with { RealizedToday = -50m } }),
            ["invalid settings"] = (SpotBuy(), Spot() with { Settings = RiskSettings.Default with { MaxLeverage = 0 } }),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_rule_appears_once_in_order(string scenario)
    {
        var (intent, inputs) = Scenarios()[scenario];

        var evaluation = Evaluate(intent, inputs);

        Assert.Equal(Enum.GetValues<RiskRule>(), evaluation.Rules.Select(rule => rule.Rule));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_failure_has_a_reason_and_nothing_else_does(string scenario)
    {
        var (intent, inputs) = Scenarios()[scenario];

        var evaluation = Evaluate(intent, inputs);

        Assert.All(evaluation.Rules, rule => Assert.Equal(rule.Outcome == RuleOutcome.Fail, rule.Reason is not null));
        Assert.Equal(evaluation.Failures.Any(), !evaluation.Passed);
        Assert.Equal(evaluation.Passed, evaluation.Order is not null);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_rule_that_ran_says_what_it_looked_at(string scenario)
    {
        var (intent, inputs) = Scenarios()[scenario];

        var evaluation = Evaluate(intent, inputs);

        Assert.All(
            evaluation.Rules,
            rule => Assert.Equal(rule.Outcome is RuleOutcome.Pass or RuleOutcome.Fail, rule.Inputs.Count > 0));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_same_inputs_give_the_same_evaluation_in_any_culture(string scenario)
    {
        var (intent, inputs) = Scenarios()[scenario];
        var original = CultureInfo.CurrentCulture;

        string first;
        string second;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            first = JsonSerializer.Serialize(Evaluate(intent, inputs), Json);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            second = JsonSerializer.Serialize(Evaluate(intent, inputs), Json);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public void Inputs_are_written_in_the_invariant_culture_without_trailing_zeros()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var size = Evaluate(SpotBuy() with { RiskPercent = 0.50m }, Spot()).Rule(RiskRule.Size);

            Assert.Equal("0.5", size.Input("RiskPercent"));
            Assert.Equal("2.5", size.Input("Quantity"));
            Assert.Equal("Risk", size.Input("BindingLimit"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_evaluation_carries_the_intent_caps_and_effective_settings()
    {
        var intent = FuturesLong();
        var inputs = Futures() with { Settings = RiskSettings.Default with { MaxLeverage = 50 } };

        var evaluation = Evaluate(intent, inputs);

        Assert.Same(intent, evaluation.Intent);
        Assert.Equal(RiskCaps.Default, evaluation.Caps);
        Assert.Equal(5, evaluation.Effective.MaxLeverage);
        Assert.Equal("50", evaluation.Rule(RiskRule.Settings).Input("MaxLeverage"));
        Assert.Equal("5", evaluation.Rule(RiskRule.Settings).Input("MaxLeverageEffective"));
    }
}
