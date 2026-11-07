using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Pauses Active agents whose credentials no longer let them trade (FR-KEY-10, FR-KEY-11): the exchange key
/// was deleted, isn't valid, or can't trade on the agent's market, or no valid AI key is left for a required
/// role's provider. Their positions and protective orders stay on the exchange (FR-AGT-06). It reads only
/// what Credentials says, so a provider's outage, which leaves a key's status alone, never pauses anything.
/// A model that's deprecated or retired is <see cref="ModelLifecycle"/>'s (FR-CAT-04). See Agents in
/// docs/architecture.md.
/// </summary>
internal sealed class AgentPrerequisites(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ICredentials credentials,
    IAuditTrail audit,
    TimeProvider time,
    AgentObservers observers,
    ILogger<AgentPrerequisites> logger)
{
    /// <summary>The one-off run that follows a change to a credential.</summary>
    public const string CredentialJobName = "agent-credential-check";

    public static readonly TimeSpan CredentialJobTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Pauses the user's Active agents that fail, one at a time. Returns how many paused.</summary>
    public async Task<int> PauseFailingAsync(Guid userId, CancellationToken cancellationToken)
    {
        List<Agent> active;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            active = await ActiveAgents(context).Where(agent => agent.UserId == userId).ToListAsync(cancellationToken);
        }

        if (active.Count == 0)
        {
            return 0;
        }

        var keys = await credentials.ListAsync(userId, cancellationToken);
        var paused = 0;

        foreach (var agent in active)
        {
            // One agent's failure never stops the others (NFR-REL-05).
            try
            {
                if (ReasonToPause(agent, keys) is not null && await PauseAsync(agent.Id, userId, keys, cancellationToken))
                {
                    paused++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Checking whether agent {AgentId} of user {UserId} can still trade failed", agent.Id, userId);
            }
        }

        return paused;
    }

    /// <summary>
    /// Every user's Active agents, user by user. It's the recovery path for a change to a credential whose
    /// event was lost with the process (see Inversions in docs/architecture.md).
    /// </summary>
    public async Task PauseFailingForEveryoneAsync(JobContext job, CancellationToken cancellationToken)
    {
        List<Guid> users;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            users = await ActiveAgents(context).Select(agent => agent.UserId).Distinct().ToListAsync(cancellationToken);
        }

        var paused = 0;
        var failed = 0;

        foreach (var userId in users)
        {
            job.Stopping.ThrowIfCancellationRequested();

            try
            {
                paused += await PauseFailingAsync(userId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(exception, "Checking the agents of user {UserId} failed", userId);
            }
        }

        job.Details = $"Users {users.Count}, paused {paused}, failed {failed}";
    }

    /// <summary>Why the agent can't keep trading, or null when its credentials still allow it.</summary>
    public static AgentStatusReason? ReasonToPause(Agent agent, IReadOnlyList<CredentialRecord> keys)
    {
        var exchange = keys.FirstOrDefault(key => key.Id == agent.ExchangeCredentialId && key.Kind == CredentialKind.Exchange);

        if (exchange is null)
        {
            return AgentStatusReason.CredentialDeleted;
        }

        if (exchange.Status != CredentialStatus.Valid)
        {
            return AgentStatusReason.CredentialInvalid;
        }

        if (!ActivationChecks.EnabledFor(exchange, agent.Market))
        {
            return AgentStatusReason.MarketNotEnabled;
        }

        var providers = keys
            .Where(key => key is { Kind: CredentialKind.Ai, Status: CredentialStatus.Valid })
            .Select(key => key.Provider)
            .ToHashSet(StringComparer.Ordinal);

        return agent.Roles.Any(role => AgentRoles.IsRequired(role.Role) && !providers.Contains(role.Provider))
            ? AgentStatusReason.AiKeyUnavailable
            : null;
    }

    /// <summary>
    /// Pauses one agent under the user's lock, so it can't cross an activation: whichever goes second sees the
    /// other's result. The reason is worked out again from the agent as it is now.
    /// </summary>
    private async Task<bool> PauseAsync(Guid agentId, Guid userId, IReadOnlyList<CredentialRecord> keys, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await ActiveAgents(context).SingleOrDefaultAsync(row => row.Id == agentId && row.UserId == userId, cancellationToken);

        if (agent is null || ReasonToPause(agent, keys) is not { } reason)
        {
            return false;
        }

        if (!await AgentPause.TryAsync(context, audit, userId, agentId, agent.Version, reason, time.GetUtcNow(), cancellationToken))
        {
            return false;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        observers.Changed(userId, agentId);

        logger.LogInformation("Paused agent {AgentId} of user {UserId}: {Reason}", agentId, userId, reason);

        return true;
    }

    private static IQueryable<Agent> ActiveAgents(EcnanibDbContext context) =>
        context.Set<Agent>().AsNoTracking().Include(agent => agent.Roles).Where(agent => agent.Status == AgentStatus.Active);
}

/// <summary>
/// Checks the user's agents after a change to one of their credentials that could stop them trading:
/// deleted, no longer valid, or a Binance key checked again, which may have lost a market. It runs as a job,
/// so the request that changed the key doesn't wait. A job is lost with the process, and
/// <see cref="AgentPrerequisitesJob"/> recovers it.
/// </summary>
internal sealed class AgentCredentialObserver(AgentPrerequisites prerequisites, IJobRunner jobs) : ICredentialObserver
{
    public void OnChanged(CredentialChange change)
    {
        if (!MayStopAgents(change))
        {
            return;
        }

        // RunAsync never throws for the work's failure, so the task can be left to run.
        _ = jobs.RunAsync(
            AgentPrerequisites.CredentialJobName,
            AgentPrerequisites.CredentialJobTimeout,
            async (context, token) => context.Details = $"Paused {await prerequisites.PauseFailingAsync(change.UserId, token)}");
    }

    public static bool MayStopAgents(CredentialChange change) => change switch
    {
        { Change: CredentialChangeKind.Deleted } => true,
        { Status: not CredentialStatus.Valid } => true,
        { Kind: CredentialKind.Exchange, Change: CredentialChangeKind.Validated or CredentialChangeKind.Rotated } => true,
        _ => false,
    };
}

/// <summary>
/// Every hour, pauses the Active agents whose credentials no longer let them trade, in case the event that
/// should have paused them was lost. It asks nothing of Binance or the AI providers.
/// </summary>
internal sealed class AgentPrerequisitesJob(AgentPrerequisites prerequisites) : IRecurringJob
{
    public const string JobName = "agent-prerequisites";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromHours(1));

    public TimeSpan Timeout => TimeSpan.FromMinutes(30);

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        prerequisites.PauseFailingForEveryoneAsync(context, cancellationToken);
}
