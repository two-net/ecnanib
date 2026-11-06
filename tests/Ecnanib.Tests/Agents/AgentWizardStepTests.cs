using System.Reflection;
using Ecnanib.Components.Agents;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-02: every setting belongs to one step of the wizard, so a step can carry the others unseen, and every refusal
/// leads to the step that can fix it.
/// </summary>
public sealed class AgentWizardStepTests
{
    /// <summary>A setting the form holds but doesn't carry would be lost between steps.</summary>
    [Fact]
    public void The_form_carries_every_setting_once()
    {
        var properties = typeof(AgentForm)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.Name != nameof(AgentForm.Version))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);

        var carried = new AgentForm().Values().Select(value => value.Name).ToList();

        Assert.Equal(properties, carried.Order(StringComparer.Ordinal));
        Assert.Equal(carried.Count, carried.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_setting_is_on_a_step_and_every_step_has_some()
    {
        var steps = new AgentForm().Values().Select(value => AgentWizard.StepOf(value.Name)).ToHashSet();

        Assert.Equal(Enum.GetValues<WizardStep>(), steps.Order());
        Assert.All(Enum.GetValues<AgentField>(), field => Assert.True(Enum.IsDefined(AgentWizard.StepOf(field))));
    }

    [Theory]
    [InlineData(nameof(AgentForm.Market), WizardStep.Market)]
    [InlineData(nameof(AgentForm.ExchangeKey), WizardStep.Market)]
    [InlineData(nameof(AgentForm.Symbol), WizardStep.Pair)]
    [InlineData(nameof(AgentForm.Timeframe), WizardStep.Pair)]
    [InlineData(nameof(AgentForm.RiskCritic), WizardStep.Roles)]
    [InlineData(nameof(AgentForm.ReviewerFallback), WizardStep.Roles)]
    [InlineData(nameof(AgentForm.CapitalAllocation), WizardStep.Risk)]
    [InlineData(nameof(AgentForm.FundingFilterPercent), WizardStep.Risk)]
    [InlineData(nameof(AgentForm.AutoExecuteRiskReducing), WizardStep.Execution)]
    [InlineData(nameof(AgentForm.MonthlyTokens), WizardStep.Execution)]
    [InlineData(nameof(AgentForm.CustomInstructions), WizardStep.Review)]
    public void A_setting_is_on_the_step_FR_UI_02_gives_it(string name, WizardStep step)
    {
        Assert.Equal(step, AgentWizard.StepOf(name));
    }

    /// <summary>Spot has no leverage, liquidation or funding, so the risk step carries those as they are.</summary>
    [Fact]
    public void The_risk_step_shows_the_Futures_settings_only_on_Futures()
    {
        Assert.False(AgentWizard.Shows(WizardStep.Risk, Market.Spot, nameof(RiskSetting.MaxLeverage)));
        Assert.True(AgentWizard.Shows(WizardStep.Risk, Market.Futures, nameof(RiskSetting.MaxLeverage)));
        Assert.True(AgentWizard.Shows(WizardStep.Risk, Market.Spot, nameof(RiskSetting.MaxRiskPerTradePercent)));
        Assert.False(AgentWizard.Shows(WizardStep.Pair, Market.Futures, nameof(RiskSetting.MaxLeverage)));
    }

    [Theory]
    [InlineData("Review", WizardStep.Review)]
    [InlineData("review", null)]
    [InlineData("2", null)]
    [InlineData(null, null)]
    public void Only_a_steps_exact_name_is_a_step(string? posted, WizardStep? step)
    {
        Assert.Equal(step, AgentWizard.Parse(posted));
    }
}
