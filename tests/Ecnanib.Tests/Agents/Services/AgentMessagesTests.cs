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

    [Theory]
    [InlineData("started", null, "The agent is running and will be evaluated at the next candle close.")]
    [InlineData("stopped", null, "The agent was stopped.")]
    [InlineData("stopped", "2 orders cancelled.", "The agent was stopped. 2 orders cancelled.")]
    [InlineData("disclaimer-accepted", null, "Thank you. The risk disclaimer is accepted.")]
    [InlineData("saved", null, null)]
    [InlineData(null, null, null)]
    public void TheDetailNoticeFollowsTheActionStatus(string? status, string? detail, string? expected) => Assert.Equal(expected, AgentMessages.DetailNotice(status, detail));

    [Theory]
    [InlineData("created", null, "The draft was created. Choose its strategy, council and risk limits, then review and start it.")]
    [InlineData("saved", null, "The changes were saved.")]
    [InlineData("upgraded", "One parameter was reset.", "The agent now runs the newest version of its strategy. One parameter was reset.")]
    [InlineData("council-single", null, "One model now performs all four roles in sequence.")]
    [InlineData("disclaimer-accepted", null, "Thank you. The risk disclaimer is accepted.")]
    [InlineData("started", null, null)]
    public void TheSetupNoticeFollowsTheSaveStatus(string? status, string? detail, string? expected) => Assert.Equal(expected, AgentMessages.SetupNotice(status, detail));

    /// <summary>FR-ACC-08: a refused live start points at the acceptance box the agent page and the review step carry, not at another page.</summary>
    [Fact]
    public void ARefusedLiveStartPointsAtTheBoxOnThePage()
    {
        var text = AgentMessages.ForAction(new AgentActionResult(AgentActionStatus.DisclaimerRequired, "Read and accept the risk disclaimer before starting a live agent."));

        Assert.Equal("Read and accept the risk disclaimer before starting a live agent. The disclaimer and the box to accept it are below.", text);
        Assert.DoesNotContain("settings", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Accept the risk disclaimer first. The disclaimer and the box to accept it are below.", AgentMessages.ForAction(new AgentActionResult(AgentActionStatus.DisclaimerRequired)));
    }

    /// <summary>NFR-USE-03: the stamp is the UTC instant in the invariant culture, whatever offset the value carries.</summary>
    [Fact]
    public void StampsAreUtcToTheMinuteAndNeverForNull()
    {
        Assert.Equal("2026-01-05 00:00 UTC", AgentMessages.Stamp(Now));
        Assert.Equal("2026-01-05 00:00 UTC", AgentMessages.Stamp(new DateTimeOffset(2026, 1, 5, 2, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("never", AgentMessages.Stamp(null));
    }

    [Fact]
    public void WindowsReadInUtcWithAColonWhateverTheHost()
    {
        var recurring = new EntryWindowView(1, EntryWindowKind.Recurring, null, null, DayOfWeek.Friday, new TimeOnly(21, 0), DayOfWeek.Monday, new TimeOnly(1, 0));
        var oneOff = new EntryWindowView(2, EntryWindowKind.OneOff, new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 11, 5, 16, 0, 0, TimeSpan.Zero), null, null, null, null);

        Assert.Equal("Every Friday 21:00 → Monday 01:00 UTC", AgentMessages.Describe(recurring));
        Assert.Equal("2026-11-05 12:00 → 2026-11-05 16:00 UTC", AgentMessages.Describe(oneOff));
    }

    [Fact]
    public void ThePagePromptListsEveryAffectedRole()
    {
        var analyst = new CouncilAssignmentView(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-4", true, ModelStatus.Deprecated);

        Assert.Equal("1 council role uses a model its provider no longer lists: Risk Manager (claude-3-opus on Claude). Choose a replacement in the council.", AgentMessages.DeprecatedModels([RiskManager]));
        Assert.Equal("2 council roles use a model its provider no longer lists: Market Analyst (gpt-4 on GPT), Risk Manager (claude-3-opus on Claude). Choose a replacement in the council.", AgentMessages.DeprecatedModels([analyst, RiskManager]));
    }
}
