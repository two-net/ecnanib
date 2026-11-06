using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>NFR-SEC-04: another user's agent behaves like one that doesn't exist, through every call.</summary>
public sealed class AgentIsolationTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task Another_user_can_neither_see_nor_change_an_agent()
    {
        await using var app = new AgentApp(postgres);
        var owner = await app.UserAsync();
        var stranger = await app.UserAsync();
        var active = await app.ActiveAsync(owner, "Active");
        var archived = await app.CreateAsync(owner.Id, AgentApp.Ready(owner, "Archived"));
        await app.Agents.ArchiveAsync(owner.Id, archived.Id, Cancellation);
        var before = await StateAsync(app, owner.Id);
        var records = (await app.AuditAsync(owner.Id)).Count;

        Assert.Empty(await app.Agents.ListAsync(stranger.Id, includeArchived: true, Cancellation));
        Assert.Null(await app.Agents.FindAsync(stranger.Id, active.Id, Cancellation));
        Assert.Equal(
            AgentSaveOutcome.NotFound,
            (await app.Agents.UpdateAsync(stranger.Id, active.Id, active.Version, AgentApp.Ready(stranger, "Mine now"), Cancellation)).Outcome);
        Assert.Equal(AgentSaveOutcome.NotFound, (await app.Agents.CloneAsync(stranger.Id, active.Id, "Mine now", Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.NotFound, (await app.Agents.ActivateAsync(stranger.Id, active.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotFound, (await app.Agents.PauseAsync(stranger.Id, active.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotFound, (await app.Agents.StopAsync(stranger.Id, active.Id, Cancellation)).Outcome);
        Assert.Equal(KillOutcome.NotFound, (await app.Agents.StopAndCloseAsync(stranger.Id, active.Id, null, Cancellation)).Outcome);
        Assert.Empty((await app.Agents.StopAllAsync(stranger.Id, null, Cancellation)).Agents);
        Assert.Equal(TransitionOutcome.NotFound, (await app.Agents.ArchiveAsync(stranger.Id, active.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotFound, (await app.Agents.DeleteAsync(stranger.Id, archived.Id, Cancellation)).Outcome);
        Assert.Empty(await app.Agents.ListUsingCredentialAsync(stranger.Id, owner.ExchangeKey, Cancellation));
        Assert.Empty(await app.Agents.ListUsingCredentialAsync(stranger.Id, owner.AiKey, Cancellation));

        // A stranger's agent can't point at the owner's key either.
        var borrowed = await app.Agents.CreateAsync(stranger.Id, AgentApp.Ready(stranger) with { ExchangeCredentialId = owner.ExchangeKey }, Cancellation);
        Assert.Equal([AgentField.ExchangeCredential], borrowed.Fields);

        Assert.Equal(before, await StateAsync(app, owner.Id));
        Assert.Equal(records, (await app.AuditAsync(owner.Id)).Count);
    }

    /// <summary>What a change would touch, as values: the version moves with every change.</summary>
    private static async Task<List<(Guid Id, int Version, AgentStatus Status, string Name)>> StateAsync(AgentApp app, Guid userId) =>
        [.. (await app.Agents.ListAsync(userId, includeArchived: true, Cancellation))
            .Select(agent => (agent.Id, agent.Version, agent.Status, agent.Settings.Name))];
}
