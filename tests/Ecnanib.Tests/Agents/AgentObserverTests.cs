using System.Collections.Concurrent;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-05: Agents tells its observers of every change to an agent once it has committed, so an open page reads the
/// agent as it now is. A change that's refused tells nobody, and an observer that throws can't undo or fail the change.
/// </summary>
public sealed class AgentObserverTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task Every_change_is_told_after_it_commits_and_a_refusal_tells_nobody()
    {
        var observer = new AgentRecorder();
        await using var app = new AgentApp(postgres, services: services => services.AddSingleton<IAgentObserver>(observer));
        observer.Read = (userId, agentId) => app.Agents.FindAsync(userId, agentId, CancellationToken.None).GetAwaiter().GetResult()?.Status;
        var user = await app.UserAsync();

        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));

        // A Draft can't be paused, so nothing changed and nobody hears of it.
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.StopAsync(user.Id, agent.Id, Cancellation)).Outcome);
        var clone = (await app.Agents.CloneAsync(user.Id, agent.Id, "Copy", Cancellation)).Agent!;
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.DeleteAsync(user.Id, agent.Id, Cancellation)).Outcome);

        // Each saw the agent as the change left it, because it had committed.
        Assert.Equal(
            [
                (agent.Id, (AgentStatus?)AgentStatus.Draft),
                (agent.Id, AgentStatus.Active),
                (agent.Id, AgentStatus.Paused),
                (agent.Id, AgentStatus.Stopped),
                (clone.Id, AgentStatus.Draft),
                (agent.Id, AgentStatus.Archived),
                (agent.Id, null),
            ],
            observer.Heard.Select(heard => (heard.AgentId, heard.Status)));
        Assert.All(observer.Heard, heard => Assert.Equal(user.Id, heard.UserId));
    }

    [Fact]
    public async Task An_observer_that_throws_fails_nothing_and_the_others_still_hear()
    {
        var observer = new AgentRecorder();
        await using var app = new AgentApp(
            postgres,
            services: services => services.AddSingleton<IAgentObserver>(new Throwing()).AddSingleton<IAgentObserver>(observer));
        var user = await app.UserAsync();

        var agent = await app.ActiveAsync(user);
        var paused = await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(TransitionOutcome.Done, paused.Outcome);
        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal(3, observer.Heard.Count);
    }

    private sealed class AgentRecorder : IAgentObserver
    {
        private readonly ConcurrentQueue<(Guid UserId, Guid AgentId, AgentStatus? Status)> _heard = new();

        public Func<Guid, Guid, AgentStatus?> Read { get; set; } = (_, _) => null;

        public IReadOnlyList<(Guid UserId, Guid AgentId, AgentStatus? Status)> Heard => [.. _heard];

        public void OnChanged(Guid userId, Guid agentId) => _heard.Enqueue((userId, agentId, Read(userId, agentId)));
    }

    private sealed class Throwing : IAgentObserver
    {
        public void OnChanged(Guid userId, Guid agentId) => throw new InvalidOperationException("An observer that broke.");
    }
}
