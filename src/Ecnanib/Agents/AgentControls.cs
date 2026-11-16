using Ecnanib.Accounts;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Starts, pauses, resumes, and stops the agents of users, and closes their positions by hand
/// (FR-AGT-1, FR-AGT-10 to FR-AGT-12). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Every change is one transaction, which starts by locking the row of the account, as what
/// changes the settings of an agent does, and as what changes a key does. So the state that a
/// control finds under the lock is the state until the transaction ends, and a key is not
/// deleted or refused between the question about it and the start.
/// </para>
/// <para>
/// The state is changed by <see cref="AgentStateChanges.MoveAsync"/>, which records the change,
/// and the action is recorded in the audit trail in the same transaction (NFR-LOG-7).
/// </para>
/// <para>
/// Agents places no order. A position is closed by whatever keeps positions, which is asked
/// through <see cref="IAgentExposure"/>, after the transaction and never inside it: the request
/// waits for the exchange, and would hold the lock for as long.
/// </para>
/// </remarks>
internal sealed class AgentControls(
    IDbContextFactory<EcnanibDbContext> contexts,
    IKeyValidation keys,
    AgentRoles roles,
    IEnumerable<IAgentExposure> exposures,
    TimeProvider clock,
    ILogger<AgentControls> logger) : IAgentControls
{
    public Task<AgentControlResult> StartAsync(HttpContext request, Guid agentId, bool confirmedForLive) =>
        MoveAsync(request, agentId, AgentControl.Start, AgentStateCause.UserStarted, AuditAction.AgentStarted, confirmedForLive);

    public Task<AgentControlResult> PauseAsync(HttpContext request, Guid agentId, bool confirmedForLive) =>
        MoveAsync(request, agentId, AgentControl.Pause, AgentStateCause.UserPaused, AuditAction.AgentPaused, confirmedForLive);

    public Task<AgentControlResult> ResumeAsync(HttpContext request, Guid agentId, bool confirmedForLive) =>
        MoveAsync(request, agentId, AgentControl.Resume, AgentStateCause.UserResumed, AuditAction.AgentResumed, confirmedForLive);

    public async Task<StopAgentResult> StopAsync(HttpContext request, Guid agentId, PositionAtStop? choice)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        bool closes;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (await FindUnderLockAsync(context, userId, agentId, cancellationToken) is not { } agent)
            {
                return new StopAgentResult(StopAgentOutcome.NotFound);
            }

            if (agent.State == AgentState.Stopped)
            {
                // The form was sent twice, or sent from two browsers. The first one stopped the
                // agent, and did with its position what was chosen then.
                return new StopAgentResult(StopAgentOutcome.Stopped);
            }

            if (AgentStates.After(agent.State, AgentControl.Stop) is not { } stopped)
            {
                logger.LogInformation(
                    "User {UserId} was refused stopping agent {AgentId}, which is {AgentState}", userId, agentId, agent.State);

                return new StopAgentResult(StopAgentOutcome.NotOffered);
            }

            // Asked under the lock, of what the agent holds now, whatever the page showed.
            var holds = await exposures.HoldPositionAsync(context, agentId, cancellationToken);

            if (holds && choice is null)
            {
                // What happens to the position is its user's choice, and is not made for them (FR-AGT-11).
                return new StopAgentResult(StopAgentOutcome.ChoiceNeeded);
            }

            closes = holds && choice == PositionAtStop.Close;

            var cause = !holds
                ? AgentStateCause.UserStopped
                : closes
                    ? AgentStateCause.UserStoppedClosingPosition
                    : AgentStateCause.UserStoppedKeepingPosition;

            var now = clock.GetUtcNow();
            var origin = AuditOrigin.Of(request);

            await AgentStateChanges.MoveAsync(context, userId, agentId, agent.Mode, stopped, cause, now, cancellationToken);

            await AuditTrail.RecordAsync(
                context, userId, AuditAction.AgentStopped, AuditTarget.Agent(agentId), now, origin, cancellationToken);

            if (closes)
            {
                // Closing the position is a manual close, and is recorded as one, with the stop
                // that it was chosen with.
                await AuditTrail.RecordAsync(
                    context, userId, AuditAction.AgentPositionCloseRequested, AuditTarget.Agent(agentId), now, origin, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "User {UserId} stopped agent {AgentId}, which was {AgentState}: {AgentStateCause}", userId, agentId, agent.State, cause);
        }

        if (!closes)
        {
            return new StopAgentResult(StopAgentOutcome.Stopped);
        }

        // The agent is stopped, whatever becomes of the close: it opens nothing from now on.
        return new StopAgentResult(StopAgentOutcome.Stopped, await CloseAtMarketAsync(userId, agentId));
    }

    public async Task<ClosePositionOutcome> ClosePositionAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // In whatever state the agent is, and whatever its key is: closing a position
            // reduces risk, and nothing of Agents stands in its way (FR-AGT-12).
            if (await FindUnderLockAsync(context, userId, agentId, cancellationToken) is null)
            {
                return ClosePositionOutcome.NotFound;
            }

            if (!await exposures.HoldPositionAsync(context, agentId, cancellationToken))
            {
                // Nothing is open: the form was sent twice, or the position was closed by its
                // protective orders meanwhile. Nothing was asked for, and nothing is recorded.
                return ClosePositionOutcome.NothingOpen;
            }

            await AuditTrail.RecordAsync(
                context,
                userId,
                AuditAction.AgentPositionCloseRequested,
                AuditTarget.Agent(agentId),
                clock.GetUtcNow(),
                AuditOrigin.Of(request),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return await CloseAtMarketAsync(userId, agentId) switch
        {
            PositionCloseOutcome.Closed => ClosePositionOutcome.Closed,
            PositionCloseOutcome.NothingOpen => ClosePositionOutcome.NothingOpen,
            _ => ClosePositionOutcome.Failed,
        };
    }

    public async Task<AgentStateHistory?> HistoryAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: an agent of another user is not found, and neither are its changes.
        if (!await context.Set<Agent>().OwnedBy(userId).AnyAsync(agent => agent.Id == agentId, cancellationToken))
        {
            return null;
        }

        var changes = context.Set<AgentStateChange>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(change => change.AgentId == agentId);

        var total = await changes.CountAsync(cancellationToken);

        // Latest first. The id decides between two changes that the clock gave the same time.
        var latest = await changes
            .OrderByDescending(change => change.ChangedAt)
            .ThenByDescending(change => change.Id)
            .Take(AgentStateHistory.MostShown)
            .ToListAsync(cancellationToken);

        return new AgentStateHistory(
            [.. latest.Select(change => new AgentStateChangeView(change.Mode, change.State, change.Cause, change.ChangedAt))], total);
    }

    /// <summary>Starts, pauses, or resumes an agent: the controls that ask nothing about a position.</summary>
    private async Task<AgentControlResult> MoveAsync(
        HttpContext request, Guid agentId, AgentControl control, AgentStateCause cause, AuditAction action, bool confirmedForLive)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await FindUnderLockAsync(context, userId, agentId, cancellationToken) is not { } agent)
        {
            return new AgentControlResult(AgentControlOutcome.NotFound);
        }

        if (AgentStates.After(agent.State, control) is not { } next)
        {
            if (agent.State == AgentStates.LeadsTo(control))
            {
                // The form was sent twice, or sent from two browsers: the agent is where the
                // first one left it, which is what was asked for.
                return new AgentControlResult(AgentControlOutcome.Done);
            }

            logger.LogInformation(
                "User {UserId} was refused the control {AgentControl} of agent {AgentId}, which is {AgentState}",
                userId,
                control,
                agentId,
                agent.State);

            return new AgentControlResult(AgentControlOutcome.NotOffered);
        }

        if (agent.Mode == TradingMode.Live && !confirmedForLive)
        {
            // The control would act on an agent that trades with real money, and its user was
            // not asked once more (FR-UI-3). Decided under the lock, of the mode that the agent
            // is in now: a form of a page that showed the agent in Demo does not start it in Live.
            return new AgentControlResult(AgentControlOutcome.NotConfirmedForLive);
        }

        if (next == AgentState.Running)
        {
            // The credential of the agent is the one of its user for its mode. It is asked
            // about under the lock that a change to a key takes, so the answer holds until the
            // agent runs. Binance is asked nothing (FR-KEY-5).
            if (await keys.BlockForAsync(userId, agent.Mode, agent.Market, cancellationToken) is { } block)
            {
                logger.LogInformation(
                    "User {UserId} was refused the control {AgentControl} of agent {AgentId}: the Binance key for {TradingMode} blocks it ({StartBlock})",
                    userId,
                    control,
                    agentId,
                    agent.Mode,
                    block);

                return new AgentControlResult(AgentControlOutcome.KeyBlocks, block);
            }

            // At most one active agent for each credential, market, and symbol (FR-AGT-9). An
            // agent that is resumed is active already. The index of the table holds to the
            // same, whatever changes a state.
            if (!AgentStates.IsActive(agent.State) && await AnotherIsActiveAsync(context, agent, cancellationToken))
            {
                logger.LogInformation(
                    "User {UserId} was refused starting agent {AgentId}: another agent of theirs is active for the same market and symbol",
                    userId,
                    agentId);

                return new AgentControlResult(AgentControlOutcome.AnotherAgentActive);
            }

            // An agent runs with its whole team: every role has a model, and each model is one
            // that its user can select now, with a key that is valid (FR-TEAM-1, FR-KEY-10,
            // FR-CAT-8). Asked under the lock that deleting a key and marking one invalid take,
            // as the Binance credential is. No provider is asked.
            var blocked = await roles.BlocksAsync(context, userId, agentId, cancellationToken);

            if (blocked.Count > 0)
            {
                logger.LogInformation(
                    "User {UserId} was refused the control {AgentControl} of agent {AgentId}: {RoleCount} of the roles of its team cannot work ({Roles})",
                    userId,
                    control,
                    agentId,
                    blocked.Count,
                    string.Join(", ", blocked.Select(role => $"{role.Role}: {role.Problem}")));

                return new AgentControlResult(AgentControlOutcome.RolesBlock, Roles: blocked);
            }
        }

        var now = clock.GetUtcNow();

        await AgentStateChanges.MoveAsync(context, userId, agentId, agent.Mode, next, cause, now, cancellationToken);

        // Starting, pausing, and stopping an agent are money-relevant, and are recorded with
        // the change (NFR-LOG-7).
        await AuditTrail.RecordAsync(
            context, userId, action, AuditTarget.Agent(agentId), now, AuditOrigin.Of(request), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} moved agent {AgentId} from {PreviousAgentState} to {AgentState}: {AgentStateCause}",
            userId,
            agentId,
            agent.State,
            next,
            cause);

        return new AgentControlResult(AgentControlOutcome.Done);
    }

    /// <summary>
    /// Locks the row of the account, and reads the agent under the lock: this is the agent until
    /// the transaction ends. Null when the user has no agent of that id, or no account any more.
    /// </summary>
    private static async Task<Agent?> FindUnderLockAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted, on another browser, since this request was signed in.
            return null;
        }

        // Asked for by owner: an agent of another user is not found, like one that does not exist.
        return await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);
    }

    /// <summary>Whether the user has another agent that is active for the credential, market, and symbol of an agent.</summary>
    private static async Task<bool> AnotherIsActiveAsync(EcnanibDbContext context, Agent agent, CancellationToken cancellationToken)
    {
        // The credential is the user and the mode. A user has few agents: they are read, and
        // the code says which of them is active.
        var states = await context.Set<Agent>()
            .OwnedBy(agent.UserId)
            .Where(other => other.Id != agent.Id
                && other.Mode == agent.Mode
                && other.Market == agent.Market
                && other.Symbol == agent.Symbol)
            .Select(other => other.State)
            .ToListAsync(cancellationToken);

        return states.Any(AgentStates.IsActive);
    }

    /// <summary>
    /// Asks whatever keeps positions to close the position of an agent at market. The request of
    /// the user is recorded by now, so it is not given up when the browser goes away.
    /// </summary>
    private async Task<PositionCloseOutcome> CloseAtMarketAsync(Guid userId, Guid agentId)
    {
        var outcome = await exposures.ClosePositionAtMarketAsync(agentId, CancellationToken.None);

        if (outcome == PositionCloseOutcome.Failed)
        {
            logger.LogWarning(
                "The position of agent {AgentId} could not be closed at market, as user {UserId} asked. It is open as it was",
                agentId,
                userId);
        }
        else
        {
            logger.LogInformation(
                "User {UserId} asked to close the position of agent {AgentId} at market: {PositionCloseOutcome}", userId, agentId, outcome);
        }

        return outcome;
    }
}

