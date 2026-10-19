using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Services;

/// <summary>Counts a user's agents against the <c>Agents</c> limits (FR-ACC-07, FR-AGT-15).</summary>
internal sealed class FairUseLimits : IFairUseLimits
{
    internal const string AgentsLimit = "agents per user";

    internal const string RunningAgentsLimit = "running agents per user";

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<AgentLimitsOptions> _options;

    public FairUseLimits(IDbContextFactory<EcnanibDbContext> factory, IOptions<AgentLimitsOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    public async Task<FairUseCheck> CheckCreateAgentAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var current = await db.Agents.CountAsync(a => a.UserId == userId, cancellationToken).ConfigureAwait(false);
        return Decide(AgentsLimit, current, _options.Value.MaxAgentsPerUser);
    }

    public async Task<FairUseCheck> CheckStartAgentAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var current = await db.Agents
            .CountAsync(a => a.UserId == userId && (a.State == AgentState.Running || a.State == AgentState.Paused), cancellationToken)
            .ConfigureAwait(false);
        return Decide(RunningAgentsLimit, current, _options.Value.MaxRunningAgentsPerUser);
    }

    /// <summary>One more is allowed while the current count is below the limit.</summary>
    internal static FairUseCheck Decide(string limit, int current, int max) => new(limit, current < max, current, max);
}
