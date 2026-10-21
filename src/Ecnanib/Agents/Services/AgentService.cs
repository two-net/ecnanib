using System.Collections.Concurrent;
using System.Text.Json;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Services;

/// <summary>
/// Creation, editing with change history and the Appendix E user actions (FR-AGT-01..07, FR-AGT-15). Every write
/// runs under the agent's runtime gate, so no evaluation is mid-flight while the state changes; creation and start also
/// run under a per-user gate because the fair-use counts are read before they are acted on.
/// </summary>
internal sealed class AgentService : IAgentService
{
    internal const string PausedByUser = "Paused by the user.";

    internal const string StoppedByUser = "Stopped by the user.";

    private const int RecentEvaluations = 20;

    private const int RecentChanges = 50;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IStrategyCatalog _catalog;
    private readonly IFairUseLimits _limits;
    private readonly IAccountService _accounts;
    private readonly IKeyResolver _keys;
    private readonly ISymbolCatalog _symbols;
    private readonly IOrderVenues _venues;
    private readonly IAgentRuntime _runtime;
    private readonly IAgentEvents _events;
    private readonly ICouncilSuggestions _suggestions;
    private readonly IOptions<RiskCapsOptions> _caps;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userGates = new();

    public AgentService(
        IDbContextFactory<EcnanibDbContext> factory,
        IStrategyCatalog catalog,
        IFairUseLimits limits,
        IAccountService accounts,
        IKeyResolver keys,
        ISymbolCatalog symbols,
        IOrderVenues venues,
        IAgentRuntime runtime,
        IAgentEvents events,
        ICouncilSuggestions suggestions,
        IOptions<RiskCapsOptions> caps,
        TimeProvider time,
        ILogger<AgentService> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _limits = limits;
        _accounts = accounts;
        _keys = keys;
        _symbols = symbols;
        _venues = venues;
        _runtime = runtime;
        _events = events;
        _suggestions = suggestions;
        _caps = caps;
        _time = time;
        _logger = logger;
    }

