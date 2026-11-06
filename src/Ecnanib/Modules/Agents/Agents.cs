using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// A user's agents and their lifecycle (FR-AGT-01 to FR-AGT-05, FR-AGT-10, FR-AGT-12). Every query filters on
/// the user (NFR-SEC-04). Each change commits with its audit record, and writes that could break a rule
/// across rows take the user's <see cref="AgentLock"/>. See Agents in docs/architecture.md.
/// </summary>
internal sealed class Agents(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IAuditTrail audit,
    ICredentials credentials,
    ActivationChecks checks,
    RoleModels roleModels,
    IAgentExposure exposure,
    AgentsSettings settings,
    RiskCaps caps,
    INotifications notifications,
    TimeProvider time,
    IServiceProvider services,
    ILogger<Agents> logger) : IAgents
{
    /// <summary>
    /// The modules that keep an agent's history. Resolved on first use, because they sit above Agents and use its
    /// contracts, so a constructor parameter would make a cycle.
    /// </summary>
    private readonly Lazy<IReadOnlyList<IAgentHistory>> _history = new(() => [.. services.GetServices<IAgentHistory>()]);

    private static readonly AgentStatus[] Activatable = [AgentStatus.Draft, AgentStatus.Paused, AgentStatus.Stopped, AgentStatus.Error];
    private static readonly AgentStatus[] Pausable = [AgentStatus.Active];
    private static readonly AgentStatus[] Stoppable = [AgentStatus.Active, AgentStatus.Paused, AgentStatus.Error];
    private static readonly AgentStatus[] Archivable = [AgentStatus.Draft, AgentStatus.Paused, AgentStatus.Stopped, AgentStatus.Error];
    private static readonly AgentStatus[] Deletable = [AgentStatus.Archived];

    public int MaxAgentsPerUser => settings.MaxAgentsPerUser;

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(Guid userId, bool includeArchived, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await Rows(context, userId)
            .Where(agent => includeArchived || agent.Status != AgentStatus.Archived)
            .OrderBy(agent => agent.CreatedAt)
            .ThenBy(agent => agent.Id)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => row.ToRecord())];
    }

    public async Task<AgentRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return (await FindRowAsync(context, userId, id, cancellationToken))?.ToRecord();
    }

    public Task<RoleChoices> ListRoleChoicesAsync(Guid userId, CancellationToken cancellationToken) =>
        roleModels.ChoicesAsync(userId, cancellationToken);

    public async Task<AgentSaveResult> CreateAsync(Guid userId, AgentSettings input, CancellationToken cancellationToken)
    {
        var (refusal, cleaned, quoteAsset, exchangeKey) = await PrepareAsync(userId, input, current: null, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        var now = time.GetUtcNow();
        var id = Guid.NewGuid();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        if (await LimitReachedAsync(context, userId, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.LimitReached);
        }

        if (await NameTakenAsync(context, userId, cleaned!.Name, except: null, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.NameTaken);
        }

        var agent = NewDraft(id, userId, cleaned, quoteAsset!, now);
        context.Set<Agent>().Add(agent);
        context.Set<AgentRoleAssignment>().AddRange(cleaned.Roles.Select(role => AgentRoleAssignment.Of(id, role)));
        audit.Add(
            context,
            AgentAudit.ByUser(AuditAction.AgentCreated, userId, id, AgentAudit.Differences(null, AgentAudit.Describe(cleaned, quoteAsset!, exchangeKey))));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created agent {AgentId} for user {UserId}: {Market} {Environment}", id, userId, cleaned.Market, cleaned.Environment);

        return new AgentSaveResult(AgentSaveOutcome.Saved, await FindAsync(userId, id, cancellationToken));
    }

    public async Task<AgentSaveResult> UpdateAsync(
        Guid userId, Guid id, int version, AgentSettings input, CancellationToken cancellationToken)
    {
        AgentRecord? before;

        await using (var reading = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            before = (await FindRowAsync(reading, userId, id, cancellationToken))?.ToRecord();
        }

        if (before is null)
        {
            return new AgentSaveResult(AgentSaveOutcome.NotFound);
        }

        if (before.Status == AgentStatus.Archived)
        {
            return new AgentSaveResult(AgentSaveOutcome.Archived, before);
        }

        if (before.Version != version)
        {
            return new AgentSaveResult(AgentSaveOutcome.Changed, before);
        }

        var (refusal, cleaned, quoteAsset, exchangeKey) = await PrepareAsync(userId, input, before, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        // An Active agent must stay activatable. These checks can call Binance, so they run before the
        // transaction, and the update is conditional on the version they checked.
        if (before.Status == AgentStatus.Active)
        {
            var (problems, _) = await checks.CheckAsync(userId, cleaned!, cancellationToken);

            if (problems.Count > 0)
            {
                return new AgentSaveResult(AgentSaveOutcome.WouldFailActivation, before, Problems: problems);
            }
        }

        var now = time.GetUtcNow();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await context.Set<Agent>().SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);

        if (agent is null)
        {
            return new AgentSaveResult(AgentSaveOutcome.NotFound);
        }

        if (agent.Version != version || agent.Status != before.Status)
        {
            return new AgentSaveResult(AgentSaveOutcome.Changed, await FindAsync(userId, id, cancellationToken));
        }

        if (Locked(before.Settings, cleaned!) && await exposure.HasOpenPositionsOrOrdersAsync(userId, id, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.LockedByOpenPositions, before);
        }

        if (cleaned!.Name != before.Settings.Name && await NameTakenAsync(context, userId, cleaned.Name, except: id, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.NameTaken, before);
        }

        if (Agent.SlotStatuses.Contains(agent.Status) && await SlotHolderAsync(context, cleaned, except: id, cancellationToken) is { } holder)
        {
            return new AgentSaveResult(
                AgentSaveOutcome.SlotTaken, before, Problems: [new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: holder)]);
        }

        var previousKey = before.Settings.ExchangeCredentialId == cleaned.ExchangeCredentialId
            ? exchangeKey
            : await LabelAsync(userId, before.Settings.ExchangeCredentialId, cancellationToken);
        var changes = AgentAudit.Differences(
            AgentAudit.Describe(before.Settings, before.QuoteAsset, previousKey),
            AgentAudit.Describe(cleaned, quoteAsset!, exchangeKey));

        agent.Apply(cleaned, quoteAsset!);
        agent.UpdatedAt = now;
        agent.Version++;

        await context.Set<AgentRoleAssignment>().Where(role => role.AgentId == id).ExecuteDeleteAsync(cancellationToken);
        context.Set<AgentRoleAssignment>().AddRange(cleaned.Roles.Select(role => AgentRoleAssignment.Of(id, role)));

        if (changes.Count > 0)
        {
            audit.Add(context, AgentAudit.ByUser(AuditAction.AgentUpdated, userId, id, changes));
        }

        // FR-AUD-01 names execution-mode changes on their own, because they decide whether trades wait for the user.
        if (cleaned.ExecutionMode != before.Settings.ExecutionMode)
        {
            audit.Add(
                context,
                AgentAudit.ByUser(
                    AuditAction.AgentExecutionModeChanged,
                    userId,
                    id,
                    [new(AgentAuditFields.ExecutionMode, before.Settings.ExecutionMode.ToString(), cleaned.ExecutionMode.ToString())]));
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new AgentSaveResult(AgentSaveOutcome.Changed, await FindAsync(userId, id, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Updated agent {AgentId} for user {UserId}", id, userId);

        return new AgentSaveResult(AgentSaveOutcome.Saved, await FindAsync(userId, id, cancellationToken));
    }

    public async Task<AgentSaveResult> CloneAsync(Guid userId, Guid id, string name, CancellationToken cancellationToken)
    {
        if (AgentInput.Text(name, AgentLimits.NameMaxLength, multiline: false) is not { } cleaned)
        {
            return new AgentSaveResult(AgentSaveOutcome.Invalid, Fields: [AgentField.Name]);
        }

        var now = time.GetUtcNow();
        var cloneId = Guid.NewGuid();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var source = await FindRowAsync(context, userId, id, cancellationToken);

        if (source is null)
        {
            return new AgentSaveResult(AgentSaveOutcome.NotFound);
        }

        if (await LimitReachedAsync(context, userId, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.LimitReached);
        }

        if (await NameTakenAsync(context, userId, cleaned, except: null, cancellationToken))
        {
            return new AgentSaveResult(AgentSaveOutcome.NameTaken);
        }

        // The same settings and none of the history (FR-AGT-12).
        var copy = source.Settings with { Name = cleaned };
        var clone = NewDraft(cloneId, userId, copy, source.QuoteAsset, now);
        context.Set<Agent>().Add(clone);
        context.Set<AgentRoleAssignment>().AddRange(copy.Roles.Select(role => AgentRoleAssignment.Of(cloneId, role)));

        var changes = AgentAudit.Differences(
            null, AgentAudit.Describe(copy, source.QuoteAsset, await LabelAsync(userId, copy.ExchangeCredentialId, cancellationToken)));
        changes.Add(new AuditChange(AgentAuditFields.ClonedFrom, null, source.Name));
        audit.Add(context, AgentAudit.ByUser(AuditAction.AgentCreated, userId, cloneId, changes));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Cloned agent {SourceAgentId} as {AgentId} for user {UserId}", id, cloneId, userId);

        return new AgentSaveResult(AgentSaveOutcome.Saved, await FindAsync(userId, cloneId, cancellationToken));
    }

    public Task<ActivationResult> ActivateAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        ActivateAsync(userId, id, resumingFrom: null, cancellationToken);

    /// <summary>
    /// The user's activation, or, with <paramref name="resumingFrom"/>, the system's resume after a daily or weekly
    /// loss limit's pause ended (FR-RISK-08). The system's is refused unless the agent is still Paused for that
    /// reason, so it never undoes what the user did meanwhile.
    /// </summary>
    internal async Task<ActivationResult> ActivateAsync(Guid userId, Guid id, AgentStatusReason? resumingFrom, CancellationToken cancellationToken)
    {
        var before = await FindAsync(userId, id, cancellationToken);

        if (before is null)
        {
            return new ActivationResult(ActivationOutcome.NotFound);
        }

        var bySystem = resumingFrom is not null;

        if (!Activatable.Contains(before.Status) || (bySystem && (before.Status != AgentStatus.Paused || before.StatusReason != resumingFrom)))
        {
            return new ActivationResult(ActivationOutcome.NotAllowed, before);
        }

        // These can call Binance, so they run before the transaction, which is conditional on the version they checked.
        var (problems, warnings) = await checks.CheckAsync(userId, before.Settings, cancellationToken);
        var now = time.GetUtcNow();

        // A loss limit's pause lasts its whole period, whatever the status, so stopping the agent can't end it early.
        if (before.LossLimitUntil is { } until && until > now)
        {
            problems.Insert(0, new ActivationProblem(ActivationProblemKind.LossLimitPause, Until: until));
        }

        var reason = bySystem ? AgentStatusReason.LossLimitEnded : AgentStatusReason.User;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await AgentLock.TakeAsync(context, userId, cancellationToken);

            if (await SlotHolderAsync(context, before.Settings, except: id, cancellationToken) is { } holder)
            {
                problems.Add(new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: holder));
            }

            if (problems.Count > 0)
            {
                logger.LogInformation(
                    "Refused to activate agent {AgentId} for user {UserId}: {Problems}", id, userId, problems.Select(problem => problem.Kind));
                return new ActivationResult(ActivationOutcome.Refused, before, problems, warnings);
            }

            var updated = await context.Set<Agent>()
                .Where(agent => agent.Id == id && agent.UserId == userId && agent.Version == before.Version && Activatable.Contains(agent.Status))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(agent => agent.Status, AgentStatus.Active)
                        .SetProperty(agent => agent.StatusReason, reason)
                        .SetProperty(agent => agent.StatusChangedAt, now)
                        .SetProperty(agent => agent.LossLimitUntil, (DateTimeOffset?)null)
                        .SetProperty(agent => agent.Version, agent => agent.Version + 1),
                    cancellationToken);

            if (updated == 0)
            {
                return new ActivationResult(ActivationOutcome.Changed, await FindAsync(userId, id, cancellationToken));
            }

            var changes = AgentAudit.StatusChange(before.Status, AgentStatus.Active, reason);
            audit.Add(
                context,
                bySystem
                    ? AgentAudit.BySystem(AuditAction.AgentActivated, userId, id, changes)
                    : AgentAudit.ByUser(AuditAction.AgentActivated, userId, id, changes));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Activated agent {AgentId} for user {UserId}, from {Status}, because {Reason}", id, userId, before.Status, reason);

        var activated = await FindAsync(userId, id, cancellationToken);

        // Nobody sees a warning when the system resumes the agent, so Binance isn't asked for one.
        if (bySystem)
        {
            return new ActivationResult(ActivationOutcome.Activated, activated, [], warnings);
        }

        // The agent is active whatever happens here, so a failure only loses a warning the SRS says "should" give.
        try
        {
            if (activated is not null && await CapitalWarningAsync(userId, activated, cancellationToken) is { } warning)
            {
                warnings.Add(warning);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Checking the capital of agent {AgentId} against its account failed", id);
        }

        return new ActivationResult(ActivationOutcome.Activated, activated, [], warnings);
    }

    public async Task<bool> PauseForLossLimitAsync(Guid userId, Guid id, LossLimitBreach breach, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breach);

        var reason = breach.Kind switch
        {
            LossLimitKind.Daily => AgentStatusReason.DailyLossLimit,
            LossLimitKind.Weekly => AgentStatusReason.WeeklyLossLimit,
            LossLimitKind.ConsecutiveLosses => AgentStatusReason.ConsecutiveLosses,
            _ => throw new ArgumentOutOfRangeException(nameof(breach), breach.Kind, "Not a loss limit."),
        };

        // A daily or weekly limit pauses for its period, and only the consecutive-loss count waits for the user.
        if ((breach.Kind == LossLimitKind.ConsecutiveLosses) != (breach.Until is null))
        {
            throw new ArgumentException("A daily or weekly limit's pause has an end, and the consecutive-loss count's has none.", nameof(breach));
        }

        var now = time.GetUtcNow();

        // A period that has already ended would pause nothing.
        if (breach.Until <= now)
        {
            return false;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);

        if (agent is not { Status: AgentStatus.Active }
            || !await AgentPause.TryAsync(context, audit, userId, id, agent.Version, reason, now, cancellationToken, breach.Until))
        {
            return false;
        }

        notifications.Add(
            context,
            new Notice(NotificationType.AgentPausedLossLimit, userId, $"/agents/{id}/settings", agent.Name, agent.Environment == TradingEnvironment.Demo));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Paused agent {AgentId} of user {UserId} because of {Reason}, until {Until}", id, userId, reason, breach.Until);

        return true;
    }

    public async Task<bool> EnterErrorAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        const AgentStatusReason reason = AgentStatusReason.RepeatedCycleErrors;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);

        if (agent is not { Status: AgentStatus.Active })
        {
            return false;
        }

        // Conditional on what was read, as pauses are, so a user's change at the same moment wins or loses whole.
        var updated = await context.Set<Agent>()
            .Where(row => row.Id == id && row.UserId == userId && row.Version == agent.Version && row.Status == AgentStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, AgentStatus.Error)
                    .SetProperty(row => row.StatusReason, reason)
                    .SetProperty(row => row.StatusChangedAt, now)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);

        if (updated == 0)
        {
            return false;
        }

        audit.Add(
            context,
            AgentAudit.BySystem(AuditAction.AgentErrored, userId, id, AgentAudit.StatusChange(AgentStatus.Active, AgentStatus.Error, reason)));
        notifications.Add(
            context,
            new Notice(NotificationType.AgentPausedErrors, userId, $"/agents/{id}/settings", agent.Name, agent.Environment == TradingEnvironment.Demo));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Put agent {AgentId} of user {UserId} in Error, because its decision cycles kept failing", id, userId);

        return true;
    }

    /// <summary>
    /// Resumes an agent whose daily or weekly loss limit's pause has ended (FR-RISK-08), by the system. When the
    /// activation checks fail, it stays paused for its user to resume, and is left alone from then on, unless all
    /// that failed was reaching Binance, which the next run tries again.
    /// </summary>
    internal async Task<ActivationOutcome> ResumeAfterLossLimitAsync(Guid userId, Guid id, AgentStatusReason reason, CancellationToken cancellationToken)
    {
        var result = await ActivateAsync(userId, id, reason, cancellationToken);

        if (result is { Outcome: ActivationOutcome.Refused, Agent: { } agent, Problems: { } problems }
            && problems.Any(problem => problem.Kind is not (ActivationProblemKind.SymbolUnverifiable or ActivationProblemKind.FuturesAccountUnverifiable)))
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Set<Agent>()
                .Where(row => row.Id == id && row.UserId == userId && row.Version == agent.Version && row.Status == AgentStatus.Paused)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.LossLimitUntil, (DateTimeOffset?)null)
                        .SetProperty(row => row.Version, row => row.Version + 1),
                    cancellationToken);

            logger.LogInformation(
                "Agent {AgentId} of user {UserId} stays paused after its loss limit's pause ended, because it fails activation: {Problems}",
                id,
                userId,
                problems.Select(problem => problem.Kind));
        }

        return result.Outcome;
    }

    public Task<TransitionResult> PauseAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(userId, id, Pausable, AgentStatus.Paused, AuditAction.AgentPaused, checkExposure: false, cancellationToken);

    public Task<TransitionResult> StopAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(userId, id, Stoppable, AgentStatus.Stopped, AuditAction.AgentStopped, checkExposure: false, cancellationToken);

    public Task<TransitionResult> ArchiveAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(userId, id, Archivable, AgentStatus.Archived, AuditAction.AgentArchived, checkExposure: true, cancellationToken);

    public async Task<TransitionResult> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await FindRowAsync(context, userId, id, cancellationToken);

        if (agent is null)
        {
            return new TransitionResult(TransitionOutcome.NotFound);
        }

        if (!Deletable.Contains(agent.Status))
        {
            return new TransitionResult(TransitionOutcome.NotAllowed, agent.ToRecord());
        }

        // Its history first, in this transaction, so nothing is left behind. The roles go with it, through their foreign key.
        foreach (var history in _history.Value)
        {
            await history.DeleteAsync(context, userId, id, cancellationToken);
        }

        await context.Set<Agent>().Where(row => row.Id == id && row.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        audit.Add(
            context,
            AgentAudit.ByUser(
                AuditAction.AgentDeleted,
                userId,
                id,
                [
                    new(AgentAuditFields.Name, agent.Name, null),
                    new(AgentAuditFields.Market, agent.Market.ToString(), null),
                    new(AgentAuditFields.Environment, agent.Environment.ToString(), null),
                    new(AgentAuditFields.Symbol, agent.Symbol, null),
                ]));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Deleted agent {AgentId} for user {UserId}", id, userId);

        return new TransitionResult(TransitionOutcome.Done);
    }

    public async Task<IReadOnlyList<AgentUse>> ListUsingCredentialAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken)
    {
        var keys = await credentials.ListAsync(userId, cancellationToken);

        if (keys.FirstOrDefault(key => key.Id == credentialId) is not { } credential)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agents = await Rows(context, userId).Where(agent => agent.Status != AgentStatus.Archived).ToListAsync(cancellationToken);

        IEnumerable<Agent> dependents;

        if (credential.Kind == CredentialKind.Exchange)
        {
            dependents = agents.Where(agent => agent.ExchangeCredentialId == credentialId);
        }
        else
        {
            // An AI key matters only while it's the user's one valid key for its provider.
            var others = keys.Any(key => key.Id != credentialId && key.Kind == CredentialKind.Ai
                && key.Provider == credential.Provider && key.Status == CredentialStatus.Valid);

            dependents = credential.Status != CredentialStatus.Valid || others
                ? []
                : agents.Where(agent => agent.Roles.Any(role => AgentRoles.IsRequired(role.Role) && role.Provider == credential.Provider));
        }

        return
        [
            .. dependents
                .OrderBy(agent => agent.CreatedAt)
                .ThenBy(agent => agent.Id)
                .Select(agent => new AgentUse(agent.Id, agent.Name, agent.Status, agent.Status == AgentStatus.Active)),
        ];
    }

    /// <summary>
    /// Cleans and checks what the user sent, then asks Binance about the pair when it's new, and checks that
    /// the exchange key is the user's for the environment. Returns a refusal, or what to store.
    /// </summary>
    private async Task<(AgentSaveResult? Refusal, AgentSettings? Cleaned, string? QuoteAsset, string? ExchangeKey)> PrepareAsync(
        Guid userId, AgentSettings input, AgentRecord? current, CancellationToken cancellationToken)
    {
        var verdict = AgentInput.Check(input, caps);

        if (verdict.Settings is not { } cleaned)
        {
            return (new AgentSaveResult(AgentSaveOutcome.Invalid, current, verdict.Fields, verdict.RiskSettings), null, null, null);
        }

        if (cleaned.Environment != TradingEnvironment.Demo)
        {
            return (new AgentSaveResult(AgentSaveOutcome.LiveNotAvailable, current), null, null, null);
        }

        string? exchangeKey = null;

        if (cleaned.ExchangeCredentialId is { } credentialId)
        {
            var key = await credentials.FindAsync(userId, credentialId, cancellationToken);

            // A key that's gone since the agent chose it can stay, so the agent can still be edited: activation says so.
            var unchanged = current?.Settings.ExchangeCredentialId == credentialId;

            if (key is { Kind: CredentialKind.Exchange } && key.Environment == cleaned.Environment)
            {
                exchangeKey = key.Label;
            }
            else if (!(unchanged && key is null))
            {
                return (new AgentSaveResult(AgentSaveOutcome.Invalid, current, [AgentField.ExchangeCredential]), null, null, null);
            }
        }

        // The pair is checked when it's new. One that stopped trading since doesn't block other edits, and
        // activation checks it again.
        if (current is not null && current.Settings.Market == cleaned.Market && current.Settings.Environment == cleaned.Environment
            && current.Settings.Symbol == cleaned.Symbol)
        {
            return (null, cleaned, current.QuoteAsset, exchangeKey);
        }

        var (symbol, problem) = await checks.FindSymbolAsync(cleaned.Market, cleaned.Environment, cleaned.Symbol, cancellationToken);

        AgentSaveOutcome? outcome = problem switch
        {
            ActivationProblemKind.SymbolUnknown => AgentSaveOutcome.SymbolUnknown,
            ActivationProblemKind.SymbolNotTrading => AgentSaveOutcome.SymbolNotTrading,
            ActivationProblemKind.SymbolNotPerpetual => AgentSaveOutcome.SymbolNotPerpetual,
            ActivationProblemKind.SymbolUnverifiable => AgentSaveOutcome.SymbolUnverifiable,
            _ => null,
        };

        return outcome is not null || symbol is null
            ? (new AgentSaveResult(outcome ?? AgentSaveOutcome.SymbolUnknown, current), null, null, null)
            : (null, cleaned, symbol.QuoteAsset, exchangeKey);
    }

    /// <summary>
    /// A user's change of status. It's conditional on the status and version it read, so of two changes at
    /// once only one happens, and it commits with its audit record (FR-AGT-04).
    /// </summary>
    private async Task<TransitionResult> TransitionAsync(
        Guid userId,
        Guid id,
        AgentStatus[] from,
        AgentStatus to,
        AuditAction action,
        bool checkExposure,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var agent = await FindRowAsync(context, userId, id, cancellationToken);

        if (agent is null)
        {
            return new TransitionResult(TransitionOutcome.NotFound);
        }

        if (!from.Contains(agent.Status))
        {
            return new TransitionResult(TransitionOutcome.NotAllowed, agent.ToRecord());
        }

        if (checkExposure && await exposure.HasOpenPositionsOrOrdersAsync(userId, id, cancellationToken))
        {
            return new TransitionResult(TransitionOutcome.OpenPositions, agent.ToRecord());
        }

        var updated = await context.Set<Agent>()
            .Where(row => row.Id == id && row.UserId == userId && row.Version == agent.Version && row.Status == agent.Status)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, to)
                    .SetProperty(row => row.StatusReason, AgentStatusReason.User)
                    .SetProperty(row => row.StatusChangedAt, now)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);

        if (updated == 0)
        {
            var current = await FindAsync(userId, id, cancellationToken);
            return current is null ? new TransitionResult(TransitionOutcome.NotFound) : new TransitionResult(TransitionOutcome.NotAllowed, current);
        }

        audit.Add(context, AgentAudit.ByUser(action, userId, id, AgentAudit.StatusChange(agent.Status, to, AgentStatusReason.User)));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Agent {AgentId} of user {UserId} went from {From} to {To}", id, userId, agent.Status, to);

        return new TransitionResult(TransitionOutcome.Done, await FindAsync(userId, id, cancellationToken));
    }

    /// <summary>
    /// FR-AGT-13's warning: the capital that the key's Active agents allocate in the pair's quote asset on
    /// this market, against what the account has available. Nothing when the balance isn't known.
    /// </summary>
    private async Task<ActivationWarning?> CapitalWarningAsync(Guid userId, AgentRecord agent, CancellationToken cancellationToken)
    {
        if (agent.Settings.ExchangeCredentialId is not { } credentialId)
        {
            return null;
        }

        decimal allocated;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            allocated = await context.Set<Agent>()
                .Where(row => row.UserId == userId && row.Status == AgentStatus.Active && row.ExchangeCredentialId == credentialId
                    && row.Market == agent.Settings.Market && row.QuoteAsset == agent.QuoteAsset)
                .SumAsync(row => row.CapitalAllocation ?? 0m, cancellationToken);
        }

        var available = await exposure.GetAvailableBalanceAsync(userId, credentialId, agent.Settings.Market, agent.QuoteAsset, cancellationToken);

        return available is { } balance && allocated > balance
            ? new ActivationWarning(ActivationWarningKind.CapitalExceedsBalance, Asset: agent.QuoteAsset, Allocated: allocated, Available: balance)
            : null;
    }

    private async Task<string?> LabelAsync(Guid userId, Guid? credentialId, CancellationToken cancellationToken) =>
        credentialId is { } id ? (await credentials.FindAsync(userId, id, cancellationToken))?.Label : null;

    private async Task<bool> LimitReachedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        await context.Set<Agent>().CountAsync(agent => agent.UserId == userId && agent.Status != AgentStatus.Archived, cancellationToken)
            >= settings.MaxAgentsPerUser;

    private static Task<bool> NameTakenAsync(EcnanibDbContext context, Guid userId, string name, Guid? except, CancellationToken cancellationToken) =>
        context.Set<Agent>().AnyAsync(
            agent => agent.UserId == userId && agent.Id != except && agent.Status != AgentStatus.Archived
                && agent.Name.ToLower() == name.ToLower(),
            cancellationToken);

    /// <summary>The name of another agent holding the settings' exchange key, market and pair (FR-AGT-10), or null.</summary>
    private static async Task<string?> SlotHolderAsync(EcnanibDbContext context, AgentSettings settings, Guid except, CancellationToken cancellationToken)
    {
        if (settings.ExchangeCredentialId is not { } credentialId)
        {
            return null;
        }

        return await context.Set<Agent>()
            .Where(agent => agent.Id != except && agent.ExchangeCredentialId == credentialId && agent.Market == settings.Market
                && agent.Symbol == settings.Symbol && Agent.SlotStatuses.Contains(agent.Status))
            .Select(agent => agent.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Whether a field FR-AGT-03 locks while there are open positions or orders changed. The exchange key too, because they live on its account.</summary>
    private static bool Locked(AgentSettings before, AgentSettings after) =>
        before.Market != after.Market || before.Environment != after.Environment || before.Symbol != after.Symbol
        || before.Timeframe != after.Timeframe || before.ExchangeCredentialId != after.ExchangeCredentialId;

    private static Agent NewDraft(Guid id, Guid userId, AgentSettings settings, string quoteAsset, DateTimeOffset now)
    {
        var agent = new Agent
        {
            Id = id,
            UserId = userId,
            Name = settings.Name,
            Market = settings.Market,
            Environment = settings.Environment,
            Symbol = settings.Symbol,
            QuoteAsset = quoteAsset,
            Timeframe = settings.Timeframe,
            ExecutionMode = settings.ExecutionMode,
            Status = AgentStatus.Draft,
            StatusReason = AgentStatusReason.User,
            StatusChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        agent.Apply(settings, quoteAsset);

        return agent;
    }

    private static IQueryable<Agent> Rows(EcnanibDbContext context, Guid userId) =>
        context.Set<Agent>().AsNoTracking().Include(agent => agent.Roles).Where(agent => agent.UserId == userId);

    private static Task<Agent?> FindRowAsync(EcnanibDbContext context, Guid userId, Guid id, CancellationToken cancellationToken) =>
        Rows(context, userId).SingleOrDefaultAsync(agent => agent.Id == id, cancellationToken);
}