/// <summary>
/// Moves the agents that run with a key to Attention required when the key is deleted or refused
/// (FR-KEY-10): the Binance credential of their environment, or the key of an AI provider that a
/// role of their team uses. Credentials tells it, inside the transaction that deletes the key or
/// marks it, which has locked the row of the account. See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// The credential of an agent is the one of its user for its mode, so the agents that depend on
/// a credential are those of the user in that environment. Of them, the ones that run are moved:
/// see <see cref="AgentStates.NeedsItsKey"/>. An agent that does not run stays as its user left
/// it, and is asked about its credential when it is started or resumed.
/// </para>
/// <para>
/// A user has one key for each AI provider, whatever the environment, so the agents that depend
/// on it are those of the user, in Demo and in Live, that have a role whose model is of the
/// provider. Of them too, the ones that run are moved, and the others are asked about their team
/// when they are started or resumed.
/// </para>
/// </remarks>
internal sealed class AgentKeyDependents(TimeProvider clock, ILogger<AgentKeyDependents> logger) : IKeyDependents
{
    public Task AiKeyDeletedAsync(
        EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken) =>
        RequireAttentionAsync(context, userId, provider, AgentStateCause.AiKeyDeleted, cancellationToken);

    public Task AiKeyInvalidAsync(
        EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken) =>
        RequireAttentionAsync(context, userId, provider, AgentStateCause.AiKeyInvalid, cancellationToken);

