using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Agents;

/// <summary>The agent wizard's steps, in order (FR-UI-02). The page posts a step's name, so a value is never renamed.</summary>
public enum WizardStep
{
    Market,
    Pair,
    Roles,
    Risk,
    Execution,
    Review,
}

/// <summary>
/// What the wizard posts besides the settings: the step it showed, the button pressed, and whether the user has been
/// to Review, after which each step offers to go straight back there. Strings, so that a value nobody offered is
/// ignored rather than a failure to bind.
/// </summary>
public sealed class WizardNav
{
    public string? Step { get; set; }

    /// <summary>One of the actions in <see cref="AgentWizard"/>, or the name of a step to go back to.</summary>
    public string? Go { get; set; }

    /// <summary>"true" once the user has reached Review.</summary>
    public string? Reviewed { get; set; }
}

/// <summary>
/// Which step each setting is on, so the page shows a step's own and carries the rest in hidden fields, and so a
/// refusal takes the user to the step that can fix it. See the agent wizard in docs/architecture.md.
/// </summary>
public static class AgentWizard
{
    public const string Next = "Next";
    public const string Back = "Back";

    /// <summary>Checks every step, and goes to Review, or to the first step that needs fixing.</summary>
    public const string ToReview = "ToReview";

    /// <summary>Creates the agent and activates it.</summary>
    public const string Start = "Start";

    /// <summary>Creates the agent as a Draft.</summary>
    public const string Draft = "Draft";

    public static IReadOnlyList<WizardStep> Steps { get; } = Enum.GetValues<WizardStep>();

    private const string FallbackSuffix = "Fallback";

    /// <summary>The settings that mean something only on Futures. On Spot the step carries them unseen, as they are.</summary>
    public static IReadOnlyList<RiskSetting> FuturesOnly { get; } =
        [RiskSetting.MaxLeverage, RiskSetting.LiquidationBufferMultiple, RiskSetting.FundingFilterPercent];

    /// <summary>The settings the risk step shows first. The rest are in a panel, because the defaults suit most agents.</summary>
    public static IReadOnlyList<RiskSetting> Main { get; } =
        [RiskSetting.MaxRiskPerTradePercent, RiskSetting.DailyLossLimitPercent, RiskSetting.WeeklyLossLimitPercent, RiskSetting.MaxLeverage];

    /// <summary>The step a posted setting is on, by its name without the <c>Input.</c> prefix.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A name <see cref="AgentForm.Values"/> doesn't give.</exception>
    public static WizardStep StepOf(string name)
    {
        var role = name.EndsWith(FallbackSuffix, StringComparison.Ordinal) ? name[..^FallbackSuffix.Length] : name;

        if (Enum.GetNames<AiRole>().Contains(role, StringComparer.Ordinal))
        {
            return WizardStep.Roles;
        }

        if (Enum.GetNames<RiskSetting>().Contains(name, StringComparer.Ordinal))
        {
            return WizardStep.Risk;
        }

        return name switch
        {
            nameof(AgentForm.Market) or nameof(AgentForm.ExchangeKey) => WizardStep.Market,
            nameof(AgentForm.Symbol) or nameof(AgentForm.Timeframe) => WizardStep.Pair,
            nameof(AgentForm.CapitalAllocation) => WizardStep.Risk,
            nameof(AgentForm.ExecutionMode) or nameof(AgentForm.AutoExecuteRiskReducing) or nameof(AgentForm.DailyBudgetUsd)
                or nameof(AgentForm.MonthlyBudgetUsd) or nameof(AgentForm.DailyTokens) or nameof(AgentForm.MonthlyTokens)
                or nameof(AgentForm.ExperienceEnabled) or nameof(AgentForm.ReflectionsInPrompt) => WizardStep.Execution,
            nameof(AgentForm.Name) or nameof(AgentForm.Description) or nameof(AgentForm.CustomInstructions) => WizardStep.Review,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Give the setting a step."),
        };
    }

    /// <summary>The step that can fix a setting the contract refused.</summary>
    public static WizardStep StepOf(AgentField field) => field switch
    {
        AgentField.Market or AgentField.Environment or AgentField.ExchangeCredential => WizardStep.Market,
        AgentField.Symbol or AgentField.Timeframe => WizardStep.Pair,
        AgentField.Roles => WizardStep.Roles,
        AgentField.CapitalAllocation or AgentField.Risk => WizardStep.Risk,
        AgentField.ExecutionMode or AgentField.Budget or AgentField.ReflectionsInPrompt => WizardStep.Execution,
        AgentField.Name or AgentField.Description or AgentField.CustomInstructions => WizardStep.Review,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Give the field a step."),
    };

    /// <summary>Whether <paramref name="step"/> shows the setting as a field of its own, rather than carrying it hidden.</summary>
    public static bool Shows(WizardStep step, Market market, string name) =>
        StepOf(name) == step && !(market == Market.Spot && FuturesOnly.Any(setting => setting.ToString() == name));

    /// <summary>A posted step, or null for one nobody offered.</summary>
    public static WizardStep? Parse(string? step) =>
        step is not null && Enum.GetNames<WizardStep>().Contains(step, StringComparer.Ordinal) ? Enum.Parse<WizardStep>(step) : null;
}
