using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Whether a user may create one more agent.</summary>
public enum AgentCreation
{
    Allowed,

    /// <summary>The user has not verified their email address yet (FR-ACC-1).</summary>
    EmailNotVerified,

    /// <summary>The user already has as many agents as the fair-use limit allows (FR-ACC-7).</summary>
    AgentLimitReached,
}

/// <summary>
/// What a user may do under the fair-use limits. The limits come from the configuration file and
/// are the same for every user: no account carries a limit of its own (FR-ACC-7).
/// </summary>
public interface IFairUse
{
    /// <summary>The most agents one user may have.</summary>
    int MaxAgentsPerUser { get; }

    /// <summary>
    /// Says whether a user may create one more agent. Whatever creates agents asks this first.
    /// </summary>
    /// <param name="userId">The user. One that does not exist is not verified.</param>
    /// <param name="agentsOwned">How many agents the user has now.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<AgentCreation> MayCreateAgentAsync(Guid userId, int agentsOwned, CancellationToken cancellationToken);
}

internal sealed class FairUse(LimitsSettings limits, IDbContextFactory<EcnanibDbContext> contexts) : IFairUse
{
    public int MaxAgentsPerUser => limits.MaxAgentsPerUser;

    public async Task<AgentCreation> MayCreateAgentAsync(Guid userId, int agentsOwned, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var verified = await context.Set<User>()
            .AnyAsync(user => user.Id == userId && user.EmailVerifiedAt != null, cancellationToken);

        if (!verified)
        {
            return AgentCreation.EmailNotVerified;
        }

        return agentsOwned >= MaxAgentsPerUser ? AgentCreation.AgentLimitReached : AgentCreation.Allowed;
    }
}
