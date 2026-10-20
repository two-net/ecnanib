using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Components.Agents;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>The deprecated-model prompts (FR-SYNC-03).</summary>
public class AgentMessagesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private static readonly CouncilAssignmentView RiskManager = new(CouncilRole.RiskManager, AiProvider.Claude, "claude-3-opus", true, ModelStatus.Deprecated);

    [Fact]
    public void ARowPromptNamesTheNewestModelAsTheReplacement()
    {
        var replacement = new CatalogModel(AiProvider.Claude, "claude-sonnet-4", "Claude Sonnet 4", ModelStatus.Available, Now, Now, null, null, null);

        Assert.Equal("Claude no longer lists claude-3-opus. Choose a replacement; Claude's newest model is Claude Sonnet 4 (claude-sonnet-4).", AgentMessages.DeprecatedModel(RiskManager, replacement));
    }

    [Fact]
    public void ARowPromptUsesTheBareIdWhenTheDisplayNameIsTheId()
    {
        var replacement = new CatalogModel(AiProvider.Gpt, "gpt-5", "gpt-5", ModelStatus.Available, Now, Now, null, null, null);
        var role = new CouncilAssignmentView(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-4", true, ModelStatus.Deprecated);

        Assert.Equal("GPT no longer lists gpt-4. Choose a replacement; GPT's newest model is gpt-5.", AgentMessages.DeprecatedModel(role, replacement));
    }

    [Fact]
    public void ARowPromptWithoutAnyAvailableModelSendsToTheProvidersPage() =>
        Assert.Equal("Claude no longer lists claude-3-opus. No Claude model is available in the catalog: sync it on the providers page or enter a model id by hand.", AgentMessages.DeprecatedModel(RiskManager, null));

    [Fact]
    public void ThePagePromptListsEveryAffectedRole()
    {
        var analyst = new CouncilAssignmentView(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-4", true, ModelStatus.Deprecated);

        Assert.Equal("1 council role uses a model its provider no longer lists: Risk Manager (claude-3-opus on Claude). Choose a replacement in the council.", AgentMessages.DeprecatedModels([RiskManager]));
        Assert.Equal("2 council roles use a model its provider no longer lists: Market Analyst (gpt-4 on GPT), Risk Manager (claude-3-opus on Claude). Choose a replacement in the council.", AgentMessages.DeprecatedModels([analyst, RiskManager]));
    }
}
