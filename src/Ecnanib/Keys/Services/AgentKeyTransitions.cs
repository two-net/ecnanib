using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Which agents use a key, and the default way of stopping or failing them: one set-based update whose WHERE clause
/// keeps the transition to Running and Paused agents (Appendix E). The agents runtime replaces this registration so
/// that Stop also cancels orders (FR-AGT-06). Reasons are user-facing text and are not logged.
/// </summary>
internal sealed class AgentKeyTransitions : IKeyDependentAgents
{
    /// <summary><c>agents.state_reason</c> is unbounded text; the reasons built here are short, this only guards a long provider message.</summary>
    internal const int MaxReasonLength = 500;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentKeyTransitions> _logger;

    public AgentKeyTransitions(IDbContextFactory<EcnanibDbContext> factory, TimeProvider time, ILogger<AgentKeyTransitions> logger)
    {
        _factory = factory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Live agents in Running or Paused that trade with the key. A paper agent may still carry the reference but does not use it (FR-KEY-09).</summary>
    public static IQueryable<Agent> ActiveOnExchangeKey(EcnanibDbContext db, Guid keyId) =>
        db.Agents.Where(a => a.ExchangeKeyId == keyId && a.Mode == AgentMode.Live && (a.State == AgentState.Running || a.State == AgentState.Paused));

    /// <summary>The user's agents in Running or Paused with an enabled council role on the provider, in either mode: the council runs for paper agents too.</summary>
    public static IQueryable<Agent> ActiveOnProvider(EcnanibDbContext db, Guid userId, AiProvider provider) =>
        db.Agents.Where(a => a.UserId == userId
            && (a.State == AgentState.Running || a.State == AgentState.Paused)
            && a.CouncilAssignments.Any(c => c.Enabled && c.Provider == provider));

    public Task<int> StopAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(agentIds, AgentState.Stopped, reason, cancellationToken);

    public Task<int> FailAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(agentIds, AgentState.Error, reason, cancellationToken);

    private async Task<int> TransitionAsync(IReadOnlyList<Guid> agentIds, AgentState state, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (agentIds.Count == 0)
            return 0;

        var ids = agentIds.Distinct().ToList();
        var text = reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Agents
            .Where(a => ids.Contains(a.Id) && (a.State == AgentState.Running || a.State == AgentState.Paused))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, state)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, text)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("{Agents} agent(s) moved to {State} because of a key change", rows, state);
        return rows;
    }
}
