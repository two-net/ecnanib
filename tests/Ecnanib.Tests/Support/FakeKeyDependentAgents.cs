using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Records the stop and fail requests the keys module makes instead of touching agents.</summary>
internal sealed class FakeKeyDependentAgents : IKeyDependentAgents
{
    public List<(AgentState State, IReadOnlyList<Guid> Ids, string Reason)> Calls { get; } = [];

    public Task<int> StopAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken) => Record(AgentState.Stopped, agentIds, reason);

    public Task<int> FailAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken) => Record(AgentState.Error, agentIds, reason);

    private Task<int> Record(AgentState state, IReadOnlyList<Guid> ids, string reason)
    {
        lock (Calls)
            Calls.Add((state, ids, reason));
        return Task.FromResult(ids.Count);
    }
}
