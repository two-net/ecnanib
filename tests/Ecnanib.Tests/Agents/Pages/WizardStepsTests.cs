using Ecnanib.Components.Agents;

namespace Ecnanib.Tests.Agents.Pages;

/// <summary>The wizard's steps (FR-UI-01): slugs, order and lenient parsing.</summary>
public class WizardStepsTests
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(null, WizardStep.Basics)]
    [InlineData("", WizardStep.Basics)]
    [InlineData("bogus", WizardStep.Basics)]
    [InlineData("basics", WizardStep.Basics)]
    [InlineData("strategy", WizardStep.Strategy)]
    [InlineData("Council", WizardStep.Council)]
    [InlineData(" RISK ", WizardStep.Risk)]
    [InlineData("review", WizardStep.Review)]
    public void SlugsParseLenientlyAndUnknownOnesLandOnTheFirstStep(string? slug, WizardStep expected) => Assert.Equal(expected, WizardSteps.Parse(slug));

    [Fact]
    public void EveryStepRoundTripsThroughItsSlugInOrder()
    {
        Assert.Equal([WizardStep.Basics, WizardStep.Strategy, WizardStep.Council, WizardStep.Risk, WizardStep.Review], WizardSteps.Ordered);
        Assert.All(WizardSteps.Ordered, step => Assert.Equal(step, WizardSteps.Parse(WizardSteps.Slug(step))));
        Assert.Equal(["basics", "strategy", "council", "risk", "review"], WizardSteps.Ordered.Select(WizardSteps.Slug));
        Assert.Equal(["Basics", "Strategy", "Council", "Risk and windows", "Review and start"], WizardSteps.Ordered.Select(WizardSteps.Title));
    }

    [Fact]
    public void NextAndPreviousStopAtTheEnds()
    {
        Assert.Equal(WizardStep.Strategy, WizardSteps.Next(WizardStep.Basics));
        Assert.Equal(WizardStep.Review, WizardSteps.Next(WizardStep.Risk));
        Assert.Null(WizardSteps.Next(WizardStep.Review));
        Assert.Equal(WizardStep.Risk, WizardSteps.Previous(WizardStep.Review));
        Assert.Null(WizardSteps.Previous(WizardStep.Basics));
    }

    [Fact]
    public void TheHrefNamesTheAgentAndTheSlug() => Assert.Equal("/agents/00000000-0000-0000-0000-000000000001/setup/council", WizardSteps.Href(Id, WizardStep.Council));
}
