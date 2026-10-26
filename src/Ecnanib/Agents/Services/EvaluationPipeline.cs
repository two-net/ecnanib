using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Services;

/// <summary>One candle close to evaluate. <see cref="DeadlineAt"/> is measured from the close (NFR-PERF-01).</summary>
internal sealed record EvaluationRun(AgentTrackingInfo Agent, Candle Candle, DateTimeOffset DeadlineAt);

/// <summary>
/// What a run did: nothing (the agent is gone, cannot be evaluated as configured, or another run owns the candle), an
/// evaluation that finished before its deadline, or one that ended at or past it (NFR-PERF-01).
/// </summary>
internal enum EvaluationRunResult
{
    NotEvaluated,
    Finished,
    MissedDeadline,
}

/// <summary>
/// The FR-AGT-10 pipeline for one agent and one closed candle: load market data → strategy engine → council when
/// needed (FR-AI-08) → execute → persist → log → notify. The evaluation row is inserted first with
/// <see cref="EvaluationOutcome.InProgress"/>; its unique (agent, candle) index makes a second run of the same candle
/// a no-op (FR-AGT-09). With open orders or trades the execution module's management step runs before the council
/// (expiry, market fallback, breakeven and trailing, FR-EXE-03/06/08) and the open state is reloaded, so the council
/// sees what the rules already did. Everything up to the council, and execution's checks before its first order,
/// honour the cancellable token (deadline, Stop, shutdown); from the first placement on, placements and the writes
/// recording them run on the stopping token, because a cancelled token does not recall an order already sent. An entry
/// decided after the deadline is recorded and not placed; a protective decision (close, modify) still executes
/// (NFR-PERF-01). Every outcome line carries the run's duration and its distance from the close and the deadline; a
/// run that ends at or past its deadline is a Warning. The experience step (<see cref="IExperienceTracker"/>,
/// FR-EXP-03) runs last, whenever market data loaded, and never changes the outcome.
/// </summary>
internal sealed class EvaluationPipeline
{
    internal const int MaxDetailLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IStrategyCatalog _catalog;
    private readonly IMarketDataLoader _loader;
    private readonly IStrategyEngine _engine;
    private readonly ICouncil _council;
    private readonly IExecutionService _execution;
    private readonly IExperienceTracker _experience;
    private readonly IOrderVenues _venues;
    private readonly IKeyResolver _keys;
    private readonly IAgentEvents _events;
    private readonly IOptions<RiskCapsOptions> _caps;
    private readonly TimeProvider _time;
    private readonly ILogger<EvaluationPipeline> _logger;

    public EvaluationPipeline(
        IDbContextFactory<EcnanibDbContext> factory,
        IStrategyCatalog catalog,
        IMarketDataLoader loader,
        IStrategyEngine engine,
        ICouncil council,
        IExecutionService execution,
        IExperienceTracker experience,
        IOrderVenues venues,
        IKeyResolver keys,
        IAgentEvents events,
        IOptions<RiskCapsOptions> caps,
        TimeProvider time,
        ILogger<EvaluationPipeline> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _loader = loader;
        _engine = engine;
        _council = council;
        _execution = execution;
        _experience = experience;
        _venues = venues;
        _keys = keys;
        _events = events;
        _caps = caps;
        _time = time;
        _logger = logger;
    }

