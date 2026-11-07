using Ecnanib.Components.Traces;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>EXT-UI-02: everything the decision trace viewer names has text of its own.</summary>
public sealed class TraceTextTests
{
    [Fact]
    public void Every_value_has_text_of_its_own()
    {
        Distinct(Enum.GetValues<RiskRule>().Select(TraceText.RuleOf));
        Distinct(Enum.GetValues<RuleOutcome>().Select(TraceText.RuleOutcomeOf));
        Distinct(Enum.GetValues<RiskVerdict>().Select(TraceText.VerdictOf));
        Distinct(Enum.GetValues<LossLimitKind>().Select(TraceText.LossLimitOf));
        Distinct(Enum.GetValues<IntentOrigin>().Select(TraceText.OriginOf));
        Distinct(Enum.GetValues<EntryType>().Select(TraceText.EntryTypeOf));
        Distinct(Enum.GetValues<SizedOrderKind>().Select(TraceText.SizedKindOf));
        Distinct(Enum.GetValues<OrderSide>().Select(TraceText.SideOf));
        Distinct(Enum.GetValues<TradeAction>().Select(TraceText.ProposalOf));
        Distinct(Enum.GetValues<BudgetLimit>().Select(TraceText.BudgetLimitOf));
        Distinct(Enum.GetValues<CycleFailure>().Select(TraceText.FailureOf));
        Distinct(Enum.GetValues<RoleState>().Select(TraceText.RoleStateOf));
        Distinct(Enum.GetValues<CouncilCallStatus>().Select(TraceText.CallStatusOf));
        Distinct(Enum.GetValues<CouncilFailure>().Select(TraceText.CallFailureOf));
        Distinct(Enum.GetValues<PromptLayer>().Select(TraceText.LayerOf));
        Distinct(Enum.GetValues<FuturesSetupOutcome>().Select(TraceText.FuturesSetupOf));
        Distinct(Enum.GetValues<OrderLeg>().Select(TraceText.LegOf));
        Distinct(Enum.GetValues<OrderKind>().Select(TraceText.OrderKindOf));
        Distinct(Enum.GetValues<OrderStatus>().Select(TraceText.OrderStatusOf));
    }

    /// <summary>Every risk setting reads back from the settings a rule used.</summary>
    [Fact]
    public void Every_setting_has_its_value()
    {
        var settings = new RiskSettings();

        Assert.All(Enum.GetValues<RiskSetting>().Where(setting => setting != RiskSetting.MaxPositionNotional), setting =>
            Assert.NotNull(TraceText.SettingOf(settings, setting)));
        Assert.Equal(settings.MaxLeverage, TraceText.SettingOf(settings, RiskSetting.MaxLeverage));
    }

    /// <summary>One call can cost a fraction of a cent, which shows rather than rounding to nothing.</summary>
    [Theory]
    [InlineData("0.0000042", "0.000004 US dollars")]
    [InlineData("0.5", "0.5 US dollars")]
    [InlineData("12.345", "12.35 US dollars")]
    public void A_cost_shows_down_to_a_millionth(string usd, string expected) =>
        Assert.Equal(expected, TraceText.CostOf(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_model_without_a_price_says_so() => Assert.Equal("No price", TraceText.CostOf(null));

    private static void Distinct(IEnumerable<string> texts)
    {
        var list = texts.ToList();

        Assert.All(list, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }
}
