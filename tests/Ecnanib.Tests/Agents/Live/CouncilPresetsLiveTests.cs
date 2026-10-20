using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>The council presets of FR-AI-04 (defaults by strength, single-model mode) go through the council save, so the state rule and the change history apply.</summary>
[Trait("Category", "Live")]
public class CouncilPresetsLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public CouncilPresetsLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DefaultsByStrengthAndSingleModelModeAreSavedAndRecorded()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "presets@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", model: "x");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        // No catalog yet: the roles already sit on their default providers, so they keep their model.
        var kept = await agents.ResetCouncilToDefaultsAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, kept.Status);
        Assert.All((await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).CouncilAssignments, c => Assert.Equal("x", c.Model));

        // No catalog and another provider: nothing to fall back on, so the user is told per role.
        Assert.Equal(AgentSaveStatus.Succeeded, (await agents.UseSingleModelAsync(user.Id, agent.Id, AiProvider.Gemini, "gemini-x", Ct)).Status);
        var refused = await agents.ResetCouncilToDefaultsAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, refused.Status);
        Assert.Equal(["council.marketAnalyst.model", "council.sentimentFlow.model", "council.riskManager.model"], refused.Errors!.Select(e => e.Field));
        Assert.Contains("add a GPT key", refused.Errors[0].Message);

        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);

        var defaults = await agents.ResetCouncilToDefaultsAsync(user.Id, agent.Id, Ct);

        Assert.Equal(AgentSaveStatus.Succeeded, defaults.Status);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        foreach (var role in CouncilRoles.Order)
        {
            var assignment = row.CouncilAssignments.Single(c => c.Role == role);
            Assert.Equal(CouncilRoles.DefaultProvider(role), assignment.Provider);
            Assert.Equal(CouncilRoles.DefaultProvider(role).ToString().ToLowerInvariant() + "-large", assignment.Model);
            Assert.True(assignment.Enabled);
        }

        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "council.riskManager.model" && c.NewValue!.Value.GetString() == "claude-large");

        var single = await agents.UseSingleModelAsync(user.Id, agent.Id, AiProvider.Gemini, " gemini-small ", Ct);

        Assert.Equal(AgentSaveStatus.Succeeded, single.Status);
        row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.All(row.CouncilAssignments, c =>
        {
            Assert.Equal(AiProvider.Gemini, c.Provider);
            Assert.Equal("gemini-small", c.Model);
            Assert.True(c.Enabled);
        });
        changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "council.marketAnalyst.provider" && c.NewValue!.Value.GetString() == "Gemini");

        var blank = await agents.UseSingleModelAsync(user.Id, agent.Id, AiProvider.Claude, "", Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, blank.Status);
        Assert.Equal(4, blank.Errors!.Count);

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Running);
        var running = await agents.ResetCouncilToDefaultsAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.InvalidState, running.Status);
        Assert.Equal(AgentSaveStatus.NotFound, (await agents.UseSingleModelAsync(Guid.NewGuid(), agent.Id, AiProvider.Claude, "m", Ct)).Status);
        Assert.Equal(AgentSaveStatus.NotFound, (await agents.ResetCouncilToDefaultsAsync(Guid.NewGuid(), agent.Id, Ct)).Status);

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Stopped);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }
}
