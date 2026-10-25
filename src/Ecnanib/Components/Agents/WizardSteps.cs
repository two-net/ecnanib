namespace Ecnanib.Components.Agents;

/// <summary>The five steps of the agent create/edit wizard (FR-UI-01), in order. Public because it is a component parameter.</summary>
public enum WizardStep
{
    Basics = 1,
    Strategy = 2,
    Council = 3,
    Risk = 4,
    Review = 5,
}

/// <summary>Route slugs, titles and the order of the wizard steps; lenient parsing, so a hand-edited slug lands on the first step.</summary>
internal static class WizardSteps
{
    public static readonly IReadOnlyList<WizardStep> Ordered = [WizardStep.Basics, WizardStep.Strategy, WizardStep.Council, WizardStep.Risk, WizardStep.Review];

    public static WizardStep Parse(string? slug)
    {
        foreach (var step in Ordered)
        {
            if (string.Equals(Slug(step), slug?.Trim(), StringComparison.OrdinalIgnoreCase))
                return step;
        }

        return WizardStep.Basics;
    }

    public static string Slug(WizardStep step) => step.ToString().ToLowerInvariant();

    public static string Title(WizardStep step) => step switch
    {
        WizardStep.Basics => "Basics",
        WizardStep.Strategy => "Strategy",
        WizardStep.Council => "Council",
        WizardStep.Risk => "Risk and windows",
        WizardStep.Review => "Review and start",
        _ => step.ToString(),
    };

    public static WizardStep? Next(WizardStep step) => step == WizardStep.Review ? null : (WizardStep)((int)step + 1);

    public static WizardStep? Previous(WizardStep step) => step == WizardStep.Basics ? null : (WizardStep)((int)step - 1);

    public static string Href(Guid agentId, WizardStep step) => $"/agents/{agentId}/setup/{Slug(step)}";
}
