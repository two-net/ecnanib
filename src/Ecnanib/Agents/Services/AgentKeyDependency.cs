using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Agents.Services;

/// <summary>
/// The runtime's answer to key changes (FR-KEY-06, FR-KEY-07), replacing the keys module's row-only default: a key
/// being deleted stops its agents with their orders cancelled and their positions flagged unmanaged (nothing can be
/// closed without the key), and an invalid key moves its agents to Error and halts their evaluation. Each agent is
/// bounded so one slow venue never delays the keys module's other checks.
/// </summary>
internal sealed class AgentKeyDependency : IKeyDependentAgents
{
    internal static readonly TimeSpan PerAgentTimeout = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IAgentRuntime _runtime;
    private readonly IOrderVenues _venues;
    private readonly IAgentEvents _events;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentKeyDependency> _logger;

    public AgentKeyDependency(
        IDbContextFactory<EcnanibDbContext> factory,
        IAgentRuntime runtime,
        IOrderVenues venues,
        IAgentEvents events,
        TimeProvider time,
        ILogger<AgentKeyDependency> logger)
    {
        _factory = factory;
        _runtime = runtime;
        _venues = venues;
        _events = events;
        _time = time;
        _logger = logger;
    }

    public async Task<int> StopAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var stopped = 0;
        foreach (var agentId in agentIds.Distinct())
        {
            try
            {
                using var timeout = new CancellationTokenSource(PerAgentTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                if (await StopOneAsync(agentId, reason, linked.Token).ConfigureAwait(false))
                    stopped++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent {AgentId} could not be stopped for a key change", agentId);
            }
        }

        _logger.LogInformation("{Agents} agent(s) stopped because of a key change", stopped);
        return stopped;
    }

    public async Task<int> FailAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var failed = 0;
        foreach (var agentId in agentIds.Distinct())
        {
            _runtime.CancelEvaluation(agentId);
            using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (await _runtime.FailAsync(agentId, reason, cancellationToken).ConfigureAwait(false))
                failed++;
        }

        return failed;
    }

    private async Task<bool> StopOneAsync(Guid agentId, string reason, CancellationToken cancellationToken)
    {
        _runtime.CancelEvaluation(agentId);
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null || !AgentStateMachine.IsEvaluated(agent.State))
            return false;

        try
        {
            await _venues.For(agent.Mode).CancelOpenOrdersAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The key is about to go; the transition stands and the positions are flagged below.
            _logger.LogWarning(ex, "Open orders of agent {AgentId} could not be cancelled while stopping it for a key change", agentId);
        }

        await db.Trades.Where(t => t.AgentId == agentId && t.ClosedAt == null && !t.IsUnmanaged)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsUnmanaged, true), cancellationToken).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var text = reason.Length <= EvaluationPipeline.MaxDetailLength ? reason : reason[..EvaluationPipeline.MaxDetailLength];
        var rows = await db.Agents
            .Where(a => a.Id == agentId && (a.State == AgentState.Running || a.State == AgentState.Paused))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Stopped)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, text)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        _runtime.Untrack(agentId);
        if (rows == 0)
            return false;

        db.AgentChanges.Add(ChangeSet.State(agentId, now, agent.State, AgentState.Stopped, text));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent {AgentId} stopped because of a key change", agentId);
        _events.Publish(new AgentStateChanged(agentId, agent.UserId, now, AgentState.Stopped, text));
        return true;
    }
}
