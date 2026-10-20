using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Services;

/// <summary>One candle close to evaluate. <see cref="DeadlineAt"/> is measured from the close (NFR-PERF-01).</summary>
internal sealed record EvaluationRun(AgentTrackingInfo Agent, Candle Candle, DateTimeOffset DeadlineAt, bool InterruptedPreviously);

/// <summary>
/// The FR-AGT-10 pipeline for one agent and one closed candle: load market data → strategy engine → council when
/// needed (FR-AI-08) → execute → persist → log → notify. The evaluation row is inserted first with
/// <see cref="EvaluationOutcome.InProgress"/>; its unique (agent, candle) index makes a second run of the same candle
/// a no-op (FR-AGT-09). Everything up to the council honours the cancellable token (deadline, Stop, shutdown);
/// execution and persistence do not, because a cancelled token does not recall an order already sent.
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
        _venues = venues;
        _keys = keys;
        _events = events;
        _caps = caps;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(EvaluationRun run, IEvaluationHost host, CancellationToken cancellable, CancellationToken stopping)
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
            return;

        // Unrecoverable before anything is recorded: the agent cannot be evaluated as configured (Appendix E → Error).
        var strategy = _catalog.Find(agent.StrategyId, agent.StrategyVersion);
        if (strategy is null)
        {
            await host.FailAsync(agentId, $"Strategy '{agent.StrategyId}' version {agent.StrategyVersion} is not in the catalog; upgrade it or choose another and start the agent again.", stopping).ConfigureAwait(false);
            return;
        }

        var stored = StrategyParameters.Read(agent.Parameters);
        var invalid = strategy.Validate(stored);
        if (invalid.Count > 0)
        {
            await host.FailAsync(agentId, $"The strategy parameters are no longer valid: {string.Join("; ", invalid.Select(e => e.Message))} Edit them and start the agent again.", stopping).ConfigureAwait(false);
            return;
        }

        ExchangeKeyHandle? exchangeKey = null;
        if (agent.Mode == AgentMode.Live)
        {
            if (agent.ExchangeKeyId is not { } keyId)
            {
                await host.FailAsync(agentId, "The exchange key is gone; choose a key and start the agent again.", stopping).ConfigureAwait(false);
                return;
            }

            var resolution = await _keys.ResolveExchangeKeyAsync(agent.UserId, keyId, agent.MarketType, stopping).ConfigureAwait(false);
            if (!resolution.IsResolved)
            {
                await host.FailAsync(agentId, $"The exchange key cannot be used: {resolution.Reason}", stopping).ConfigureAwait(false);
                return;
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
            return;
        }

        var outcome = EvaluationOutcome.Failed;
        string? detail = null;
        var councilConsulted = false;
        JsonElement? strategyJson = null;
        Decision? decision = null;
        string? fault = null;
        try
        {
            var entriesAllowed = agent.State == AgentState.Running;
            var market = await _loader.LoadAsync(agent, strategy, run.Agent.Key, candle, cancellable).ConfigureAwait(false);
            var parameters = strategy.Resolve(stored);
            var output = _engine.Evaluate(new StrategyContext(agent, strategy, parameters, market, entriesAllowed));
            strategyJson = JsonSerializer.SerializeToElement(output, Json);

            IReadOnlyList<Trade> openTrades;
            IReadOnlyList<Order> openOrders;
            await using (var db = await _factory.CreateDbContextAsync(cancellable).ConfigureAwait(false))
            {
                openTrades = await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId && t.ClosedAt == null).OrderBy(t => t.OpenedAt).ToListAsync(cancellable).ConfigureAwait(false);
                openOrders = await db.Orders.AsNoTracking()
                    .Where(o => o.AgentId == agentId && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
                    .OrderBy(o => o.CreatedAt).ToListAsync(cancellable).ConfigureAwait(false);
            }

            // FR-AI-08, FR-AGT-05: a Paused agent opens nothing, so only an open position makes the council worth its cost.
            var councilNeeded = (output.Setup is not null && entriesAllowed) || openTrades.Count > 0 || (agent.Discretionary && entriesAllowed);
            if (!councilNeeded)
            {
                outcome = EvaluationOutcome.NoAction;
                detail = output.Note ?? (entriesAllowed ? "No setup and no open position." : "Paused: no open position to manage.");
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
                    var venue = _venues.For(agent.Mode);
                    var request = new ExecutionRequest(agent, decision, openTrades, openOrders, _caps.Value, venue, run.InterruptedPreviously, exchangeKey);
                    var result = await _execution.ExecuteAsync(request, stopping).ConfigureAwait(false);
                    outcome = EvaluationOutcome.Decided;
                    detail = result.Note;
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

        var finishedAt = _time.GetUtcNow();
        await FinishAsync(evaluation.Id, strategyJson, councilConsulted, outcome, Truncate(detail), finishedAt).ConfigureAwait(false);
        _logger.LogInformation("Agent {AgentId} evaluated candle {CandleOpen:O} ({Timeframe}): {Outcome}{Detail}",
            agentId, candle.OpenTime, agent.Timeframe.ToLabel(), outcome, detail is null ? "" : " - " + detail);
        _events.Publish(new EvaluationFinished(agentId, agent.UserId, finishedAt, evaluation.Id, candle.OpenTime, outcome));

        // FR-SYNC-04: an agent whose council cannot run as configured stops being evaluated until the user fixes it.
        if (fault is not null)
            await host.FailAsync(agentId, fault, stopping).ConfigureAwait(false);
    }

    /// <summary>Records that a candle was received but not evaluated (stale or late); a second record of the same candle is ignored.</summary>
    public async Task RecordSkippedAsync(AgentTrackingInfo info, Candle candle, string detail, CancellationToken cancellationToken)
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
            return;
        _logger.LogInformation("Agent {AgentId} skipped candle {CandleOpen:O}: {Detail}", info.AgentId, candle.OpenTime, detail);
        _events.Publish(new EvaluationFinished(info.AgentId, info.UserId, now, evaluation.Id, candle.OpenTime, EvaluationOutcome.Skipped));
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
        catch (DbUpdateException ex) when (CandleStore.IsUniqueViolation(ex))
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Evaluations.AnyAsync(e => e.Id == evaluation.Id, cancellationToken).ConfigureAwait(false);
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
}
