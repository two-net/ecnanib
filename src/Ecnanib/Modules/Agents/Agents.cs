using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// A user's agents and their lifecycle (FR-AGT-01 to FR-AGT-05, FR-AGT-10, FR-AGT-12, UC-04). Every query filters on
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
    ITwoFactor twoFactor,
    IEmailVerification emailVerification,
    TimeProvider time,
    AgentObservers observers,
    IServiceProvider services,
    ILogger<Agents> logger) : IAgents
{
    /// <summary>
    /// The modules that keep an agent's history. Resolved on first use, because they sit above Agents and use its
    /// contracts, so a constructor parameter would make a cycle.
    /// </summary>
    private readonly Lazy<IReadOnlyList<IAgentHistory>> _history = new(() => [.. services.GetServices<IAgentHistory>()]);

    /// <summary>Execution's side of the kill switch, resolved on first use for the same reason.</summary>
    private readonly Lazy<IAgentLiquidation> _liquidation = new(services.GetRequiredService<IAgentLiquidation>);

    private static readonly AgentStatus[] Activatable = [AgentStatus.Draft, AgentStatus.Paused, AgentStatus.Stopped, AgentStatus.Error];
    private static readonly AgentStatus[] Pausable = [AgentStatus.Active];
    private static readonly AgentStatus[] Stoppable = [AgentStatus.Active, AgentStatus.Paused, AgentStatus.Error];

    /// <summary>A Stopped agent too, for what it still holds. Draft and Archived agents can have nothing on the exchange.</summary>
    private static readonly AgentStatus[] Killable = [AgentStatus.Active, AgentStatus.Paused, AgentStatus.Error, AgentStatus.Stopped];
    private static readonly AgentStatus[] Archivable = [AgentStatus.Draft, AgentStatus.Paused, AgentStatus.Stopped, AgentStatus.Error];
    private static readonly AgentStatus[] Deletable = [AgentStatus.Archived];

    /// <summary>UC-04's: an agent that isn't running, and that the user stopped or paused, rather than a Draft that never ran in Demo.</summary>
    private static readonly AgentStatus[] Switchable = [AgentStatus.Paused, AgentStatus.Stopped];

    public int MaxAgentsPerUser => settings.MaxAgentsPerUser;

    public bool TwoFactorRequiredForLive => settings.RequireTwoFactorForLive;

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

    public AgentInputCheck Check(AgentSettings settings)
    {
        var verdict = AgentInput.Check(settings, caps);

        return new AgentInputCheck(verdict.Fields, verdict.RiskSettings);
    }

    public Task<PairChoices> ListPairsAsync(Market market, CancellationToken cancellationToken) =>
        checks.ListPairsAsync(market, settings.DefaultEnvironment, cancellationToken);

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
        observers.Changed(userId, id);

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
        observers.Changed(userId, id);

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

        // The same settings and none of the history (FR-AGT-12). Only switching makes an agent Live (UC-04), so a Live
        // agent's clone starts in Demo, without the Live key, which a Demo agent can't use.
        var copy = source.Settings with { Name = cleaned };

        if (copy.Environment != settings.DefaultEnvironment)
        {
            copy = copy with { Environment = settings.DefaultEnvironment, ExchangeCredentialId = null };
        }

        var clone = NewDraft(cloneId, userId, copy, source.QuoteAsset, now);
        context.Set<Agent>().Add(clone);
        context.Set<AgentRoleAssignment>().AddRange(copy.Roles.Select(role => AgentRoleAssignment.Of(cloneId, role)));

        var changes = AgentAudit.Differences(
            null, AgentAudit.Describe(copy, source.QuoteAsset, await LabelAsync(userId, copy.ExchangeCredentialId, cancellationToken)));
        changes.Add(new AuditChange(AgentAuditFields.ClonedFrom, null, source.Name));
        audit.Add(context, AgentAudit.ByUser(AuditAction.AgentCreated, userId, cloneId, changes));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        observers.Changed(userId, cloneId);

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

        if (LossLimitProblem(before, now) is { } pause)
        {
            problems.Insert(0, pause);
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
            observers.Changed(userId, id);
        }

        logger.LogInformation("Activated agent {AgentId} for user {UserId}, from {Status}, because {Reason}", id, userId, before.Status, reason);

        var activated = await FindAsync(userId, id, cancellationToken);

        // Nobody sees a warning when the system resumes the agent, so Binance isn't asked for one.
        if (bySystem)
        {
            return new ActivationResult(ActivationOutcome.Activated, activated, [], warnings);
        }

        await AddCapitalWarningAsync(userId, activated, warnings, cancellationToken);

        return new ActivationResult(ActivationOutcome.Activated, activated, [], warnings);
    }

    public async Task<LiveSwitchResult> SwitchToLiveAsync(Guid userId, Guid id, LiveSwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var before = await FindAsync(userId, id, cancellationToken);

        if (before is null)
        {
            return new LiveSwitchResult(LiveSwitchOutcome.NotFound);
        }

        if (!before.IsDemo || !Switchable.Contains(before.Status))
        {
            return new LiveSwitchResult(LiveSwitchOutcome.NotAllowed, before);
        }

        // The user agreed to what the page showed, so anything that changed since needs showing again.
        if (before.Version != request.Version || before.Settings.ExecutionMode != request.ExecutionMode
            || request.RiskDisclosureVersion != LegalDocuments.RiskDisclosureVersion)
        {
            return new LiveSwitchResult(LiveSwitchOutcome.Changed, before);
        }

        // UC-04 starts from an agent with nothing open, so nothing it holds came from Demo's account.
        if (await exposure.HasOpenPositionsOrOrdersAsync(userId, id, cancellationToken))
        {
            return new LiveSwitchResult(LiveSwitchOutcome.OpenPositions, before);
        }

        var live = before.Settings with { Environment = TradingEnvironment.Live, ExchangeCredentialId = request.ExchangeCredentialId };
        var now = time.GetUtcNow();

        // Everything that can refuse comes before the code, so a refusal doesn't use it up. The activation checks can call
        // Binance, so these run before the transaction, which is conditional on the version they checked.
        var problems = new List<ActivationProblem>();

        if (!request.RiskDisclosureAcknowledged)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.RiskDisclosureNotAcknowledged));
        }

        if (!request.ExecutionModeAcknowledged)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.ExecutionModeNotAcknowledged));
        }

        if (!await emailVerification.IsSatisfiedAsync(userId, cancellationToken))
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.EmailNotVerified));
        }

        if (settings.RequireTwoFactorForLive && !await twoFactor.IsEnabledAsync(userId, cancellationToken))
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.TwoFactorRequired));
        }

        if (LossLimitProblem(before, now) is { } pause)
        {
            problems.Add(pause);
        }

        var (checkProblems, warnings) = await checks.CheckAsync(userId, live, cancellationToken);
        problems.AddRange(checkProblems);

        // The capital is an amount in the quote asset, which is stored from the pair as the Live exchange lists it.
        string? quoteAsset = null;

        if (checkProblems.Count == 0)
        {
            var (symbol, symbolProblem) = await checks.FindSymbolAsync(live.Market, live.Environment, live.Symbol, cancellationToken);
            quoteAsset = symbol?.QuoteAsset;

            if (quoteAsset is null)
            {
                problems.Add(new ActivationProblem(symbolProblem ?? ActivationProblemKind.SymbolUnverifiable));
            }
        }

        // Without the lock, so the code isn't spent on a slot that's plainly taken. It's checked again under the lock.
        await using (var reading = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await SlotHolderAsync(reading, live, except: id, cancellationToken) is { } taken)
            {
                problems.Add(new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: taken));
            }
        }

        if (problems.Count > 0)
        {
            logger.LogInformation(
                "Refused to switch agent {AgentId} of user {UserId} to Live: {Problems}", id, userId, problems.Select(problem => problem.Kind));
            return new LiveSwitchResult(LiveSwitchOutcome.Refused, before, problems, warnings);
        }

        // Before anything changes, as every sensitive action does (FR-AUTH-05).
        var check = await twoFactor.CheckAsync(userId, request.TwoFactorCode, cancellationToken);

        if (!check.Allows())
        {
            return new LiveSwitchResult(LiveSwitchOutcome.TwoFactorRefused, before, TwoFactor: check);
        }

        var demoKey = await LabelAsync(userId, before.Settings.ExchangeCredentialId, cancellationToken);
        var liveKey = await LabelAsync(userId, live.ExchangeCredentialId, cancellationToken);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await AgentLock.TakeAsync(context, userId, cancellationToken);

            if (await SlotHolderAsync(context, live, except: id, cancellationToken) is { } holder)
            {
                return new LiveSwitchResult(
                    LiveSwitchOutcome.Refused, before, [new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: holder)], warnings);
            }

            // Execution sends nothing for an agent that isn't Active, so this only finds what reconciliation found meanwhile.
            if (await exposure.HasOpenPositionsOrOrdersAsync(userId, id, cancellationToken))
            {
                return new LiveSwitchResult(LiveSwitchOutcome.OpenPositions, before);
            }

            var updated = await context.Set<Agent>()
                .Where(agent => agent.Id == id && agent.UserId == userId && agent.Version == before.Version && agent.Status == before.Status
                    && agent.Environment == TradingEnvironment.Demo)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(agent => agent.Environment, TradingEnvironment.Live)
                        .SetProperty(agent => agent.ExchangeCredentialId, live.ExchangeCredentialId)
                        .SetProperty(agent => agent.QuoteAsset, quoteAsset!)
                        .SetProperty(agent => agent.Status, AgentStatus.Active)
                        .SetProperty(agent => agent.StatusReason, AgentStatusReason.User)
                        .SetProperty(agent => agent.StatusChangedAt, now)
                        .SetProperty(agent => agent.UpdatedAt, now)
                        .SetProperty(agent => agent.LossLimitUntil, (DateTimeOffset?)null)
                        .SetProperty(agent => agent.Version, agent => agent.Version + 1),
                    cancellationToken);

            if (updated == 0)
            {
                return new LiveSwitchResult(LiveSwitchOutcome.Changed, await FindAsync(userId, id, cancellationToken));
            }

            // FR-AUD-01 names switches to Live, and activations, so each has its record, as an edit's execution mode does.
            audit.Add(
                context,
                AgentAudit.ByUser(
                    AuditAction.AgentSwitchedToLive,
                    userId,
                    id,
                    [
                        new(AgentAuditFields.Environment, before.Settings.Environment.ToString(), live.Environment.ToString()),
                        new(AgentAuditFields.ExchangeKey, demoKey, liveKey),
                        new(AgentAuditFields.AcknowledgedRiskDisclosure, null, request.RiskDisclosureVersion),
                        new(AgentAuditFields.AcknowledgedExecutionMode, null, request.ExecutionMode.ToString()),
                    ]));
            audit.Add(
                context,
                AgentAudit.ByUser(AuditAction.AgentActivated, userId, id, AgentAudit.StatusChange(before.Status, AgentStatus.Active, AgentStatusReason.User)));

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            observers.Changed(userId, id);
        }

        logger.LogInformation("Switched agent {AgentId} of user {UserId} to Live, from {Status}", id, userId, before.Status);

        var switched = await FindAsync(userId, id, cancellationToken);
        await AddCapitalWarningAsync(userId, switched, warnings, cancellationToken);

        return new LiveSwitchResult(LiveSwitchOutcome.Switched, switched, [], warnings);
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
        observers.Changed(userId, id);

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
        observers.Changed(userId, id);

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
            observers.Changed(userId, id);

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

    public async Task<KillResult> StopAndCloseAsync(Guid userId, Guid id, string? twoFactorCode, CancellationToken cancellationToken)
    {
        // Before anything changes, as every sensitive action does (FR-AUTH-05).
        var check = await twoFactor.CheckAsync(userId, twoFactorCode, cancellationToken);

        if (!check.Allows())
        {
            return new KillResult(KillOutcome.TwoFactorRefused, TwoFactor: check);
        }

        return await KillAsync(userId, id, cancellationToken);
    }

    public async Task<StopAllResult> StopAllAsync(Guid userId, string? twoFactorCode, CancellationToken cancellationToken)
    {
        // Once for them all: a code works only once, so checking it per agent would refuse every one after the first.
        var check = await twoFactor.CheckAsync(userId, twoFactorCode, cancellationToken);

        if (!check.Allows())
        {
            return new StopAllResult(check, []);
        }

        var agents = await ListAsync(userId, includeArchived: false, cancellationToken);
        var results = new List<AgentKill>();

        logger.LogWarning("User {UserId} asked to stop all their agents", userId);

        foreach (var agent in agents)
        {
            if (!Killable.Contains(agent.Status))
            {
                continue;
            }

            try
            {
                // A Stopped agent that holds nothing has nothing to stop.
                if (agent.Status == AgentStatus.Stopped && !await exposure.HasOpenPositionsOrOrdersAsync(userId, agent.Id, cancellationToken))
                {
                    continue;
                }

                results.Add(new AgentKill(agent.Id, agent.Settings.Name, await KillAsync(userId, agent.Id, cancellationToken)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One agent's failure never keeps the rest running (NFR-REL-05). If it stopped, execution finishes closing it.
                logger.LogError(exception, "Stopping all agents of user {UserId} failed for agent {AgentId}, and goes on with the rest", userId, agent.Id);

                var current = await TryFindAsync(userId, agent.Id);
                var outcome = current is { Status: AgentStatus.Stopped, StatusReason: AgentStatusReason.KillSwitch } ? KillOutcome.Unconfirmed : KillOutcome.Failed;
                results.Add(new AgentKill(agent.Id, agent.Settings.Name, new KillResult(outcome, current)));
            }
        }

        return new StopAllResult(check, results);
    }

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
        observers.Changed(userId, id);

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

        // A new agent starts in Demo, and an edit keeps the environment: only switching makes an agent Live (UC-04).
        if (cleaned.Environment != (current?.Settings.Environment ?? settings.DefaultEnvironment))
        {
            return (new AgentSaveResult(AgentSaveOutcome.EnvironmentNotEditable, current), null, null, null);
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

    /// <summary>The agent as it is now, for a report after a failure, or null when even that fails.</summary>
    private async Task<AgentRecord?> TryFindAsync(Guid userId, Guid id)
    {
        try
        {
            return await FindAsync(userId, id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Couldn't read agent {AgentId} of user {UserId} again", id, userId);
            return null;
        }
    }

    /// <summary>
    /// One agent's kill switch, once the code has passed. It stops the agent with <see cref="AgentStatusReason.KillSwitch"/>,
    /// with its audit record and notice, in one transaction, before anything is sent: from then on no cycle runs, and
    /// execution sends nothing for it but cancels and closes, so the close can't race a cycle, and a restart finishes
    /// it. Then execution closes it.
    /// </summary>
    /// <param name="again">It changed under the first try, so this is the second, and the last.</param>
    private async Task<KillResult> KillAsync(Guid userId, Guid id, CancellationToken cancellationToken, bool again = false)
    {
        var now = time.GetUtcNow();
        const AgentStatusReason reason = AgentStatusReason.KillSwitch;
        bool held;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var agent = await FindRowAsync(context, userId, id, cancellationToken);

            if (agent is null)
            {
                return new KillResult(KillOutcome.NotFound);
            }

            if (!Killable.Contains(agent.Status))
            {
                return new KillResult(KillOutcome.NotAllowed, agent.ToRecord());
            }

            // From execution's records, before anything can close it: once it's stopped, the agent's watch or a sweep may
            // close everything before execution is asked to, which must still read as closed, not as nothing open.
            held = await exposure.HasOpenPositionsOrOrdersAsync(userId, id, cancellationToken);

            var updated = await context.Set<Agent>()
                .Where(row => row.Id == id && row.UserId == userId && row.Version == agent.Version && row.Status == agent.Status)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, AgentStatus.Stopped)
                        .SetProperty(row => row.StatusReason, reason)
                        .SetProperty(row => row.StatusChangedAt, now)
                        .SetProperty(row => row.Version, row => row.Version + 1),
                    cancellationToken);

            if (updated == 0)
            {
                // It changed meanwhile, such as by a system pause. Read it again and try once more: the kill switch
                // shouldn't fail on a race the user can't see.
                await transaction.RollbackAsync(cancellationToken);

                return again
                    ? new KillResult(KillOutcome.NotAllowed, await FindAsync(userId, id, cancellationToken))
                    : await KillAsync(userId, id, cancellationToken, again: true);
            }

            audit.Add(context, AgentAudit.ByUser(AuditAction.AgentKillSwitch, userId, id, AgentAudit.StatusChange(agent.Status, AgentStatus.Stopped, reason)));
            notifications.Add(
                context,
                new Notice(NotificationType.KillSwitchExecuted, userId, $"/agents/{id}/settings", agent.Name, agent.Environment == TradingEnvironment.Demo));

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            observers.Changed(userId, id);

            logger.LogWarning("Used the kill switch on agent {AgentId} of user {UserId}, which was {From}", id, userId, agent.Status);
        }

        LiquidationOutcome closed;

        try
        {
            closed = await _liquidation.Value.CloseAllAsync(userId, id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The agent is stopped by the kill switch, which reconciliation acts on at its next pass.
            logger.LogError(exception, "Closing agent {AgentId} of user {UserId} after its kill switch failed, and is left to reconciliation", id, userId);
            closed = LiquidationOutcome.Unconfirmed;
        }

        var outcome = closed switch
        {
            LiquidationOutcome.NothingOpen => held ? KillOutcome.Closed : KillOutcome.NothingOpen,
            LiquidationOutcome.Closed => KillOutcome.Closed,
            LiquidationOutcome.Closing => KillOutcome.Closing,
            _ => KillOutcome.Unconfirmed,
        };

        return new KillResult(outcome, await FindAsync(userId, id, CancellationToken.None));
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
        observers.Changed(userId, id);

        logger.LogInformation("Agent {AgentId} of user {UserId} went from {From} to {To}", id, userId, agent.Status, to);

        return new TransitionResult(TransitionOutcome.Done, await FindAsync(userId, id, cancellationToken));
    }

    /// <summary>
    /// A daily or weekly loss limit's pause that hasn't ended, or null. It lasts its whole period, whatever the status, so
    /// stopping the agent, or switching it to Live, can't end it early (FR-RISK-08).
    /// </summary>
    private static ActivationProblem? LossLimitProblem(AgentRecord agent, DateTimeOffset now) =>
        agent.LossLimitUntil is { } until && until > now ? new ActivationProblem(ActivationProblemKind.LossLimitPause, Until: until) : null;

    /// <summary>
    /// Adds FR-AGT-13's warning for an agent that just became Active, when there is one. The agent is active whatever
    /// happens here, so a failure only loses a warning the SRS says "should" give.
    /// </summary>
    private async Task AddCapitalWarningAsync(Guid userId, AgentRecord? agent, List<ActivationWarning> warnings, CancellationToken cancellationToken)
    {
        if (agent is null)
        {
            return;
        }

        try
        {
            if (await CapitalWarningAsync(userId, agent, cancellationToken) is { } warning)
            {
                warnings.Add(warning);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Checking the capital of agent {AgentId} against its account failed", agent.Id);
        }
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
