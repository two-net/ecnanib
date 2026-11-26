using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Halts an agent that reached a loss limit, and resumes an agent that its daily loss limit
/// halted when the next UTC day begins (FR-RSK-8). See <see cref="IAgentHalts"/> and
/// docs/agents.md.
/// </summary>
/// <remarks>
/// The state is changed by <see cref="AgentStateChanges.MoveAsync"/>, which records the change,
/// under the lock of the account: so a halt and a control of the agent's user cannot cross, and
/// an agent that its user paused a moment before is not halted.
/// </remarks>
internal sealed class AgentHalts(
    IDbContextFactory<EcnanibDbContext> contexts,
    IKeyValidation keys,
    AgentRoles roles,
    INotifications notifications,
    TimeProvider clock,
    ILogger<AgentHalts> logger) : IAgentHalts
{
    /// <summary>
    /// How often the user of an agent is told that it stays halted after the day of its daily
    /// loss limit: the halts are asked about every quarter hour, and nothing has changed by the
    /// next one.
    /// </summary>
    public static readonly TimeSpan StaysSaidEvery = TimeSpan.FromDays(1);

    public async Task<Agent?> FindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Across users: a job has no user who is signed in, and takes the owner from the row.
        return await context.Set<Agent>().AsNoTracking().SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);
    }

    public async Task<bool> HaltAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, AgentStateCause cause, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (cause is not (AgentStateCause.DailyLossLimitReached or AgentStateCause.DrawdownLimitReached))
        {
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "An agent is halted for a loss limit that it reached, and for nothing else.");
        }

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An agent is halted inside the transaction that writes what its loss limit is counted from, and this context holds none.");
        }

        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted since the limit was worked out.
            return false;
        }

        // Asked for by owner, under the lock: the agent as it is now.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => agent.Id == agentId)
            .Select(agent => new { agent.State, agent.Mode })
            .SingleOrDefaultAsync(cancellationToken);

        // Only an agent that runs: one that its user paused or stopped, or that waits for its
        // user, stays as it is, with its position and its protective orders.
        if (agent is null || !AgentStates.RunsCycles(agent.State))
        {
            return false;
        }

        await AgentStateChanges.MoveAsync(context, userId, agentId, agent.Mode, AgentState.Halted, cause, at, cancellationToken);

        // Written inside the transaction of whoever asked, and stands for a change that is stored with it.
        logger.LogWarning("Agent {AgentId} of user {UserId} is halted: {AgentStateCause}", agentId, userId, cause);

        return true;
    }

    public async Task<int> EndDailyHaltsAsync(CancellationToken cancellationToken)
    {
        // The UTC day that it is now. An agent that was halted before it began was halted on an earlier day.
        var today = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);

        List<Halted> halted;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Across users, in the order of their ids. Most closes find none.
            halted = await context.Set<Agent>()
                .AsNoTracking()
                .Where(agent => agent.State == AgentState.Halted
                    && agent.StateCause == AgentStateCause.DailyLossLimitReached
                    && agent.StateChangedAt < today)
                .OrderBy(agent => agent.Id)
                .Select(agent => new Halted(agent.Id, agent.UserId))
                .ToListAsync(cancellationToken);
        }

        var resumed = 0;

        foreach (var agent in halted)
        {
            try
            {
                if (await EndAsync(agent, cancellationToken))
                {
                    resumed++;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Said once, with the agent. The others go on, and the next close asks again.
                logger.LogError(
                    error, "The halt of agent {AgentId} of user {UserId} for its daily loss limit could not be ended", agent.Id, agent.UserId);
            }
        }

        return resumed;
    }

    /// <summary>
    /// Resumes one agent whose halt for its daily loss limit is over, when its key and its team
    /// allow it. Everything is asked under the lock of its account, of the agent as it is then.
    /// Its user is told that it runs again, with the change of its state, or that it stays
    /// halted and why, once in <see cref="StaysSaidEvery"/> (FR-NTF-1).
    /// </summary>
    private async Task<bool> EndAsync(Halted halted, CancellationToken cancellationToken)
    {
        var (agentId, userId) = halted;

        // What keeps the agent halted, when something does: said after the transaction is over.
        Agent? kept;
        string keeps;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
            {
                return false;
            }

            var agent = await context.Set<Agent>()
                .AsNoTracking()
                .OwnedBy(userId)
                .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

            // The time as it is under the lock: a change that waited for the lock is later than the one that held it.
            var now = clock.GetUtcNow();

            // Its user may have stopped it meanwhile, or resumed it a moment ago.
            if (agent is null || AgentStates.HaltEnds(agent.State, agent.StateCause, agent.StateChangedAt) is not { } ends || now < ends)
            {
                return false;
            }

            // An operator disabled the account of its user: nothing of a disabled account starts
            // to run, and the service resumes nothing for it either (FR-OPS-4). The agent stays
            // halted, so its position is still closed where that failed, and the first close
            // after the account is enabled again ends the halt. Its user is not told: they
            // cannot sign in to do anything about it.
            if (await AccountStanding.IsDisabledAsync(context, userId, cancellationToken))
            {
                logger.LogInformation(
                    "Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: the account of its user is disabled",
                    agentId,
                    userId);

                return false;
            }

            // What a resume of its user asks, as far as it is known without the exchange (FR-KEY-5).
            if (await keys.BlockForAsync(userId, agent.Mode, agent.Market, cancellationToken) is { } block)
            {
                logger.LogInformation(
                    "Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: the Binance key for {TradingMode} blocks it ({StartBlock})",
                    agentId,
                    userId,
                    agent.Mode,
                    block);

                kept = agent;
                keeps = Says(block, agent.Mode, agent.Market);
            }
            else
            {
                // An agent runs with its whole team (FR-TEAM-1).
                var blocked = await roles.BlocksAsync(context, userId, agentId, cancellationToken);

                if (blocked.Count == 0)
                {
                    await AgentStateChanges.MoveAsync(
                        context, userId, agentId, agent.Mode, AgentState.Running, AgentStateCause.DailyLossHaltEnded, now, cancellationToken);

                    // Its user is told with the change, in the same transaction: so they are
                    // told when the agent runs again, and not when it does not (FR-NTF-1).
                    await notifications.RaiseInAsync(
                        context,
                        userId,
                        NotificationType.AgentResumed,
                        agentId,
                        agent.Mode,
                        $"The agent in {agent.Symbol} ({agent.Mode}) runs again: the UTC day in which its daily loss limit halted it is over, " +
                        "and the service resumed it.",
                        cancellationToken);

                    await transaction.CommitAsync(cancellationToken);

                    logger.LogInformation(
                        "Agent {AgentId} of user {UserId} runs again: the UTC day in which its daily loss limit halted it is over", agentId, userId);

                    return true;
                }

                logger.LogInformation(
                    "Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: {RoleCount} of the roles of its team cannot work ({Roles})",
                    agentId,
                    userId,
                    blocked.Count,
                    string.Join(", ", blocked.Select(role => $"{role.Role}: {role.Problem}")));

                var names = RoleNames.Listed([.. blocked.Select(role => role.Role)]);

                kept = agent;
                keeps = blocked.Count == 1
                    ? $"its {names} cannot work. Give the role a model that you can select, or store a key that works for its provider"
                    : $"its {names} cannot work. Give each of these roles a model that you can select, or store a key that works for its provider";
            }
        }

        // Nothing changed, and nothing is held any more. The halts are asked about every
        // quarter hour, so what keeps this one is said once in a day.
        try
        {
            await notifications.RaiseOnceAsync(
                userId,
                NotificationType.AttentionRequired,
                agentId,
                kept.Mode,
                $"The agent in {kept.Symbol} ({kept.Mode}) stays halted after the day of its daily loss limit: {keeps}. The service " +
                "resumes it once that is put right. This is said once in a day for an agent.",
                StaysSaidEvery,
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The user {UserId} could not be told that agent {AgentId} stays halted", userId, agentId);
        }

        return false;
    }

    /// <summary>What about the Binance key of a user keeps an agent halted, and what to do about it, as its user is told.</summary>
    private static string Says(StartBlock block, TradingMode mode, Market market) => block switch
    {
        StartBlock.NoKey => $"you have stored no Binance key for {mode}. Store one on the page of your keys",
        StartBlock.NotValid => $"your Binance key for {mode} is not valid. Store a key that works on the page of your keys",
        StartBlock.SpotTradingMissing or StartBlock.FuturesTradingMissing =>
            $"your Binance key for {mode} lacks the permission to trade on the {market} market. Give it the permission at Binance, and have the key checked again on the page of your keys",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Nothing says what keeps the agent halted."),
    };

    private sealed record Halted(Guid Id, Guid UserId);
}