    public Task ExchangeKeyDeletedAsync(
        EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
        RequireAttentionAsync(context, userId, environment, AgentStateCause.ExchangeKeyDeleted, cancellationToken);

    public Task ExchangeKeyInvalidAsync(
        EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
        RequireAttentionAsync(context, userId, environment, AgentStateCause.ExchangeKeyInvalid, cancellationToken);

    private async Task RequireAttentionAsync(
        EcnanibDbContext context, Guid userId, TradingMode environment, AgentStateCause cause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The agents of the user in the environment of the key, in the order of their ids. A
        // user has few: they are read, and the code says which of them needs the key.
        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => agent.Mode == environment)
            .OrderBy(agent => agent.Id)
            .Select(agent => new { agent.Id, agent.State })
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();

        foreach (var agent in agents.Where(agent => AgentStates.NeedsItsKey(agent.State)))
        {
            await AgentStateChanges.MoveAsync(
                context, userId, agent.Id, environment, AgentState.AttentionRequired, cause, now, cancellationToken);

            // Stored when the change to the key is, and not when it is not: both are of the
            // transaction of whoever tells.
            logger.LogInformation(
                "Agent {AgentId} of user {UserId} requires attention with the change to the Binance key for {TradingMode}: {AgentStateCause}",
                agent.Id,
                userId,
                environment,
                cause);
        }
    }

    private async Task RequireAttentionAsync(
        EcnanibDbContext context, Guid userId, AiProviderCode provider, AgentStateCause cause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The agents of the user that have a role whose model is of the provider, in the order
        // of their ids, in either environment. A user has few: they are read, and the code says
        // which of them needs the key.
        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => context.Set<AgentRole>().Any(role =>
                role.AgentId == agent.Id
                && context.Set<AiModel>().Any(model => model.Id == role.ModelId && model.Provider == provider)))
            .OrderBy(agent => agent.Id)
            .Select(agent => new { agent.Id, agent.State, agent.Mode })
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();

        foreach (var agent in agents.Where(agent => AgentStates.NeedsItsKey(agent.State)))
        {
            // Labeled with the mode that the agent is in, as every change of its state is (FR-BIN-11).
            await AgentStateChanges.MoveAsync(
                context, userId, agent.Id, agent.Mode, AgentState.AttentionRequired, cause, now, cancellationToken);

            logger.LogInformation(
                "Agent {AgentId} of user {UserId} requires attention with the change to the key for the AI provider {Provider}: {AgentStateCause}",
                agent.Id,
                userId,
                provider.Value,
                cause);
        }
    }
}
