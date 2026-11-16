using Ecnanib.Accounts;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Keeps the agents of users and their settings (FR-AGT-1, FR-AGT-2). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Every change to the agents of a user is one transaction, which starts by locking the row of
/// the account. So two changes to the agents of one user happen one after the other, and what
/// one of them read under the lock holds until it ends: how many agents the user has, and which
/// names are taken. The deletion of the account takes the stronger lock of a deletion, so no
/// agent is created in an account that is being deleted.
/// </para>
/// <para>
/// The exchange is asked which symbols a market has before the transaction, and never inside
/// it: a call that waits for the exchange would hold the lock for as long.
/// </para>
/// </remarks>
internal sealed class StoredAgents(
    IDbContextFactory<EcnanibDbContext> contexts,
    IFairUse fairUse,
    IExchangeMarketData marketData,
    IEnumerable<IAgentExposure> exposures,
    TradingSettings trading,
    BinanceSettings binance,
    TimeProvider clock,
    ILogger<StoredAgents> logger) : IStoredAgents
{
    public async Task<StoredAgentList> ListAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(agent => agent.CreatedAt)
            .ThenBy(agent => agent.Id)
            .ToListAsync(cancellationToken);

        var views = new List<AgentView>(agents.Count);
        foreach (var agent in agents)
        {
            views.Add(await ViewOfAsync(context, agent, cancellationToken));
        }

        return new StoredAgentList(
            views, await fairUse.MayCreateAgentAsync(userId, agents.Count, cancellationToken), fairUse.MaxAgentsPerUser);
    }

    public async Task<AgentView?> FindAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: an agent of another user is not found, like one that does not exist.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        return agent is null ? null : await ViewOfAsync(context, agent, cancellationToken);
    }

    public async Task<IReadOnlyList<MarketSymbols>> SymbolsAsync(TradingMode mode, CancellationToken cancellationToken)
    {
        var markets = Enum.GetValues<Market>();
        var read = await Task.WhenAll(markets.Select(market => marketData.GetSymbolsAsync(market, mode, cancellationToken)));

        return
        [
            .. markets.Zip(read, (market, symbols) => new MarketSymbols(
                market,
                symbols.Outcome == ExchangeCallOutcome.Succeeded,
                [
                    // Of the file alone, whatever else the market has: an agent is given no other (FR-AGT-6).
                    .. symbols.Symbols
                        .Where(symbol => binance.Symbols.Contains(symbol.Symbol, StringComparer.Ordinal))
                        .Select(symbol => new SymbolChoice(symbol.Symbol, symbol.QuoteAsset)),
                ])),
        ];
    }

    public async Task<SaveAgentResult> CreateAsync(HttpContext request, AgentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // Asked first, before anything that was entered is looked at: a user who may create no
        // agent is told that, and not what is wrong with a form they should not have had.
        await using (var asking = await contexts.CreateDbContextAsync(cancellationToken))
        {
            var owned = await asking.Set<Agent>().OwnedBy(userId).CountAsync(cancellationToken);

            if (await RefusalAsync(userId, owned, cancellationToken) is { } refusal)
            {
                return new SaveAgentResult(refusal);
            }
        }

        var problems = new Dictionary<AgentField, AgentProblem>();
        if (AgentRules.Read(entry, trading.Aggressive, binance.Symbols, problems) is not { } settings)
        {
            return new SaveAgentResult(SaveAgentOutcome.Invalid, Problems: problems);
        }

        // A new agent is in Demo (FR-AGT-7), so its symbol is one that the market has there.
        const TradingMode mode = TradingMode.Demo;

        var (known, quoteAsset) = await QuoteAssetOfAsync(settings.Market, mode, settings.Symbol, cancellationToken);
        if (!known)
        {
            logger.LogInformation(
                "User {UserId} could not create an agent: the exchange did not say which symbols {Market} has in {TradingMode}",
                userId,
                settings.Market,
                mode);

            return new SaveAgentResult(SaveAgentOutcome.SymbolsUnknown);
        }

        if (quoteAsset is null)
        {
            return Invalid(AgentField.Symbol, AgentProblem.NotOnMarket);
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted, on another browser, since this request was signed in.
            return new SaveAgentResult(SaveAgentOutcome.EmailNotVerified);
        }

        // Read under the lock, so that this is what the user has until the transaction ends.
        var names = await context.Set<Agent>()
            .OwnedBy(userId)
            .Select(agent => agent.Name)
            .ToListAsync(cancellationToken);

        // Asked again, with the count that holds now: another browser may have created an agent
        // while the exchange was asked.
        if (await RefusalAsync(userId, names.Count, cancellationToken) is { } late)
        {
            return new SaveAgentResult(late);
        }

        if (names.Contains(settings.Name, StringComparer.Ordinal))
        {
            return Invalid(AgentField.Name, AgentProblem.Taken);
        }

        var now = clock.GetUtcNow();
        var agent = NewAgent(userId, settings.Name, mode, settings.Market, settings.Symbol, quoteAsset, settings.Timeframe,
            settings.AllocatedCapital, settings.Risk, now);

        context.Set<Agent>().Add(agent);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // By ids. The name is what its user typed, and is in no log event.
        logger.LogInformation("User {UserId} created agent {AgentId}", userId, agent.Id);

        return new SaveAgentResult(SaveAgentOutcome.Saved, agent.Id);
    }

    public async Task<SaveAgentResult> SaveAsync(HttpContext request, Guid agentId, AgentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        Agent? before;
        await using (var asking = await contexts.CreateDbContextAsync(cancellationToken))
        {
            before = await asking.Set<Agent>()
                .AsNoTracking()
                .OwnedBy(userId)
                .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);
        }

        if (before is null)
        {
            return new SaveAgentResult(SaveAgentOutcome.NotFound);
        }

        var problems = new Dictionary<AgentField, AgentProblem>();
        if (AgentRules.Read(entry, trading.Aggressive, binance.Symbols, problems) is not { } settings)
        {
            return new SaveAgentResult(SaveAgentOutcome.Invalid, agentId, problems);
        }

        // The exchange is asked only about a market and a symbol that the agent does not have
        // already: what it has was held to its market when it was stored. So a name or a risk
        // setting can be changed while the exchange gives no answer.
        var quoteAsset = before.QuoteAsset;
        if (settings.Market != before.Market || settings.Symbol != before.Symbol)
        {
            var (known, quoteAssetThere) = await QuoteAssetOfAsync(settings.Market, before.Mode, settings.Symbol, cancellationToken);
            if (!known)
            {
                logger.LogInformation(
                    "User {UserId} could not change agent {AgentId}: the exchange did not say which symbols {Market} has in {TradingMode}",
                    userId,
                    agentId,
                    settings.Market,
                    before.Mode);

                return new SaveAgentResult(SaveAgentOutcome.SymbolsUnknown, agentId);
            }

            if (quoteAssetThere is null)
            {
                return Invalid(AgentField.Symbol, AgentProblem.NotOnMarket, agentId);
            }

            quoteAsset = quoteAssetThere;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, userId, cancellationToken))
        {
            return new SaveAgentResult(SaveAgentOutcome.NotFound);
        }

        // Read again under the lock: this is the agent until the transaction ends.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return new SaveAgentResult(SaveAgentOutcome.NotFound);
        }

        // The market, the symbol, and the timeframe change only while the agent is in Draft, or
        // is Stopped with no open position or orders (FR-AGT-8). Asked under the lock, of the
        // state that the agent has now.
        var structureChanges = settings.Market != agent.Market
            || settings.Symbol != agent.Symbol
            || settings.Timeframe != agent.Timeframe;

        if (structureChanges && !await StructureCanChangeAsync(context, agent, cancellationToken))
        {
            logger.LogInformation(
                "User {UserId} was refused changing the market, symbol, or timeframe of agent {AgentId}, which is {AgentState}",
                userId,
                agentId,
                agent.State);

            return new SaveAgentResult(SaveAgentOutcome.StructureLocked, agentId);
        }

        var taken = await context.Set<Agent>()
            .OwnedBy(userId)
            .AnyAsync(other => other.Id != agentId && other.Name == settings.Name, cancellationToken);

        if (taken)
        {
            return Invalid(AgentField.Name, AgentProblem.Taken, agentId);
        }

        await context.Set<Agent>()
            .OwnedBy(userId)
            .Where(stored => stored.Id == agentId)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Name, settings.Name)
                    .SetProperty(stored => stored.Market, settings.Market)
                    .SetProperty(stored => stored.Symbol, settings.Symbol)
                    .SetProperty(stored => stored.QuoteAsset, quoteAsset)
                    .SetProperty(stored => stored.Timeframe, settings.Timeframe)
                    .SetProperty(stored => stored.AllocatedCapital, settings.AllocatedCapital)
                    .SetProperty(stored => stored.RiskPerTradePercent, settings.Risk.RiskPerTradePercent)
                    .SetProperty(stored => stored.LeverageCap, settings.Risk.LeverageCap)
                    .SetProperty(stored => stored.DailyLossPercent, settings.Risk.DailyLossPercent)
                    .SetProperty(stored => stored.DrawdownPercent, settings.Risk.DrawdownPercent)
                    .SetProperty(stored => stored.MaxEntryDistancePercent, settings.Risk.MaxEntryDistancePercent),
                cancellationToken);

        // A change to a risk setting is money-relevant, and is recorded with the change
        // (NFR-LOG-7). The event names the agent by its id, and holds no number.
        var riskChanged = settings.Risk != RiskOf(agent);
        if (riskChanged)
        {
            await AuditTrail.RecordAsync(
                context,
                userId,
                AuditAction.AgentRiskSettingsChanged,
                AuditTarget.Agent(agentId),
                clock.GetUtcNow(),
                AuditOrigin.Of(request),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (riskChanged)
        {
            logger.LogInformation("User {UserId} changed the risk settings of agent {AgentId}", userId, agentId);
        }
        else
        {
            logger.LogInformation("User {UserId} changed the settings of agent {AgentId}", userId, agentId);
        }

        return new SaveAgentResult(SaveAgentOutcome.Saved, agentId);
    }

    public async Task<SaveAgentResult> CloneAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, userId, cancellationToken))
        {
            return new SaveAgentResult(SaveAgentOutcome.NotFound);
        }

        var source = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        if (source is null)
        {
            return new SaveAgentResult(SaveAgentOutcome.NotFound);
        }

        var names = await context.Set<Agent>()
            .OwnedBy(userId)
            .Select(agent => agent.Name)
            .ToListAsync(cancellationToken);

        // A copy is one more agent, and counts as one (FR-ACC-7).
        if (await RefusalAsync(userId, names.Count, cancellationToken) is { } refusal)
        {
            return new SaveAgentResult(refusal);
        }

        // The settings of the agent, under a name of its own. A copy is a new agent: it is in
        // Demo and in Draft, whatever the agent it was made from is (FR-AGT-7).
        var now = clock.GetUtcNow();
        var copy = NewAgent(userId, AgentRules.CopyNameOf(source.Name, names), TradingMode.Demo, source.Market, source.Symbol,
            source.QuoteAsset, source.Timeframe, source.AllocatedCapital, RiskOf(source), now);

        context.Set<Agent>().Add(copy);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} created agent {AgentId} as a copy of agent {SourceAgentId}", userId, copy.Id, agentId);

        return new SaveAgentResult(SaveAgentOutcome.Saved, copy.Id);
    }

    public async Task<DeleteAgentOutcome> DeleteAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted, and its agents with it.
            return DeleteAgentOutcome.Deleted;
        }

        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        if (agent is null)
        {
            // Nothing is there: the form was sent twice, or sent from two browsers. It is what
            // was asked for.
            return DeleteAgentOutcome.Deleted;
        }

        if (AgentStates.IsActive(agent.State))
        {
            logger.LogInformation(
                "User {UserId} was refused deleting agent {AgentId}, which is {AgentState}", userId, agentId, agent.State);

            return DeleteAgentOutcome.NotStopped;
        }

        if (!await StructureCanChangeAsync(context, agent, cancellationToken))
        {
            logger.LogInformation(
                "User {UserId} was refused deleting agent {AgentId}, which has an open position or open orders", userId, agentId);

            return DeleteAgentOutcome.HoldsPosition;
        }

        await context.Set<Agent>()
            .OwnedBy(userId)
            .Where(stored => stored.Id == agentId)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} deleted agent {AgentId}", userId, agentId);

        return DeleteAgentOutcome.Deleted;
    }

    /// <summary>The risk settings that a row holds.</summary>
    private static AgentRisk RiskOf(Agent agent) =>
        new(agent.RiskPerTradePercent, agent.LeverageCap, agent.DailyLossPercent, agent.DrawdownPercent, agent.MaxEntryDistancePercent);

    /// <summary>A new agent: in Draft, because its user created it.</summary>
    private static Agent NewAgent(
        Guid userId,
        string name,
        TradingMode mode,
        Market market,
        string symbol,
        string quoteAsset,
        Timeframe timeframe,
        decimal allocatedCapital,
        AgentRisk risk,
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            userId,
            name,
            mode,
            market,
            symbol,
            quoteAsset,
            timeframe,
            allocatedCapital,
            risk.RiskPerTradePercent,
            risk.LeverageCap,
            risk.DailyLossPercent,
            risk.DrawdownPercent,
            risk.MaxEntryDistancePercent,
            AgentState.Draft,
            AgentStateCause.Created,
            now,
            now);

    private static SaveAgentResult Invalid(AgentField field, AgentProblem problem, Guid? agentId = null) =>
        new(SaveAgentOutcome.Invalid, agentId, new Dictionary<AgentField, AgentProblem> { [field] = problem });

    /// <summary>
    /// Locks the row of an account for the rest of the transaction, as the first thing the
    /// transaction asks of it and with the lock of a change, as what changes a key of the user
    /// does. The deletion of the account takes the stronger lock of a deletion.
    /// </summary>
    /// <returns>Whether the account exists.</returns>
    private static async Task<bool> LockAccountAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var locked = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id = {userId}
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        return locked.Count > 0;
    }

    /// <summary>What keeps a user from creating one more agent, or null when nothing does (FR-ACC-1, FR-ACC-7).</summary>
    private async Task<SaveAgentOutcome?> RefusalAsync(Guid userId, int agentsOwned, CancellationToken cancellationToken)
    {
        switch (await fairUse.MayCreateAgentAsync(userId, agentsOwned, cancellationToken))
        {
            case AgentCreation.Allowed:
                return null;

            case AgentCreation.EmailNotVerified:
                logger.LogInformation("User {UserId} was refused creating an agent: their email address is not verified", userId);

                return SaveAgentOutcome.EmailNotVerified;

            default:
                logger.LogInformation(
                    "User {UserId} was refused creating an agent: they have {AgentCount} of {MaxAgentsPerUser} already",
                    userId,
                    agentsOwned,
                    fairUse.MaxAgentsPerUser);

                return SaveAgentOutcome.AgentLimitReached;
        }
    }

    /// <summary>
    /// What a symbol is priced in on a market, as the exchange says: the answer also says
    /// whether the market has the symbol at all (FR-AGT-6).
    /// </summary>
    /// <returns>
    /// Whether the exchange said which symbols the market has, and the quote asset of the
    /// symbol, which is null when the market does not have it.
    /// </returns>
    private async Task<(bool Known, string? QuoteAsset)> QuoteAssetOfAsync(
        Market market, TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        var symbols = await marketData.GetSymbolsAsync(market, mode, cancellationToken);

        return symbols.Outcome == ExchangeCallOutcome.Succeeded
            ? (true, symbols.Symbols.FirstOrDefault(listed => string.Equals(listed.Symbol, symbol, StringComparison.Ordinal))?.QuoteAsset)
            : (false, null);
    }

    private async Task<AgentView> ViewOfAsync(EcnanibDbContext context, Agent agent, CancellationToken cancellationToken) =>
        new(
            agent.Id,
            agent.Name,
            agent.Mode,
            agent.Market,
            agent.Symbol,
            agent.QuoteAsset,
            agent.Timeframe,
            agent.AllocatedCapital,
            RiskOf(agent),
            agent.State,
            agent.StateChangedAt,
            agent.CreatedAt,
            await StructureCanChangeAsync(context, agent, cancellationToken));

    /// <summary>
    /// Whether the market, symbol, and timeframe of an agent can be changed, and the agent
    /// deleted (FR-AGT-8). What an agent holds on the exchange is asked only of a stopped one:
    /// a draft never held anything, and an active agent is refused whatever it holds.
    /// </summary>
    private async Task<bool> StructureCanChangeAsync(EcnanibDbContext context, Agent agent, CancellationToken cancellationToken)
    {
        var holds = false;

        if (agent.State == AgentState.Stopped)
        {
            foreach (var exposure in exposures)
            {
                if (await exposure.HasOpenPositionOrOrdersAsync(context, agent.Id, cancellationToken))
                {
                    holds = true;

                    break;
                }
            }
        }

        return AgentStates.StructureCanChange(agent.State, holds);
    }
}