    public async Task<EvaluationRunResult> RunAsync(EvaluationRun run, IEvaluationHost host, CancellationToken cancellable, CancellationToken stopping)
    {
        var agentId = run.Agent.AgentId;
        var candle = run.Candle;

        Agent? agent;
        await using (var db = await _factory.CreateDbContextAsync(stopping).ConfigureAwait(false))
        {
            agent = await db.Agents.AsNoTracking().Include(a => a.CouncilAssignments).Include(a => a.EntryWindows).AsSplitQuery()
                .SingleOrDefaultAsync(a => a.Id == agentId, stopping).ConfigureAwait(false);
        }

        if (agent is null || !AgentStateMachine.IsEvaluated(agent.State))
            return EvaluationRunResult.NotEvaluated;

        // Unrecoverable before anything is recorded: the agent cannot be evaluated as configured (Appendix E → Error).
        var strategy = _catalog.Find(agent.StrategyId, agent.StrategyVersion);
        if (strategy is null)
        {
            await host.FailAsync(agentId, $"Strategy '{agent.StrategyId}' version {agent.StrategyVersion} is not in the catalog; upgrade it or choose another and start the agent again.", stopping).ConfigureAwait(false);
            return EvaluationRunResult.NotEvaluated;
        }

        var stored = StrategyParameters.Read(agent.Parameters);
        var invalid = strategy.Validate(stored);
        if (invalid.Count > 0)
        {
            await host.FailAsync(agentId, $"The strategy parameters are no longer valid: {string.Join("; ", invalid.Select(e => e.Message))} Edit them and start the agent again.", stopping).ConfigureAwait(false);
            return EvaluationRunResult.NotEvaluated;
        }

        ExchangeKeyHandle? exchangeKey = null;
        if (agent.Mode == AgentMode.Live)
        {
            if (agent.ExchangeKeyId is not { } keyId)
            {
                await host.FailAsync(agentId, "The exchange key is gone; choose a key and start the agent again.", stopping).ConfigureAwait(false);
                return EvaluationRunResult.NotEvaluated;
            }

            var resolution = await _keys.ResolveExchangeKeyAsync(agent.UserId, keyId, agent.MarketType, stopping).ConfigureAwait(false);
            if (!resolution.IsResolved)
            {
                await host.FailAsync(agentId, $"The exchange key cannot be used: {resolution.Reason}", stopping).ConfigureAwait(false);
                return EvaluationRunResult.NotEvaluated;
            }

            exchangeKey = resolution.Handle;
        }

        var now = _time.GetUtcNow();
        var evaluation = new Evaluation
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
            Symbol = agent.Symbol,
            MarketType = agent.MarketType,
            Timeframe = agent.Timeframe,
            CandleOpenTime = candle.OpenTime,
            StartedAt = now,
            Outcome = EvaluationOutcome.InProgress,
        };
        if (!await InsertAsync(evaluation, stopping).ConfigureAwait(false))
        {
            _logger.LogDebug("Agent {AgentId} already has an evaluation for candle {CandleOpen:O}; skipping", agentId, candle.OpenTime);
            return EvaluationRunResult.NotEvaluated;
        }

        _events.Publish(new EvaluationStarted(agentId, agent.UserId, now, evaluation.Id, candle.OpenTime));
        // FR-LOG-02: the logs of this evaluation name it (the scheduler's scope already names the agent and the user).
        using var evaluationScope = _logger.BeginScope(evaluationId: evaluation.Id);