    // --- Reads ----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AgentSummary>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Agents.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Name).ThenBy(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.Name, a.MarketType, a.Symbol, a.Timeframe, a.StrategyId, a.StrategyVersion, a.Mode, a.State, a.StateReason, a.UpdatedAt,
                Last = db.Evaluations.Where(e => e.AgentId == a.Id && e.FinishedAt != null)
                    .OrderByDescending(e => e.CandleOpenTime)
                    .Select(e => new { e.FinishedAt, e.Outcome })
                    .FirstOrDefault(),
                OpenPositions = db.Trades.Count(t => t.AgentId == a.Id && t.ClosedAt == null),
                HasDeprecatedModel = a.CouncilAssignments.Any(c => c.Enabled
                    && db.ProviderModels.Any(m => m.Provider == c.Provider && m.ModelId == c.Model && m.Status == ModelStatus.Deprecated)),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new AgentSummary(r.Id, r.Name, r.MarketType, r.Symbol, r.Timeframe, r.StrategyId,
                (_catalog.Find(r.StrategyId, r.StrategyVersion) ?? _catalog.Find(r.StrategyId))?.Name ?? r.StrategyId, r.Mode, r.State, r.StateReason,
                r.Last?.FinishedAt, r.Last?.Outcome, r.OpenPositions, r.UpdatedAt, r.HasDeprecatedModel))
            .ToList();
    }

    public async Task<AgentDetail?> GetAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.AsNoTracking().Include(a => a.CouncilAssignments).Include(a => a.EntryWindows).AsSplitQuery()
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return null;

        var trades = await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId && t.ClosedAt == null).OrderBy(t => t.OpenedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.AgentId == agentId && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .OrderBy(o => o.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var evaluations = await db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId)
            .OrderByDescending(e => e.CandleOpenTime).Take(RecentEvaluations)
            .Select(e => new EvaluationView(e.Id, e.CandleOpenTime, e.StartedAt, e.FinishedAt, e.Outcome, e.OutcomeDetail, e.CouncilConsulted,
                e.Decision != null ? e.Decision.FinalAction : (DecisionAction?)null))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = await db.AgentChanges.AsNoTracking().Where(c => c.AgentId == agentId)
            .OrderByDescending(c => c.Timestamp).ThenByDescending(c => c.Id).Take(RecentChanges)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // FR-SYNC-03: each role's model is looked up in the catalog so the page can warn about a deprecated one.
        var assignments = AgentRules.Roles.Select(role => agent.CouncilAssignments.FirstOrDefault(c => c.Role == role))
            .Where(c => c is not null).Select(c => c!).ToList();
        var providers = assignments.Select(c => c.Provider).Distinct().ToList();
        var models = assignments.Select(c => c.Model).Distinct().ToList();
        var catalog = await db.ProviderModels.AsNoTracking()
            .Where(m => providers.Contains(m.Provider) && models.Contains(m.ModelId))
            .Select(m => new { m.Provider, m.ModelId, m.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var closing = orders.Where(o => o.Purpose == OrderPurpose.Close && o.TradeId != null).Select(o => o.TradeId!.Value).ToHashSet();
        return new AgentDetail(
            agent.Id, agent.Name, agent.MarketType, agent.Symbol, agent.Timeframe, agent.StrategyId, agent.StrategyVersion,
            _catalog.Find(agent.StrategyId)?.Version,
            StrategyParameters.Read(agent.Parameters), agent.Mode, agent.ExchangeKeyId, agent.Leverage, agent.MarginType,
            agent.RiskLimits, agent.Discretionary, agent.State, agent.PauseCause, agent.AutoResumeAt, agent.StateReason,
            agent.CreatedAt, agent.UpdatedAt,
            assignments.Select(c => new CouncilAssignmentView(c.Role, c.Provider, c.Model, c.Enabled,
                catalog.FirstOrDefault(m => m.Provider == c.Provider && m.ModelId == c.Model)?.Status)).ToList(),
            Ordered(agent.EntryWindows).Select(w => new EntryWindowView(w.Id, w.Kind, w.StartAt, w.EndAt, w.StartDay, w.StartTime, w.EndDay, w.EndTime)).ToList(),
            trades.Select(t => new OpenPositionView(t.Id, t.Side, t.Size, t.RemainingSize > 0 ? t.RemainingSize : t.Size, t.EntryPrice, t.CurrentStop, t.IsUnmanaged, closing.Contains(t.Id), t.IsPaper, t.OpenedAt)).ToList(),
            orders.Select(o => new OpenOrderView(o.Id, o.Purpose, o.Type, o.Side, o.Price, o.StopPrice, o.Quantity, o.Status, o.IsPaper, o.ExpiresAt, o.CreatedAt)).ToList(),
            evaluations,
            changes.Select(c => new AgentChangeView(c.Timestamp, c.Field, c.OldValue?.GetRawText(), c.NewValue?.GetRawText())).ToList());
    }

    // --- Create, clone, delete ------------------------------------------------------------------------------------

    public async Task<AgentSaveResult> CreateAsync(Guid userId, AgentCreateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<FieldError>();
        var settings = AgentRules.NormalizeSettings(input.Name, input.MarketType, input.Symbol, input.Timeframe, input.Mode, input.ExchangeKeyId,
            input.Leverage, input.MarginType, discretionary: false, _caps.Value, errors);
        if (settings is null)
            return AgentSaveResult.Invalid(errors);

        using var userLock = await UserLockAsync(userId, cancellationToken).ConfigureAwait(false);
        var limit = await _limits.CheckCreateAgentAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!limit.Allowed)
            return new AgentSaveResult(AgentSaveStatus.LimitReached, Limit: limit);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var (keyError, environment) = await CheckKeyAsync(db, userId, settings, cancellationToken).ConfigureAwait(false);
        if (keyError is not null)
            return AgentSaveResult.Invalid(keyError);
        if (await CheckSymbolAsync(environment, settings, cancellationToken).ConfigureAwait(false) is { } symbolResult)
            return symbolResult;

        var strategy = _catalog.ForTimeframe(settings.Timeframe)[0];
        var now = _time.GetUtcNow();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = settings.Name,
            MarketType = settings.MarketType,
            Symbol = settings.Symbol,
            Timeframe = settings.Timeframe,
            StrategyId = strategy.Id,
            StrategyVersion = strategy.Version,
            Parameters = JsonValues.EmptyObject,
            Mode = settings.Mode,
            ExchangeKeyId = settings.ExchangeKeyId,
            Leverage = settings.Leverage,
            MarginType = settings.MarginType,
            RiskLimits = new RiskLimits(),
            Discretionary = false,
            State = AgentState.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // FR-EXP-08: a role takes the provider and model with the best expectancy in the user's own history for this
        // timeframe and market, else the Appendix B default on its provider's newest catalog model (FR-SYNC-05).
        var suggestions = await _suggestions.SuggestAsync(userId, settings.Timeframe, settings.MarketType, cancellationToken).ConfigureAwait(false);
        var suggested = 0;
        foreach (var role in AgentRules.Roles)
        {
            var suggestion = suggestions.FirstOrDefault(s => s.Role == role && !string.IsNullOrWhiteSpace(s.Model));
            var provider = suggestion?.Provider ?? AgentRules.DefaultProvider(role);
            var model = suggestion?.Model.Trim() ?? await NewestModelAsync(db, provider, cancellationToken).ConfigureAwait(false);
            if (suggestion is not null)
                suggested++;
            agent.CouncilAssignments.Add(new CouncilAssignment
            {
                Role = role,
                Provider = provider,
                Model = model,
                Enabled = true,
            });
        }

        var changes = new ChangeSet(agent.Id, now);
        changes.Note(ChangeSet.CreatedField, new { settings.Mode, settings.MarketType, settings.Symbol, settings.Timeframe, strategy = strategy.Id });
        db.Agents.Add(agent);
        db.AgentChanges.AddRange(changes.Entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("User {UserId} created agent {AgentId} ({Mode} {Market} {Symbol} {Timeframe}); {Suggested} council role(s) assigned from experience",
            userId, agent.Id, agent.Mode, agent.MarketType, agent.Symbol, agent.Timeframe.ToLabel(), suggested);
        return new AgentSaveResult(AgentSaveStatus.Succeeded, agent.Id);
    }

    public async Task<AgentActionResult> CloneAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var userLock = await UserLockAsync(userId, cancellationToken).ConfigureAwait(false);
        var limit = await _limits.CheckCreateAgentAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!limit.Allowed)
            return new AgentActionResult(AgentActionStatus.LimitReached, Limit: limit);

        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = await db.Agents.AsNoTracking().Include(a => a.CouncilAssignments).Include(a => a.EntryWindows).AsSplitQuery()
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (source is null)
            return new AgentActionResult(AgentActionStatus.NotFound);

        var now = _time.GetUtcNow();
        var name = source.Name + " (copy)";
        var clone = new Agent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Length <= AgentRules.MaxNameLength ? name : name[..AgentRules.MaxNameLength],
            MarketType = source.MarketType,
            Symbol = source.Symbol,
            Timeframe = source.Timeframe,
            StrategyId = source.StrategyId,
            StrategyVersion = source.StrategyVersion,
            Parameters = source.Parameters.Clone(),
            Mode = source.Mode,
            ExchangeKeyId = source.ExchangeKeyId,
            Leverage = source.Leverage,
            MarginType = source.MarginType,
            RiskLimits = new RiskLimits
            {
                RiskPerTradePercent = source.RiskLimits.RiskPerTradePercent,
                MaxOpenPositions = source.RiskLimits.MaxOpenPositions,
                DailyLossLimitPercent = source.RiskLimits.DailyLossLimitPercent,
                MaxNotional = source.RiskLimits.MaxNotional,
            },
            Discretionary = source.Discretionary,
            State = AgentState.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var assignment in source.CouncilAssignments)
            clone.CouncilAssignments.Add(new CouncilAssignment { Role = assignment.Role, Provider = assignment.Provider, Model = assignment.Model, Enabled = assignment.Enabled });
        foreach (var window in source.EntryWindows)
        {
            clone.EntryWindows.Add(new EntryWindow
            {
                Kind = window.Kind, StartAt = window.StartAt, EndAt = window.EndAt,
                StartDay = window.StartDay, StartTime = window.StartTime, EndDay = window.EndDay, EndTime = window.EndTime,
            });
        }

        var changes = new ChangeSet(clone.Id, now);
        changes.Note(ChangeSet.CreatedField, new { clonedFrom = source.Id });
        db.Agents.Add(clone);
        db.AgentChanges.AddRange(changes.Entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("User {UserId} cloned agent {AgentId} into {CloneId}", userId, agentId, clone.Id);
        return new AgentActionResult(AgentActionStatus.Succeeded, AgentId: clone.Id);
    }

    public async Task<AgentActionResult> DeleteAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.Agents.Where(a => a.Id == agentId && a.UserId == userId).Select(a => (AgentState?)a.State)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
            return new AgentActionResult(AgentActionStatus.NotFound);
        if (!AgentStateMachine.CanDelete(state.Value))
            return new AgentActionResult(AgentActionStatus.InvalidState, $"An agent in {state} cannot be deleted; stop it first.");

        var rows = await db.Agents
            .Where(a => a.Id == agentId && a.UserId == userId && (a.State == AgentState.Draft || a.State == AgentState.Stopped))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            return new AgentActionResult(AgentActionStatus.NotFound);

        _runtime.Forget(agentId);
        _logger.LogInformation("User {UserId} deleted agent {AgentId}", userId, agentId);
        return new AgentActionResult(AgentActionStatus.Succeeded);
    }

    // --- Edits ----------------------------------------------------------------------------------------------------

    public async Task<AgentSaveResult> UpdateSettingsAsync(Guid userId, Guid agentId, AgentSettingsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<FieldError>();
        var settings = AgentRules.NormalizeSettings(input.Name, input.MarketType, input.Symbol, input.Timeframe, input.Mode, input.ExchangeKeyId,
            input.Leverage, input.MarginType, input.Discretionary, _caps.Value, errors);
        if (settings is null)
            return AgentSaveResult.Invalid(errors);

        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);

        var blocked = new List<string>();
        Guard(agent.State, AgentField.Name, agent.Name != settings.Name, "the name", blocked);
        Guard(agent.State, AgentField.Discretionary, agent.Discretionary != settings.Discretionary, "discretionary mode", blocked);
        Guard(agent.State, AgentField.MarketType, agent.MarketType != settings.MarketType, "the market", blocked);
        Guard(agent.State, AgentField.Symbol, agent.Symbol != settings.Symbol, "the symbol", blocked);
        Guard(agent.State, AgentField.Timeframe, agent.Timeframe != settings.Timeframe, "the timeframe", blocked);
        Guard(agent.State, AgentField.Mode, agent.Mode != settings.Mode, "the mode", blocked);
        Guard(agent.State, AgentField.ExchangeKey, agent.ExchangeKeyId != settings.ExchangeKeyId, "the exchange key", blocked);
        Guard(agent.State, AgentField.Leverage, agent.Leverage != settings.Leverage, "the leverage", blocked);
        Guard(agent.State, AgentField.MarginType, agent.MarginType != settings.MarginType, "the margin type", blocked);
        if (blocked.Count > 0)
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, {string.Join(", ", blocked)} cannot be changed.");

        var marketChanged = agent.MarketType != settings.MarketType || agent.Symbol != settings.Symbol;
        var keyChanged = agent.ExchangeKeyId != settings.ExchangeKeyId || agent.Mode != settings.Mode;
        var (keyError, environment) = await CheckKeyAsync(db, userId, settings, cancellationToken).ConfigureAwait(false);
        if (keyError is not null)
            return AgentSaveResult.Invalid(keyError);
        if (marketChanged || (settings.Mode == AgentMode.Live && keyChanged))
        {
            if (await CheckSymbolAsync(environment, settings, cancellationToken).ConfigureAwait(false) is { } symbolResult)
                return symbolResult;
        }

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        if (changes.Track("name", agent.Name, settings.Name))
            agent.Name = settings.Name;
        if (changes.Track("discretionary", agent.Discretionary, settings.Discretionary))
            agent.Discretionary = settings.Discretionary;
        if (changes.Track("marketType", agent.MarketType, settings.MarketType))
            agent.MarketType = settings.MarketType;
        if (changes.Track("symbol", agent.Symbol, settings.Symbol))
            agent.Symbol = settings.Symbol;
        if (changes.Track("mode", agent.Mode, settings.Mode))
            agent.Mode = settings.Mode;
        if (changes.Track("exchangeKeyId", agent.ExchangeKeyId, settings.ExchangeKeyId))
            agent.ExchangeKeyId = settings.ExchangeKeyId;
        if (changes.Track("leverage", agent.Leverage, settings.Leverage))
            agent.Leverage = settings.Leverage;
        if (changes.Track("marginType", agent.MarginType, settings.MarginType))
            agent.MarginType = settings.MarginType;
        if (changes.Track("timeframe", agent.Timeframe, settings.Timeframe))
        {
            // FR-AGT-03: a strategy belongs to one timeframe, so a new timeframe takes its default strategy and parameters.
            agent.Timeframe = settings.Timeframe;
            var strategy = _catalog.ForTimeframe(settings.Timeframe)[0];
            if (changes.Track("strategyId", agent.StrategyId, strategy.Id))
                agent.StrategyId = strategy.Id;
            if (changes.Track("strategyVersion", agent.StrategyVersion, strategy.Version))
                agent.StrategyVersion = strategy.Version;
            if (changes.TrackJson("parameters", agent.Parameters, JsonValues.EmptyObject))
                agent.Parameters = JsonValues.EmptyObject;
        }

        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSaveResult> UpdateStrategyAsync(Guid userId, Guid agentId, StrategyInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);

        var latest = _catalog.Find(input.StrategyId);
        if (latest is null)
            return AgentSaveResult.Invalid(new FieldError("strategyId", "Choose a strategy."));
        if (latest.Timeframe != agent.Timeframe)
            return AgentSaveResult.Invalid(new FieldError("strategyId", $"{latest.Name} is a {AgentsOptions.KeyOf(latest.Timeframe)} strategy; this agent runs on {AgentsOptions.KeyOf(agent.Timeframe)}."));

        // FR-STR-06: a new strategy pins its newest version and starts from defaults; an edit keeps the pinned version.
        var strategyChanged = agent.StrategyId != latest.Id;
        var strategy = strategyChanged ? latest : _catalog.Find(agent.StrategyId, agent.StrategyVersion);
        if (strategy is null)
            return AgentSaveResult.Invalid(new FieldError("strategyId", $"Version {agent.StrategyVersion} of {latest.Name} is no longer available; upgrade the agent to version {latest.Version} first."));

        var errors = new List<FieldError>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!input.ResetToDefaults && !strategyChanged)
        {
            foreach (var parameter in strategy.Parameters)
            {
                input.Parameters.TryGetValue(parameter.Name, out var text);
                if (!parameter.TryParse(text, out var value, out var error))
                    errors.Add(new FieldError($"parameters.{parameter.Name}", error!));
                else if (value.GetRawText() != parameter.Default.GetRawText())
                    values[parameter.Name] = value;
            }

            if (errors.Count == 0)
                errors.AddRange(strategy.Validate(values));
        }

        if (errors.Count > 0)
            return AgentSaveResult.Invalid(errors);

        var stored = StrategyParameters.Read(agent.Parameters);
        var parametersChanged = StrategyParameters.Write(stored).GetRawText() != StrategyParameters.Write(values).GetRawText();
        var blocked = new List<string>();
        Guard(agent.State, AgentField.Strategy, strategyChanged, "the strategy", blocked);
        Guard(agent.State, AgentField.Parameters, parametersChanged, "the strategy parameters", blocked);
        if (blocked.Count > 0)
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, {string.Join(", ", blocked)} cannot be changed.");

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        if (changes.Track("strategyId", agent.StrategyId, strategy.Id))
            agent.StrategyId = strategy.Id;
        if (strategyChanged && changes.Track("strategyVersion", agent.StrategyVersion, strategy.Version))
            agent.StrategyVersion = strategy.Version;
        if (parametersChanged)
        {
            foreach (var parameter in strategy.Parameters)
            {
                var before = stored.TryGetValue(parameter.Name, out var old) ? old : parameter.Default;
                var after = values.TryGetValue(parameter.Name, out var updated) ? updated : parameter.Default;
                changes.TrackJson($"parameters.{parameter.Name}", before, after);
            }

            foreach (var (name, old) in stored.Where(s => strategy.Parameter(s.Key) is null))
                changes.TrackJson($"parameters.{name}", old, null);
            agent.Parameters = StrategyParameters.Write(values);
        }

        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSaveResult> UpdateCouncilAsync(Guid userId, Guid agentId, CouncilInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = AgentRules.ValidateCouncil(input);
        if (errors.Count > 0)
            return AgentSaveResult.Invalid(errors);

        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.Include(a => a.CouncilAssignments)
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        var pending = new List<Action>();
        foreach (var role in input.Roles)
        {
            var field = $"council.{AgentRules.CamelCase(role.Role)}";
            var model = role.Model?.Trim() ?? "";
            var existing = agent.CouncilAssignments.FirstOrDefault(c => c.Role == role.Role);
            if (existing is null)
            {
                changes.Track(field, (object?)null, new { role.Provider, model, role.Enabled });
                pending.Add(() => agent.CouncilAssignments.Add(new CouncilAssignment { Role = role.Role, Provider = role.Provider, Model = model, Enabled = role.Enabled }));
                continue;
            }

            if (changes.Track(field + ".provider", existing.Provider, role.Provider))
                pending.Add(() => existing.Provider = role.Provider);
            if (changes.Track(field + ".model", existing.Model, model))
                pending.Add(() => existing.Model = model);
            if (changes.Track(field + ".enabled", existing.Enabled, role.Enabled))
                pending.Add(() => existing.Enabled = role.Enabled);
        }

        if (changes.Count > 0 && !AgentStateMachine.CanEdit(agent.State, AgentField.Council))
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, the council cannot be changed.");

        foreach (var apply in pending)
            apply();
        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSaveResult> ResetCouncilToDefaultsAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var roles = new List<CouncilRoleInput>();
        var errors = new List<FieldError>();
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = await db.CouncilAssignments.AsNoTracking()
                .Where(c => c.AgentId == agentId && c.Agent.UserId == userId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (current.Count == 0 && await StateAsync(db, userId, agentId, cancellationToken).ConfigureAwait(false) is null)
                return new AgentSaveResult(AgentSaveStatus.NotFound);

            foreach (var role in CouncilRoles.Order)
            {
                var provider = CouncilRoles.DefaultProvider(role);
                var model = await NewestModelAsync(db, provider, cancellationToken).ConfigureAwait(false);
                // Without a synced catalog the role keeps the model it already has on that provider (FR-SYNC-05: nothing is hard-coded).
                if (model.Length == 0 && current.FirstOrDefault(c => c.Role == role) is { } existing && existing.Provider == provider)
                    model = existing.Model;
                if (model.Length == 0)
                    errors.Add(new FieldError($"council.{CouncilRoles.CamelCase(role)}.model", $"No {ProviderNames.Display(provider)} model is in the catalog yet; add a {ProviderNames.Display(provider)} key to sync it, or choose the {CouncilRoles.Display(role)}'s model by hand."));
                roles.Add(new CouncilRoleInput(role, provider, model, true));
            }
        }

        if (errors.Count > 0)
            return AgentSaveResult.Invalid(errors);
        return await UpdateCouncilAsync(userId, agentId, new CouncilInput(roles), cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentSaveResult> UseSingleModelAsync(Guid userId, Guid agentId, AiProvider provider, string? model, CancellationToken cancellationToken) =>
        UpdateCouncilAsync(userId, agentId, new CouncilInput(CouncilRoles.Order.Select(role => new CouncilRoleInput(role, provider, model, true)).ToList()), cancellationToken);

    public async Task<AgentSaveResult> UpdateRiskLimitsAsync(Guid userId, Guid agentId, RiskLimitsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = AgentRules.ValidateRiskLimits(input, _caps.Value);
        if (errors.Count > 0)
            return AgentSaveResult.Invalid(errors);

        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        var limits = agent.RiskLimits;
        var riskChanged = changes.Track("riskLimits.riskPerTradePercent", limits.RiskPerTradePercent, input.RiskPerTradePercent);
        var positionsChanged = changes.Track("riskLimits.maxOpenPositions", limits.MaxOpenPositions, input.MaxOpenPositions);
        var lossChanged = changes.Track("riskLimits.dailyLossLimitPercent", limits.DailyLossLimitPercent, input.DailyLossLimitPercent);
        var notionalChanged = changes.Track("riskLimits.maxNotional", limits.MaxNotional, input.MaxNotional);
        if (changes.Count > 0 && !AgentStateMachine.CanEdit(agent.State, AgentField.RiskLimits))
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, the risk limits cannot be changed.");

        if (riskChanged)
            limits.RiskPerTradePercent = input.RiskPerTradePercent;
        if (positionsChanged)
            limits.MaxOpenPositions = input.MaxOpenPositions;
        if (lossChanged)
            limits.DailyLossLimitPercent = input.DailyLossLimitPercent;
        if (notionalChanged)
            limits.MaxNotional = input.MaxNotional;
        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSaveResult> UpgradeStrategyAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);

        var latest = _catalog.Find(agent.StrategyId);
        if (latest is null)
            return AgentSaveResult.Invalid(new FieldError("strategyId", $"Strategy '{agent.StrategyId}' is no longer in the catalog; choose another."));
        if (latest.Version <= agent.StrategyVersion)
            return new AgentSaveResult(AgentSaveStatus.Succeeded, agent.Id);
        if (!AgentStateMachine.CanEdit(agent.State, AgentField.StrategyVersion))
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, the strategy version cannot be changed; pause or stop it first.");

        // FR-STR-04/06: what the new version still knows and accepts carries over; the rest returns to its defaults.
        var stored = StrategyParameters.Read(agent.Parameters);
        var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var dropped = new List<(string Name, JsonElement Old, JsonElement? New)>();
        foreach (var (name, value) in stored)
        {
            var parameter = latest.Parameter(name);
            if (parameter is null)
                dropped.Add((name, value, null));
            else if (parameter.Validate(value) is not null)
                dropped.Add((name, value, parameter.Default));
            else
                kept[name] = value;
        }

        var crossed = latest.Validate(kept);
        if (crossed.Count > 0)
        {
            foreach (var name in crossed.Select(e => e.Field["parameters.".Length..]).Where(kept.ContainsKey).Distinct(StringComparer.Ordinal).ToList())
            {
                dropped.Add((name, kept[name], latest.Parameter(name)!.Default));
                kept.Remove(name);
            }

            if (latest.Validate(kept).Count > 0)
            {
                foreach (var (name, value) in kept)
                    dropped.Add((name, value, latest.Parameter(name)!.Default));
                kept.Clear();
            }
        }

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        changes.Track("strategyVersion", agent.StrategyVersion, latest.Version);
        agent.StrategyVersion = latest.Version;
        foreach (var (name, old, replacement) in dropped)
            changes.TrackJson($"parameters.{name}", old, replacement);
        if (dropped.Count > 0)
            agent.Parameters = StrategyParameters.Write(kept);

        var result = await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
        if (result.Status != AgentSaveStatus.Succeeded || dropped.Count == 0)
            return result;
        var names = dropped.Select(d => d.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return result with { Detail = $"{names.Count} parameter(s) reset to the version {latest.Version} default: {string.Join(", ", names)}." };
    }

    public async Task<AgentSaveResult> AddEntryWindowAsync(Guid userId, Guid agentId, EntryWindowInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<FieldError>();
        var window = AgentRules.NormalizeEntryWindow(input, _time.GetUtcNow(), errors);
        if (window is null)
            return AgentSaveResult.Invalid(errors);

        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.Include(a => a.EntryWindows)
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);
        if (!AgentStateMachine.CanEdit(agent.State, AgentField.EntryWindows))
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, the no-entry windows cannot be changed.");
        if (agent.EntryWindows.Count >= AgentRules.MaxEntryWindows)
            return AgentSaveResult.Invalid(new FieldError("entryWindow", $"An agent can have at most {AgentRules.MaxEntryWindows} no-entry windows."));
        if (agent.EntryWindows.Any(w => AgentRules.SameWindow(w, window)))
            return AgentSaveResult.Invalid(new FieldError("entryWindow", "That window already exists."));

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        var before = Snapshot(agent.EntryWindows);
        agent.EntryWindows.Add(window);
        changes.Track("entryWindows", before, Snapshot(agent.EntryWindows));
        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSaveResult> RemoveEntryWindowAsync(Guid userId, Guid agentId, long windowId, CancellationToken cancellationToken)
    {
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.Include(a => a.EntryWindows)
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentSaveResult(AgentSaveStatus.NotFound);
        var window = agent.EntryWindows.FirstOrDefault(w => w.Id == windowId);
        if (window is null)
            return AgentSaveResult.Invalid(new FieldError("entryWindow", "That window was already removed."));
        if (!AgentStateMachine.CanEdit(agent.State, AgentField.EntryWindows))
            return new AgentSaveResult(AgentSaveStatus.InvalidState, Detail: $"While the agent is {agent.State}, the no-entry windows cannot be changed.");

        var now = _time.GetUtcNow();
        var changes = new ChangeSet(agentId, now);
        var before = Snapshot(agent.EntryWindows);
        agent.EntryWindows.Remove(window);
        db.EntryWindows.Remove(window);
        changes.Track("entryWindows", before, Snapshot(agent.EntryWindows));
        return await SaveChangesAsync(db, agent, changes, now, cancellationToken).ConfigureAwait(false);
    }

    // --- Lifecycle ------------------------------------------------------------------------------------------------

    public async Task<AgentActionResult> StartAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var userLock = await UserLockAsync(userId, cancellationToken).ConfigureAwait(false);
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.AsNoTracking().Include(a => a.CouncilAssignments)
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentActionResult(AgentActionStatus.NotFound);
        if (!AgentStateMachine.CanStart(agent.State))
            return new AgentActionResult(AgentActionStatus.InvalidState, $"An agent in {agent.State} cannot be started.");

        var limit = await _limits.CheckStartAgentAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!limit.Allowed)
            return new AgentActionResult(AgentActionStatus.LimitReached, Limit: limit);

        var strategy = _catalog.Find(agent.StrategyId, agent.StrategyVersion);
        if (strategy is null)
        {
            var latest = _catalog.Find(agent.StrategyId);
            return new AgentActionResult(AgentActionStatus.StrategyUnavailable, latest is null
                ? $"Strategy '{agent.StrategyId}' is no longer in the catalog; choose another."
                : $"Version {agent.StrategyVersion} of {latest.Name} is no longer in the catalog; upgrade the agent to version {latest.Version}.");
        }

        var caps = _caps.Value;
        var problems = new List<string>();
        if (agent.MarketType == MarketType.UsdFutures && (agent.Leverage is null || agent.Leverage < 1 || agent.Leverage > caps.MaxLeverage))
            problems.Add($"leverage must be between 1 and {caps.MaxLeverage}");
        if (agent.RiskLimits.RiskPerTradePercent <= 0 || agent.RiskLimits.RiskPerTradePercent > caps.MaxRiskPerTradePercent)
            problems.Add($"risk per trade must be at most {caps.MaxRiskPerTradePercent}%");
        if (agent.RiskLimits.MaxOpenPositions < 1 || agent.RiskLimits.MaxOpenPositions > caps.MaxOpenPositions)
            problems.Add($"open positions must be at most {caps.MaxOpenPositions}");
        if (agent.RiskLimits.DailyLossLimitPercent <= 0 || agent.RiskLimits.DailyLossLimitPercent > caps.MaxDailyLossPercent)
            problems.Add($"the daily loss limit must be at most {caps.MaxDailyLossPercent}%");
        var enabledRoles = agent.CouncilAssignments.Where(c => c.Enabled).ToList();
        if (enabledRoles.All(c => c.Role != CouncilRole.RiskManager))
            problems.Add("the Risk Manager must be enabled");
        foreach (var role in enabledRoles.Where(c => string.IsNullOrWhiteSpace(c.Model)))
            problems.Add($"the {AgentRules.Display(role.Role)} has no model");
        var parameterErrors = strategy.Validate(StrategyParameters.Read(agent.Parameters));
        if (parameterErrors.Count > 0)
            problems.Add("the strategy parameters are invalid: " + string.Join("; ", parameterErrors.Select(e => e.Message.TrimEnd('.'))));
        if (problems.Count > 0)
            return new AgentActionResult(AgentActionStatus.InvalidConfiguration, Capitalize(string.Join("; ", problems)) + ".");

        var environment = ExchangeEnvironment.Production;
        if (agent.Mode == AgentMode.Live)
        {
            var disclaimer = await _accounts.GetDisclaimerStatusAsync(userId, cancellationToken).ConfigureAwait(false);
            if (!disclaimer.IsAccepted)
                return new AgentActionResult(AgentActionStatus.DisclaimerRequired, "Read and accept the risk disclaimer before starting a live agent.");
            if (agent.ExchangeKeyId is not { } keyId)
                return new AgentActionResult(AgentActionStatus.KeyUnavailable, "Choose a Binance key for live mode.");
            var resolution = await _keys.ResolveExchangeKeyAsync(userId, keyId, agent.MarketType, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsResolved)
                return new AgentActionResult(AgentActionStatus.KeyUnavailable, resolution.Reason ?? KeyStatusText(resolution.Status));
            environment = resolution.Handle!.Environment;

            // FR-EXE-08: one live agent per key, market and symbol, so the exchange's position and balance attribute to one agent; an agent in
            // Error keeps its position and its rows, so it counts until it is stopped.
            var duplicate = await db.Agents.AsNoTracking()
                .Where(a => a.UserId == userId && a.Id != agentId && a.Mode == AgentMode.Live && a.ExchangeKeyId == keyId
                    && a.MarketType == agent.MarketType && a.Symbol == agent.Symbol && (a.State == AgentState.Running || a.State == AgentState.Paused || a.State == AgentState.Error))
                .Select(a => a.Name)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                return new AgentActionResult(AgentActionStatus.InvalidConfiguration, $"Another live agent already trades {agent.Symbol} on {MarketName(agent.MarketType)} with this Binance key; stop it first, because one position per key, market and symbol can belong to one agent only.");
        }

        var missing = new List<string>();
        foreach (var provider in enabledRoles.Select(c => c.Provider).Distinct().OrderBy(p => p))
        {
            var resolution = await _keys.ResolveProviderKeyAsync(userId, provider, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsResolved)
                missing.Add(ProviderNames.Display(provider));
        }

        if (missing.Count > 0)
            return new AgentActionResult(AgentActionStatus.ProviderKeyMissing, $"No valid key for {string.Join(", ", missing)}. Add one on the keys page or disable the role.");

        var now = _time.GetUtcNow();
        var rows = await db.Agents
            .Where(a => a.Id == agentId && a.UserId == userId && (a.State == AgentState.Draft || a.State == AgentState.Stopped || a.State == AgentState.Error))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Running)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, (string?)null)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return new AgentActionResult(AgentActionStatus.NotFound);

        db.AgentChanges.Add(ChangeSet.State(agentId, now, agent.State, AgentState.Running, null));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var tracked = _runtime.Track(new AgentTrackingInfo(agentId, userId, new MarketKey(environment, agent.MarketType, agent.Symbol), agent.Timeframe, agent.StrategyId, agent.StrategyVersion));
        if (!tracked)
            _logger.LogWarning("Agent {AgentId} started while the host is stopping; it resumes at the next start", agentId);
        _logger.LogInformation("User {UserId} started agent {AgentId} ({Mode} {Market} {Symbol} {Timeframe})", userId, agentId, agent.Mode, agent.MarketType, agent.Symbol, agent.Timeframe.ToLabel());
        _events.Publish(new AgentStateChanged(agentId, userId, now, AgentState.Running, null));
        return new AgentActionResult(AgentActionStatus.Succeeded);
    }

    public async Task<AgentActionResult> PauseAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await StateAsync(db, userId, agentId, cancellationToken).ConfigureAwait(false);
        if (state is null)
            return new AgentActionResult(AgentActionStatus.NotFound);
        if (!AgentStateMachine.CanPause(state.Value))
            return new AgentActionResult(AgentActionStatus.InvalidState, $"An agent in {state} cannot be paused.");

        var now = _time.GetUtcNow();
        var rows = await db.Agents.Where(a => a.Id == agentId && a.UserId == userId && a.State == AgentState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Paused)
                .SetProperty(a => a.PauseCause, PauseCause.User)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, PausedByUser)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return new AgentActionResult(AgentActionStatus.NotFound);

        db.AgentChanges.Add(ChangeSet.State(agentId, now, AgentState.Running, AgentState.Paused, PausedByUser));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} paused agent {AgentId}", userId, agentId);
        _events.Publish(new AgentStateChanged(agentId, userId, now, AgentState.Paused, PausedByUser));
        return new AgentActionResult(AgentActionStatus.Succeeded);
    }

    public async Task<AgentActionResult> ResumeAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.AsNoTracking().Include(a => a.ExchangeKey)
            .SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentActionResult(AgentActionStatus.NotFound);
        if (!AgentStateMachine.CanResume(agent.State))
            return new AgentActionResult(AgentActionStatus.InvalidState, $"An agent in {agent.State} cannot be resumed.");

        var now = _time.GetUtcNow();
        var rows = await db.Agents.Where(a => a.Id == agentId && a.UserId == userId && a.State == AgentState.Paused)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Running)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, (string?)null)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return new AgentActionResult(AgentActionStatus.NotFound);

        db.AgentChanges.Add(ChangeSet.State(agentId, now, AgentState.Paused, AgentState.Running, null));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!_runtime.IsTracked(agentId))
        {
            var environment = agent.Mode == AgentMode.Live ? agent.ExchangeKey?.Environment ?? ExchangeEnvironment.Production : ExchangeEnvironment.Production;
            _runtime.Track(new AgentTrackingInfo(agentId, userId, new MarketKey(environment, agent.MarketType, agent.Symbol), agent.Timeframe, agent.StrategyId, agent.StrategyVersion));
        }

        _logger.LogInformation("User {UserId} resumed agent {AgentId}", userId, agentId);
        _events.Publish(new AgentStateChanged(agentId, userId, now, AgentState.Running, null));
        return new AgentActionResult(AgentActionStatus.Succeeded);
    }

    public async Task<AgentActionResult> StopAsync(Guid userId, Guid agentId, bool closePositions, CancellationToken cancellationToken)
    {
        _runtime.CancelEvaluation(agentId);
        using var agentLock = await _runtime.LockAsync(agentId, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return new AgentActionResult(AgentActionStatus.NotFound);
        if (!AgentStateMachine.CanStop(agent.State))
            return new AgentActionResult(AgentActionStatus.InvalidState, $"An agent in {agent.State} cannot be stopped.");

        // FR-AGT-06: orders first; a failure here leaves the agent as it was and tells the user.
        var venue = _venues.For(agent.Mode);
        int cancelled;
        var closed = 0;
        var unmanaged = 0;
        try
        {
            cancelled = await venue.CancelOpenOrdersAsync(agent, cancellationToken).ConfigureAwait(false);
            if (closePositions)
            {
                closed = await venue.ClosePositionsAtMarketAsync(agent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                unmanaged = await db.Trades.Where(t => t.AgentId == agentId && t.ClosedAt == null && !t.IsUnmanaged)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsUnmanaged, true), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent {AgentId} could not be stopped because its orders or positions could not be handled", agentId);
            return new AgentActionResult(AgentActionStatus.Failed, $"The agent's orders could not be cancelled: {ex.Message}");
        }

        var now = _time.GetUtcNow();
        var rows = await db.Agents
            .Where(a => a.Id == agentId && a.UserId == userId && (a.State == AgentState.Running || a.State == AgentState.Paused || a.State == AgentState.Error))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Stopped)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, StoppedByUser)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        _runtime.Untrack(agentId);
        if (rows == 0)
        {
            _logger.LogWarning("Agent {AgentId} changed state while being stopped; its orders were cancelled", agentId);
            return new AgentActionResult(AgentActionStatus.NotFound);
        }

        db.AgentChanges.Add(ChangeSet.State(agentId, now, agent.State, AgentState.Stopped, StoppedByUser));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} stopped agent {AgentId}: {Cancelled} order(s) cancelled, {Closed} position(s) closing, {Unmanaged} left unmanaged", userId, agentId, cancelled, closed, unmanaged);
        _events.Publish(new AgentStateChanged(agentId, userId, now, AgentState.Stopped, StoppedByUser));
        var detail = closePositions
            ? $"{cancelled} order(s) cancelled; {closed} position(s) closing at market."
            : $"{cancelled} order(s) cancelled; {unmanaged} position(s) left open as unmanaged.";
        return new AgentActionResult(AgentActionStatus.Succeeded, detail);
    }

    public async Task<AgentBulkActionResult> StopAllAsync(Guid userId, bool closePositions, CancellationToken cancellationToken)
    {
        List<(Guid Id, string Name)> candidates;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates = (await db.Agents.AsNoTracking()
                .Where(a => a.UserId == userId && (a.State == AgentState.Running || a.State == AgentState.Paused || a.State == AgentState.Error))
                .OrderBy(a => a.Name).ThenBy(a => a.Id)
                .Select(a => new { a.Id, a.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(a => (a.Id, a.Name)).ToList();
        }

        var stopped = 0;
        var failed = new List<AgentBulkFailure>();
        foreach (var (id, name) in candidates)
        {
            var result = await StopAsync(userId, id, closePositions, cancellationToken).ConfigureAwait(false);
            if (result.Status == AgentActionStatus.Succeeded)
                stopped++;
            else if (result.Status != AgentActionStatus.InvalidState)
                failed.Add(new AgentBulkFailure(id, name, result));
        }

        _logger.LogInformation("User {UserId} stopped all agents: {Stopped} stopped, {Failed} could not be stopped", userId, stopped, failed.Count);
        return new AgentBulkActionResult(stopped, failed);
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>Recurring windows first by day and time, then one-off ones by start; ties by id.</summary>
    private static IEnumerable<EntryWindow> Ordered(IEnumerable<EntryWindow> windows) => windows
        .OrderBy(w => w.Kind == EntryWindowKind.Recurring ? 0 : 1)
        .ThenBy(w => w.Kind == EntryWindowKind.Recurring ? EntryWindowSchedule.WeekSecond(w.StartDay ?? DayOfWeek.Monday, w.StartTime ?? TimeOnly.MinValue) : 0)
        .ThenBy(w => w.StartAt ?? DateTimeOffset.MinValue)
        .ThenBy(w => w.Id);

    /// <summary>The windows as the change history records them: values only, so a clone and its source compare equal.</summary>
    private static List<WindowValue> Snapshot(IEnumerable<EntryWindow> windows) =>
        Ordered(windows).Select(w => new WindowValue(w.Kind, w.StartAt, w.EndAt, w.StartDay, w.StartTime, w.EndDay, w.EndTime)).ToList();

    private sealed record WindowValue(EntryWindowKind Kind, DateTimeOffset? StartAt, DateTimeOffset? EndAt, DayOfWeek? StartDay, TimeOnly? StartTime, DayOfWeek? EndDay, TimeOnly? EndTime);

    private static void Guard(AgentState state, AgentField field, bool changed, string label, List<string> blocked)
    {
        if (changed && !AgentStateMachine.CanEdit(state, field))
            blocked.Add(label);
    }

    private async Task<AgentSaveResult> SaveChangesAsync(EcnanibDbContext db, Agent agent, ChangeSet changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return new AgentSaveResult(AgentSaveStatus.Succeeded, agent.Id);

        agent.UpdatedAt = now;
        db.AgentChanges.AddRange(changes.Entries);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new AgentSaveResult(AgentSaveStatus.Conflict, agent.Id, Detail: "The agent changed meanwhile; reload the page and try again.");
        }

        _logger.LogInformation("User {UserId} changed agent {AgentId}: {Fields}", agent.UserId, agent.Id, string.Join(", ", changes.Entries.Select(c => c.Field)));
        return new AgentSaveResult(AgentSaveStatus.Succeeded, agent.Id);
    }

    /// <summary>For live mode the key must be the user's and allow the market; returns the environment the agent trades in (FR-KEY-01, FR-KEY-09).</summary>
    private static async Task<(FieldError? Error, ExchangeEnvironment Environment)> CheckKeyAsync(EcnanibDbContext db, Guid userId, AgentSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode != AgentMode.Live || settings.ExchangeKeyId is not { } keyId)
            return (null, ExchangeEnvironment.Production);

        var key = await db.ExchangeKeys.AsNoTracking().Where(k => k.Id == keyId && k.UserId == userId)
            .Select(k => new { k.Environment, k.AllowSpot, k.AllowFutures })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (key is null)
            return (new FieldError("exchangeKeyId", "Choose one of your Binance keys."), ExchangeEnvironment.Production);
        var allowed = settings.MarketType == MarketType.Spot ? key.AllowSpot : key.AllowFutures;
        if (!allowed)
            return (new FieldError("exchangeKeyId", $"That key is not enabled for {MarketName(settings.MarketType)}."), key.Environment);
        return (null, key.Environment);
    }

    /// <summary>Null when the symbol trades on the market; otherwise the result to return (FR-DATA-01).</summary>
    private async Task<AgentSaveResult?> CheckSymbolAsync(ExchangeEnvironment environment, AgentSettings settings, CancellationToken cancellationToken)
    {
        SymbolInfo? symbol;
        try
        {
            symbol = await _symbols.FindAsync(environment, settings.MarketType, settings.Symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Symbol {Symbol} could not be checked on {Environment} {Market}", settings.Symbol, environment, settings.MarketType);
            return new AgentSaveResult(AgentSaveStatus.ExchangeUnavailable, Detail: "Binance could not be reached to check the symbol; try again.");
        }

        if (symbol is null)
            return AgentSaveResult.Invalid(new FieldError("symbol", $"{settings.Symbol} is not listed on Binance {MarketName(settings.MarketType)} ({environment})."));
        if (!symbol.IsTrading)
            return AgentSaveResult.Invalid(new FieldError("symbol", $"{settings.Symbol} is not trading on Binance {MarketName(settings.MarketType)} (status {symbol.Status})."));
        return null;
    }

    /// <summary>The most recently added available model of a provider (FR-SYNC-05), or empty when the catalog has none yet.</summary>
    private static async Task<string> NewestModelAsync(EcnanibDbContext db, AiProvider provider, CancellationToken cancellationToken)
    {
        var model = await db.ProviderModels.AsNoTracking()
            .Where(m => m.Provider == provider && m.Status == ModelStatus.Available)
            .OrderByDescending(m => m.ProviderCreatedAt ?? m.FirstSeenAt)
            .ThenBy(m => m.ModelId)
            .Select(m => m.ModelId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return model ?? "";
    }

    private static async Task<AgentState?> StateAsync(EcnanibDbContext db, Guid userId, Guid agentId, CancellationToken cancellationToken) =>
        await db.Agents.Where(a => a.Id == agentId && a.UserId == userId).Select(a => (AgentState?)a.State)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IDisposable> UserLockAsync(Guid userId, CancellationToken cancellationToken)
    {
        var gate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private static string MarketName(MarketType market) => market == MarketType.Spot ? "Spot" : "Futures";

    private static string KeyStatusText(KeyResolutionStatus status) => status switch
    {
        KeyResolutionStatus.Missing => "The chosen Binance key no longer exists.",
        KeyResolutionStatus.Invalid => "The chosen Binance key is invalid.",
        KeyResolutionStatus.MarketNotAllowed => "The chosen Binance key is not enabled for this market.",
        _ => "The chosen Binance key cannot be used.",
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