/// <summary>
/// Says whether a user has an agent that is not stopped, for Accounts, which deletes an account
/// only while none is (FR-ACC-9). An agent that is in Draft was never started, and stands in the
/// way no more than a stopped one: what counts is an agent that is active.
/// </summary>
internal sealed class AgentActivity : IAgentActivity
{
    public async Task<bool> HasAgentsNotStoppedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // The rows are locked for the rest of the transaction of whoever asks, in the order of
        // their ids: an agent that is started at the same moment is seen as active here, or
        // waits and finds its account gone.
        var states = await context.Database
            .SqlQuery<string>(
                $"""
                SELECT state AS "Value" FROM agents
                WHERE user_id = {userId}
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        return states.Any(state => AgentStates.IsActive(Enum.Parse<AgentState>(state)));
    }
}

/// <summary>An agent in the export of a user's data (NFR-SEC-9).</summary>
/// <param name="Id">The id that the audit trail names the agent by.</param>
/// <param name="Name">The name its user gave it.</param>
/// <param name="Mode">The environment it trades in.</param>
/// <param name="Market">The market it trades on.</param>
/// <param name="Symbol">The symbol it trades.</param>
/// <param name="Timeframe">The code of the candle length it works on, such as <c>1H</c>.</param>
/// <param name="AllocatedCapital">The capital allocated to it.</param>
/// <param name="QuoteAsset">The asset that the capital is in.</param>
/// <param name="Risk">Its risk settings.</param>
/// <param name="State">Its state.</param>
/// <param name="StateCause">Why it is in that state.</param>
/// <param name="StateChangedAt">Since when it is.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record ExportedAgent(
    Guid Id,
    string Name,
    TradingMode Mode,
    Market Market,
    string Symbol,
    string Timeframe,
    decimal AllocatedCapital,
    string QuoteAsset,
    AgentRisk Risk,
    AgentState State,
    AgentStateCause StateCause,
    DateTimeOffset StateChangedAt,
    DateTimeOffset CreatedAt);

/// <summary>The agents of a user, for the export of their data (NFR-SEC-9).</summary>
internal sealed class AgentsExport : IExportedPart
{
    public string Section => "agents";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(agent => agent.CreatedAt)
            .ThenBy(agent => agent.Id)
            .ToListAsync(cancellationToken);

        return
        [
            .. agents.Select(agent => new ExportedAgent(
                agent.Id,
                agent.Name,
                agent.Mode,
                agent.Market,
                agent.Symbol,
                agent.Timeframe.Code(),
                agent.AllocatedCapital,
                agent.QuoteAsset,
                new AgentRisk(
                    agent.RiskPerTradePercent,
                    agent.LeverageCap,
                    agent.DailyLossPercent,
                    agent.DrawdownPercent,
                    agent.MaxEntryDistancePercent),
                agent.State,
                agent.StateCause,
                agent.StateChangedAt,
                agent.CreatedAt)),
        ];
    }
}