        var outcome = EvaluationOutcome.Failed;
        string? detail = null;
        var councilConsulted = false;
        JsonElement? strategyJson = null;
        Decision? decision = null;
        string? fault = null;
        var entriesAllowed = agent.State == AgentState.Running;
        var parameters = strategy.Resolve(stored);
        MarketSnapshot? market = null;
        StrategyOutput? output = null;
        string? managementNote = null;
        string? entriesBlocked = null;
        try
        {
            market = await _loader.LoadAsync(agent, strategy, run.Agent.Key, candle, cancellable).ConfigureAwait(false);
            output = _engine.Evaluate(new StrategyContext(agent, strategy, parameters, market, entriesAllowed));
            strategyJson = JsonSerializer.SerializeToElement(output, Json);

            var (openTrades, openOrders) = await LoadOpenAsync(agentId, cancellable).ConfigureAwait(false);
            var venue = _venues.For(agent.Mode);

            // FR-EXE-03/06/08: the rules manage what is open before the council is asked; a fallback entry placed here
            // takes the setup off the table for this candle.
            if (openTrades.Count > 0 || openOrders.Count > 0)
            {
                var request = new ExecutionRequest(agent, evaluation, strategy, parameters, output, market, openTrades, openOrders, _caps.Value, venue, exchangeKey, entriesAllowed, run.DeadlineAt);
                var managed = await ManageAsync(request, cancellable, stopping).ConfigureAwait(false);
                if (managed is null)
                {
                    // NFR-REL-03: the open state could not be managed, so nothing new opens this candle.
                    entriesBlocked = "Position management failed; see the log.";
                    entriesAllowed = false;
                }
                else
                {
                    managementNote = managed.Note;
                    fault ??= managed.Fault;
                    if (managed.EntryPlaced)
                        entriesAllowed = false;
                    if (managed.EntriesBlockedReason is { } blocked)
                    {
                        entriesBlocked = blocked;
                        entriesAllowed = false;
                    }

                    (openTrades, openOrders) = await LoadOpenAsync(agentId, cancellable).ConfigureAwait(false);
                }
            }

            // FR-AI-08, FR-AGT-05, NFR-REL-03, NFR-COST-01: the council is asked for an open position, or when an entry is
            // possible and wanted (a Paused agent, one whose open state could not be managed, a no-entry window or a full
            // position cap opens nothing); otherwise the evaluation ends here with the gate's reason.
            var need = CouncilGate.Decide(agent, output, entriesAllowed, entriesBlocked, openTrades, openOrders, _caps.Value.MaxOpenPositions, candle.OpenTime + agent.Timeframe.ToTimeSpan());
            if (!need.Needed)
            {
                outcome = EvaluationOutcome.NoAction;
                detail = Join(managementNote, need.Detail);
            }
            else
            {
                councilConsulted = true;
                var context = new CouncilContext(agent, evaluation, strategy, parameters, output, market, openTrades, openOrders, entriesAllowed, run.DeadlineAt);
                var council = await _council.DeliberateAsync(context, cancellable).ConfigureAwait(false);

                // FR-AI-14: the deliberation is kept whether or not the Risk Manager decided.
                if (council.Decision is { } produced)
                {
                    decision = produced;
                    if (decision.Id == Guid.Empty)
                        decision.Id = Guid.CreateVersion7();
                    decision.EvaluationId = evaluation.Id;
                    if (decision.CreatedAt == default)
                        decision.CreatedAt = _time.GetUtcNow();
                    await using (var db = await _factory.CreateDbContextAsync(stopping).ConfigureAwait(false))
                    {
                        db.Decisions.Add(decision);
                        await db.SaveChangesAsync(stopping).ConfigureAwait(false);
                    }

                    _events.Publish(new DecisionRecorded(agentId, agent.UserId, decision.CreatedAt, evaluation.Id, decision.Id, decision.FinalAction));
                }

                if (council.Failure is not null || council.Fault is not null)
                {
                    // FR-AI-12: no Risk Manager decision opens nothing and leaves protective orders alone. A fault
                    // (FR-SYNC-04) moves the agent to Error once the evaluation is recorded.
                    outcome = EvaluationOutcome.Failed;
                    detail = $"No decision: {council.Failure ?? council.Fault}";
                    fault = council.Fault;
                }
                else if (decision is null)
                {
                    outcome = EvaluationOutcome.Failed;
                    detail = "No decision: the council produced none";
                }
                else if (!host.IsTracked(agentId))
                {
                    outcome = EvaluationOutcome.Failed;
                    detail = "The agent was stopped before the decision could be executed.";
                }
                else
                {
                    // NFR-PERF-01: no position is opened once the deadline, or a lifecycle action, has cut the run. A Close or
                    // Modify executes on the stopping token whatever the time, the deadline waived, because dropping protection
                    // for a whole candle is worse than a late finish; a placement under way is never interrupted either way.
                    var late = cancellable.IsCancellationRequested || _time.GetUtcNow() >= run.DeadlineAt;
                    var opens = decision.FinalAction is DecisionAction.OpenLong or DecisionAction.OpenShort;
                    var protective = decision.FinalAction is DecisionAction.Close or DecisionAction.Modify;
                    if (late && opens)
                    {
                        if (stopping.IsCancellationRequested)
                            throw new OperationCanceledException(stopping);
                        outcome = EvaluationOutcome.Failed;
                        detail = _time.GetUtcNow() >= run.DeadlineAt
                            ? $"The deadline of {run.DeadlineAt:HH:mm:ss} UTC passed before the decision could be executed; nothing was placed."
                            : "The evaluation was cancelled by a lifecycle action before the decision could be executed; nothing was placed.";
                    }
                    else
                    {
                        var request = new ExecutionRequest(agent, evaluation, strategy, parameters, output, market, openTrades, openOrders, _caps.Value, venue, exchangeKey, entriesAllowed, run.DeadlineAt);
                        var result = await _execution.ExecuteAsync(request, decision, protective ? stopping : cancellable, stopping).ConfigureAwait(false);
                        outcome = EvaluationOutcome.Decided;
                        detail = Join(managementNote, result.Note);
                        fault ??= result.Fault;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            outcome = EvaluationOutcome.Failed;
            detail = "Interrupted by shutdown.";
        }
        catch (OperationCanceledException) when (cancellable.IsCancellationRequested)
        {
            outcome = EvaluationOutcome.Failed;
            detail = _time.GetUtcNow() >= run.DeadlineAt
                ? $"The deadline of {run.DeadlineAt:HH:mm:ss} UTC passed before the evaluation finished."
                : "The evaluation was cancelled by a lifecycle action.";
        }
        catch (Exception ex)
        {
            outcome = EvaluationOutcome.Failed;
            detail = Truncate(ex.Message);
            _logger.LogError(ex, "Evaluation {EvaluationId} of agent {AgentId} for candle {CandleOpen:O} failed", evaluation.Id, agentId, candle.OpenTime);
        }

        if (market is not null && output is not null)
            await TrackExperienceAsync(new ExperienceContext(agent, evaluation, output, parameters, market, decision, entriesAllowed), stopping).ConfigureAwait(false);

        var finishedAt = _time.GetUtcNow();
        await FinishAsync(evaluation.Id, strategyJson, councilConsulted, outcome, Truncate(detail), finishedAt).ConfigureAwait(false);
        // NFR-PERF-01: how long the run took and where it ended relative to the close and the deadline; a miss is a Warning, whatever cut it.
        var overrun = finishedAt - run.DeadlineAt;
        var missed = overrun >= TimeSpan.Zero;
        _logger.Log(missed ? LogLevel.Warning : LogLevel.Information,
            "Agent {AgentId} evaluated candle {CandleOpen:O} ({Timeframe}): {Outcome} in {Elapsed}, {SinceClose} after the close{Overrun}{Detail}",
            agentId, candle.OpenTime, agent.Timeframe.ToLabel(), outcome, Seconds(finishedAt - evaluation.StartedAt), Seconds(finishedAt - (candle.OpenTime + agent.Timeframe.ToTimeSpan())),
            missed ? $", {Seconds(overrun)} past the deadline" : "", detail is null ? "" : " - " + detail);
        _events.Publish(new EvaluationFinished(agentId, agent.UserId, finishedAt, evaluation.Id, candle.OpenTime, outcome));

        // FR-SYNC-04, FR-EXE-11: an agent whose council or venue cannot run as configured stops being evaluated until the user fixes it.
        if (fault is not null)
            await host.FailAsync(agentId, fault, stopping).ConfigureAwait(false);
        return missed ? EvaluationRunResult.MissedDeadline : EvaluationRunResult.Finished;
    }

    private async Task<(IReadOnlyList<Trade> Trades, IReadOnlyList<Order> Orders)> LoadOpenAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var trades = await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId && t.ClosedAt == null).OrderBy(t => t.OpenedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.AgentId == agentId && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .OrderBy(o => o.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (trades, orders);
    }

    /// <summary>The management step; a failure is logged and leaves protection as it is, the evaluation goes on with new entries blocked (NFR-REL-03).</summary>
    private async Task<ExecutionResult?> ManageAsync(ExecutionRequest request, CancellationToken cancellable, CancellationToken stopping)
    {
        try
        {
            return await _execution.ManageAsync(request, cancellable, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || cancellable.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Position management for evaluation {EvaluationId} of agent {AgentId} failed; the protective orders stay as they are", request.Evaluation.Id, request.Agent.Id);
            return null;
        }
    }

    /// <summary>Two detail segments with a space between, either one absent; shared with the council gate.</summary>
    internal static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : $"{first} {second}";

    /// <summary>
    /// Records that a candle was received but not evaluated (stale or late); a second record of the same candle is
    /// ignored and returns false. A candle too late to start is a deadline miss and logged as a Warning (NFR-PERF-01).
    /// </summary>
    public async Task<bool> RecordSkippedAsync(AgentTrackingInfo info, Candle candle, string detail, bool deadlineMissed, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var evaluation = new Evaluation
        {
            Id = Guid.CreateVersion7(),
            AgentId = info.AgentId,
            Symbol = info.Key.Symbol,
            MarketType = info.Key.Market,
            Timeframe = info.Timeframe,
            CandleOpenTime = candle.OpenTime,
            StartedAt = now,
            FinishedAt = now,
            Outcome = EvaluationOutcome.Skipped,
            OutcomeDetail = Truncate(detail),
        };
        if (!await InsertAsync(evaluation, cancellationToken).ConfigureAwait(false))
            return false;
        _logger.Log(deadlineMissed ? LogLevel.Warning : LogLevel.Information, "Agent {AgentId} skipped candle {CandleOpen:O}: {Detail}", info.AgentId, candle.OpenTime, detail);
        _events.Publish(new EvaluationFinished(info.AgentId, info.UserId, now, evaluation.Id, candle.OpenTime, EvaluationOutcome.Skipped));
        return true;
    }

    /// <summary>False when another run owns the candle. A duplicate of our own row (the retrying strategy re-ran a committed insert) counts as inserted.</summary>
    private async Task<bool> InsertAsync(Evaluation evaluation, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Evaluations.Add(evaluation);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Evaluations.AnyAsync(e => e.Id == evaluation.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>FR-EXP-03 on the evaluation's own market data; a failure is logged and the evaluation's outcome stands (NFR-REL-03).</summary>
    private async Task TrackExperienceAsync(ExperienceContext context, CancellationToken stopping)
    {
        try
        {
            await _experience.TrackAsync(context, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            _logger.LogDebug("Experience tracking for evaluation {EvaluationId} of agent {AgentId} was interrupted by shutdown", context.Evaluation.Id, context.Agent.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Experience tracking for evaluation {EvaluationId} of agent {AgentId} failed; the evaluation stands", context.Evaluation.Id, context.Agent.Id);
        }
    }

    private async Task FinishAsync(Guid evaluationId, JsonElement? strategyOutput, bool councilConsulted, EvaluationOutcome outcome, string? detail, DateTimeOffset finishedAt)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var row = await db.Evaluations.SingleOrDefaultAsync(e => e.Id == evaluationId, CancellationToken.None).ConfigureAwait(false);
            if (row is null)
                return;
            row.StrategyOutput = strategyOutput;
            row.CouncilConsulted = councilConsulted;
            row.Outcome = outcome;
            row.OutcomeDetail = detail;
            row.FinishedAt = finishedAt;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The scheduler's watchdog finalises the row later (FR-LOG-08 in spirit: a lost write never stops the runtime).
            _logger.LogWarning(ex, "Evaluation {EvaluationId} could not be finalised as {Outcome}", evaluationId, outcome);
        }
    }

    private static string? Truncate(string? text) =>
        text is null || text.Length <= MaxDetailLength ? text : text[..MaxDetailLength];

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
