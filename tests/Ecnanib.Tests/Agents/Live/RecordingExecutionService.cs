using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>An execution stage that places nothing, manages nothing and counts the decisions it was handed.</summary>
internal sealed class RecordingExecutionService : IExecutionService
{
    private int _calls;

    /// <summary>Decisions handed to <see cref="ExecuteAsync"/>.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public Task<ExecutionResult> ManageAsync(ExecutionRequest request, CancellationToken cancellable, CancellationToken stopping) =>
        Task.FromResult(ExecutionResult.Nothing());

    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, Decision decision, CancellationToken cancellable, CancellationToken stopping)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(ExecutionResult.Nothing("executed"));
    }
}
