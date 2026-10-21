using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Configuration;
using Ecnanib.Execution.Services.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services;

/// <summary>
/// The "apply hard risk limits → execute" stages (FR-AGT-10, FR-AI-11, FR-EXE-03..06, FR-EXE-09) and the candle-close
/// management before the council. Sizing and the venue-bound checks are recorded in <c>decisions.execution_check</c>;
/// a decision that fails them is rewritten as no-trade the way the council rewrites its own rejections, so the
/// experience module opens its shadow outcome. Faults (hedge mode) travel in the result for the pipeline.
/// </summary>
internal sealed class ExecutionService : IExecutionService
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly ISymbolCatalog _symbols;
    private readonly IDailyLossLimit _dailyLoss;
    private readonly ExchangeReconciler _reconciler;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly IOptions<AgentsOptions> _agents;
    private readonly TimeProvider _time;
    private readonly ILogger<ExecutionService> _logger;

    public ExecutionService(
        IDbContextFactory<EcnanibDbContext> factory, ISymbolCatalog symbols, IDailyLossLimit dailyLoss, ExchangeReconciler reconciler,
        IOptions<ExecutionOptions> options, IOptions<AgentsOptions> agents, TimeProvider time, ILogger<ExecutionService> logger)
    {
        _factory = factory;
        _symbols = symbols;
        _dailyLoss = dailyLoss;
        _reconciler = reconciler;
        _options = options;
        _agents = agents;
        _time = time;
        _logger = logger;
    }

    // --- Management before the council -----------------------------------------------------------------------------

    public async Task<ExecutionResult> ManageAsync(ExecutionRequest request, CancellationToken cancellable, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = request.Agent;
        var notes = new List<string>();
        var orders = new List<Order>();
        var entryPlaced = false;
        string? fault = null;
        using var budget = new CancellationTokenSource(_options.Value.ManageBudget, _time);
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellable, budget.Token);
        try
        {
            if (request.InterruptedPreviously && agent.Mode == AgentMode.Live)
            {
                try
                {
                    await _reconciler.ReconcileAgentAsync(agent, request.ExchangeKey, reads.Token).ConfigureAwait(false);
                    notes.Add("Reconciled with the exchange after the restart.");
                }
                catch (OperationCanceledException) when (reads.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent {AgentId} could not be reconciled after the restart; the open state stands", agent.Id);
                    notes.Add("Reconciliation after the restart failed; see the log.");
                }
            }

            var filters = await FiltersAsync(request, reads.Token).ConfigureAwait(false);
            var lastClose = request.Market.Latest?.Close ?? 0m;

            // Each step is guarded on its own (StepAsync), so a failure in one leaves the others alone and keeps what was already placed, a
            // market fallback above all, in the result the pipeline reads (FR-EXE-03).
            var expired = new HashSet<Guid>();
            foreach (var entry in request.OpenOrders.Where(o => o.Purpose == OrderPurpose.Entry && o.ExpiresAt is { } at && at <= request.CandleClose))
            {
                await StepAsync("Expiring the entry", async () =>
                {
                    var ended = await request.Venue.ExpireEntryAsync(agent, entry, stopping).ConfigureAwait(false);
                    if (ended.FilledQuantity > 0 || ended.Status == OrderStatus.Filled)
                    {
                        notes.Add("The entry filled at its timeout.");
                        return;
                    }

                    expired.Add(entry.Id);
                    notes.Add($"The {entry.Type.ToString().ToLowerInvariant()} entry expired unfilled.");
                    if (entryPlaced || !PositionManager.MarketFallback(request.Parameters) || entry.DecisionId is not { } decisionId)
                        return;
                    var fallback = await FallbackAsync(request, entry, expired, decisionId, filters, lastClose, reads.Token, stopping).ConfigureAwait(false);
                    if (fallback.Order is not null)
                    {
                        orders.Add(fallback.Order);
                        entryPlaced = true;
                    }

                    notes.Add(fallback.Note);
                }).ConfigureAwait(false);
            }

            // FR-EXE-06: breakeven and trailing per open trade once its partial filled.
            foreach (var trade in request.OpenTrades.Where(t => !t.IsUnmanaged))
            {
                var change = PositionManager.Advise(trade, request.Output, request.Parameters, filters, lastClose);
                if (change is null)
                    continue;
                await StepAsync("Moving the stop", async () =>
                {
                    await request.Venue.ReplaceStopAsync(agent, trade, change.Stop, change.Reason, null, stopping).ConfigureAwait(false);
                    notes.Add($"Stop {change.Reason}: {Sizer.N(change.Stop)}.");
                    _logger.LogInformation("Agent {AgentId} trade {TradeId}: {Detail}", agent.Id, trade.Id, change.Detail);
                }).ConfigureAwait(false);
            }

            // The single-protection spot fallback: the partial is taken at candle close when the first target traded, when both parts stay executable.
            foreach (var trade in request.OpenTrades.Where(t => !t.IsUnmanaged && TradeMath.OpenSize(t) >= t.Size))
            {
                var targets = TradeJson.Targets(trade.Targets);
                var takeProfits = request.OpenOrders.Count(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit);
                if (targets.Count < 2 || takeProfits != 1 || lastClose <= 0)
                    continue;
                var reached = trade.Side == PositionSide.Long ? lastClose >= targets[0] : lastClose <= targets[0];
                if (!reached)
                    continue;
                var open = TradeMath.OpenSize(trade);
                var quantity = ProtectionUnits.Floor(open * ProtectionUnits.PartialFraction(request.Parameters), filters?.StepSize ?? 0m);
                if (quantity >= open || PartialProblem(filters, open, quantity, lastClose) is not null)
                    continue;
                await StepAsync("Taking the partial", async () =>
                {
                    var partial = await request.Venue.PlacePartialCloseAsync(agent, trade, quantity, trade.EntryDecisionId, stopping).ConfigureAwait(false);
                    orders.Add(partial);
                    notes.Add($"Partial of {Sizer.N(quantity)} taken at the first target.");
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellable.IsCancellationRequested)
        {
            notes.Add("Management skipped: its time budget passed; protection is unchanged.");
            _logger.LogWarning("Position management of agent {AgentId} ran out of its {Budget} budget", agent.Id, _options.Value.ManageBudget);
        }
        catch (ExecutionFaultException ex)
        {
            fault = ex.Message;
        }
        catch (ExecutionUnavailableException ex)
        {
            notes.Add(ex.Message);
            _logger.LogWarning("Position management of agent {AgentId}: {Reason}", agent.Id, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add("Management failed; see the log.");
            _logger.LogWarning(ex, "Position management of agent {AgentId} failed; what was placed before stays in the result", agent.Id);
        }

        return new ExecutionResult(orders, notes.Count == 0 ? null : string.Join(" ", notes), fault, entryPlaced);

        async Task StepAsync(string what, Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExecutionFaultException)
            {
                throw;
            }
            catch (ExecutionUnavailableException ex)
            {
                notes.Add(ex.Message);
                _logger.LogWarning("Position management of agent {AgentId}, {What}: {Reason}", agent.Id, what.ToLowerInvariant(), ex.Message);
            }
            catch (Exception ex)
            {
                notes.Add($"{what} failed; see the log.");
                _logger.LogWarning(ex, "Position management of agent {AgentId}: {What} failed; protection is unchanged", agent.Id, what.ToLowerInvariant());
            }
        }
    }

    /// <summary>FR-EXE-03 market fallback: the time-dependent guards again (entries that expired this evaluation no longer count as open), the venue-bound checks at the last close with the decision's stop, then a market entry.</summary>
    private async Task<(Order? Order, string Note)> FallbackAsync(ExecutionRequest request, Order entry, IReadOnlySet<Guid> expired, Guid decisionId, SymbolFilters? filters, decimal lastClose, CancellationToken reads, CancellationToken stopping)
    {
        var agent = request.Agent;
        var checks = new List<LimitCheck>();
        Decision? decision;
        await using (var db = await _factory.CreateDbContextAsync(reads).ConfigureAwait(false))
            decision = await db.Decisions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == decisionId, reads).ConfigureAwait(false);
        if (decision is null || decision.StopPrice is not { } stop || lastClose <= 0 || filters is null)
            return (null, "No market fallback: the decision, its stop or the price is unavailable.");

        var side = entry.Side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short;
        var window = agent.EntryWindows.FirstOrDefault(w => EntryWindowSchedule.IsActive(w, request.CandleClose));
        Add(checks, LimitNames.EntriesAllowed, request.EntriesAllowed, "new entries are allowed", "the agent opens nothing new while paused");
        Add(checks, LimitNames.NoEntryWindow, window is null, "no no-entry window is active", "a no-entry window is active at the candle close");
        var cap = Math.Min(agent.RiskLimits.MaxOpenPositions, request.Caps.MaxOpenPositions);
        var open = request.OpenTrades.Count + request.OpenOrders.Count(o => o.Purpose == OrderPurpose.Entry && !expired.Contains(o.Id));
        Add(checks, LimitNames.OpenPositions, open < cap, $"{open} of {cap} open", $"{open} of {cap} already open");
        Add(checks, LimitNames.StopSide, side == PositionSide.Long ? stop < lastClose : stop > lastClose, $"stop {Sizer.N(stop)} is on the losing side of the last close {Sizer.N(lastClose)}", $"stop {Sizer.N(stop)} is not on the losing side of the last close {Sizer.N(lastClose)}");
        var daily = await _dailyLoss.CheckAsync(agent.Id, reads).ConfigureAwait(false);
        Add(checks, ExecutionLimitNames.DailyLoss, !daily.Reached, "the daily loss limit is not reached", "the daily loss limit is reached");

        SizingResult? sizing = null;
        if (checks.All(c => c.Status != LimitCheckStatus.Fail))
        {
            var equity = await request.Venue.GetEquityAsync(agent, reads).ConfigureAwait(false);
            var available = await request.Venue.GetAvailableAsync(agent, reads).ConfigureAwait(false);
            var risk = decision.RiskPercent ?? Math.Min(agent.RiskLimits.RiskPerTradePercent, request.Caps.MaxRiskPerTradePercent);
            sizing = Sizer.Size(new SizingInput(side, lastClose, stop, risk, equity, available, request.Output.Setup?.SizeMultiplier ?? 1m, filters, agent.RiskLimits.MaxNotional, Math.Max(1, agent.Leverage ?? 1), true, agent.MarketType));
            checks.AddRange(sizing.Checks);
        }

        var rejected = checks.Any(c => c.Status == LimitCheckStatus.Fail);
        checks.Insert(0, new LimitCheck(ExecutionLimitNames.MarketFallback, rejected ? LimitCheckStatus.Fail : LimitCheckStatus.Pass, rejected ? "the expired limit entry is not replaced at market" : $"the expired limit entry is replaced by a market entry at about {Sizer.N(lastClose)}"));
        await AppendExecutionCheckAsync(decisionId, checks, null, stopping).ConfigureAwait(false);
        if (rejected || sizing is null)
            return (null, $"No market fallback ({string.Join(", ", checks.Where(c => c.Status == LimitCheckStatus.Fail).Select(c => c.Limit))}).");

        var placement = new EntryPlacement(decisionId, side, OrderType.Market, null, sizing.Quantity, sizing.Stop, TradeJson.Targets(decision.Targets), ProtectionUnits.PartialFraction(request.Parameters), null, Sequence: 1);
        var order = await request.Venue.PlaceEntryAsync(agent, placement, stopping).ConfigureAwait(false);
        _logger.LogInformation("Agent {AgentId}: market fallback of {Quantity} {Symbol} placed for decision {DecisionId} (FR-EXE-03)", agent.Id, sizing.Quantity, agent.Symbol, decisionId);
        return (order, $"Market fallback placed for {Sizer.N(sizing.Quantity)} {agent.Symbol}.");
    }

    /// <summary>
    /// Why a partial of <paramref name="quantity"/> out of <paramref name="open"/> cannot be taken: the part taken or the part left would be
    /// below the exchange minimums, and a remainder under them is dust no stop or close could ever execute.
    /// </summary>
    private static (string Detail, string Note)? PartialProblem(SymbolFilters? filters, decimal open, decimal quantity, decimal reference)
    {
        var minimum = Math.Max(filters?.MinQuantity ?? 0m, filters?.MarketMinQuantity ?? 0m);
        if (quantity <= 0 || quantity < minimum || (filters is not null && !filters.MeetsMinNotional(reference, quantity, true)))
            return ($"{Sizer.N(quantity)} is below the exchange minimum; no partial is taken", "No partial: below the exchange minimum.");
        var remainder = open - quantity;
        if (remainder < minimum || (filters is not null && !filters.MeetsMinNotional(reference, remainder, true)))
            return ($"the remaining {Sizer.N(remainder)} would be below the exchange minimum; no partial is taken", "No partial: the remainder would be below the exchange minimum.");
        return null;
    }

    // --- The decision ---------------------------------------------------------------------------------------------

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, Decision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        try
        {
            return decision.FinalAction switch
            {
                DecisionAction.OpenLong or DecisionAction.OpenShort => await OpenAsync(request, decision, cancellationToken).ConfigureAwait(false),
                DecisionAction.Modify => await ModifyAsync(request, decision, cancellationToken).ConfigureAwait(false),
                DecisionAction.Close => await CloseAsync(request, decision, cancellationToken).ConfigureAwait(false),
                DecisionAction.Hold => ExecutionResult.Nothing("Holding the position."),
                _ => ExecutionResult.Nothing("No trade."),
            };
        }
        catch (ExecutionFaultException ex)
        {
            return new ExecutionResult([], ex.Message, Fault: ex.Message);
        }
        catch (ExecutionUnavailableException ex)
        {
            _logger.LogWarning("Agent {AgentId} decision {DecisionId} could not be executed: {Reason}", request.Agent.Id, decision.Id, ex.Message);
            return ExecutionResult.Nothing($"Not executed: {ex.Message}");
        }
    }

    private async Task<ExecutionResult> OpenAsync(ExecutionRequest request, Decision decision, CancellationToken cancellationToken)
    {
        var agent = request.Agent;
        var side = decision.FinalAction == DecisionAction.OpenLong ? PositionSide.Long : PositionSide.Short;
        var checks = new List<LimitCheck>();
        var filters = await FiltersAsync(request, cancellationToken).ConfigureAwait(false);
        var lastClose = request.Market.Latest?.Close ?? 0m;
        var market = decision.EntryType == OrderType.Market;
        var price = market ? lastClose : decision.EntryPrice ?? 0m;
        if (filters is null || decision.StopPrice is not { } stop || price <= 0)
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.Size, LimitCheckStatus.Fail, filters is null ? "the symbol's filters are unavailable" : decision.StopPrice is null ? "no stop was given" : "no entry price is available"));
            return await RejectAsync(decision, checks, cancellationToken).ConfigureAwait(false);
        }

        var daily = await _dailyLoss.CheckAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        checks.Add(new LimitCheck(ExecutionLimitNames.DailyLoss, daily.Reached ? LimitCheckStatus.Fail : LimitCheckStatus.Pass,
            daily.Reached ? $"the daily loss of {Sizer.N(daily.LossToday)} reached the limit of {Sizer.N(daily.LimitAmount)}" : daily.LossToday > 0 ? $"a daily loss of {Sizer.N(daily.LossToday)} is under the limit of {Sizer.N(daily.LimitAmount)}" : "no loss today"));
        if (daily.Reached)
            return await RejectAsync(decision, checks, cancellationToken).ConfigureAwait(false);

        var equity = await request.Venue.GetEquityAsync(agent, cancellationToken).ConfigureAwait(false);
        var available = await request.Venue.GetAvailableAsync(agent, cancellationToken).ConfigureAwait(false);
        var risk = decision.RiskPercent ?? Math.Min(agent.RiskLimits.RiskPerTradePercent, request.Caps.MaxRiskPerTradePercent);
        var sizing = Sizer.Size(new SizingInput(side, price, stop, risk, equity, available, request.Output.Setup?.SizeMultiplier ?? 1m, filters, agent.RiskLimits.MaxNotional, Math.Max(1, agent.Leverage ?? 1), market, agent.MarketType));
        checks.AddRange(sizing.Checks);
        if (sizing.Rejected)
            return await RejectAsync(decision, checks, cancellationToken).ConfigureAwait(false);

        var timeout = PositionManager.EntryTimeoutCandles(request.Parameters, _agents.Value.DefaultEntryTimeoutCandles);
        var expiresAt = market ? (DateTimeOffset?)null : request.CandleClose + agent.Timeframe.ToTimeSpan() * timeout;
        var targets = TradeJson.Targets(decision.Targets).Select(t => side == PositionSide.Long ? filters.RoundPriceUpToTick(t) : filters.RoundPriceDownToTick(t)).ToList();
        var placement = new EntryPlacement(decision.Id, side, market ? OrderType.Market : OrderType.Limit, market ? null : sizing.Price, sizing.Quantity, sizing.Stop, targets, ProtectionUnits.PartialFraction(request.Parameters), expiresAt);
        var order = await request.Venue.PlaceEntryAsync(agent, placement, cancellationToken).ConfigureAwait(false);
        await AppendExecutionCheckAsync(decision.Id, checks, sizing.Quantity, cancellationToken).ConfigureAwait(false);
        decision.Size = sizing.Quantity;
        decision.ExecutionCheck = ToJson(checks);

        var kind = market ? "Market" : "Limit";
        var note = $"{kind} {side.ToString().ToLowerInvariant()} {Sizer.N(sizing.Quantity)} {agent.Symbol}" + (market ? "" : $" at {Sizer.N(sizing.Price)}") + $" (stop {Sizer.N(sizing.Stop)})" + (expiresAt is { } e ? $", expires {e:HH:mm} UTC" : "") + ".";
        var clamped = checks.Where(c => c.Status == LimitCheckStatus.Clamped).Select(c => c.Limit).ToList();
        if (clamped.Count > 0)
            note += $" Clamped: {string.Join(", ", clamped)}.";
        _logger.LogInformation("Agent {AgentId} decision {DecisionId}: {Note}", agent.Id, decision.Id, note);
        return new ExecutionResult([order], note);
    }

    private async Task<ExecutionResult> ModifyAsync(ExecutionRequest request, Decision decision, CancellationToken cancellationToken)
    {
        var agent = request.Agent;
        var trade = request.OpenTrades.FirstOrDefault(t => t.Id == decision.TradeId) ?? request.OpenTrades.FirstOrDefault();
        if (trade is null)
            return ExecutionResult.Nothing("Nothing to modify: no position is open.");
        var filters = await FiltersAsync(request, cancellationToken).ConfigureAwait(false);
        var lastClose = request.Market.Latest?.Close ?? 0m;
        var isLong = trade.Side == PositionSide.Long;
        var notes = new List<string>();
        var orders = new List<Order>();
        var checks = new List<LimitCheck>();

        if (decision.StopPrice is { } proposed)
        {
            var stop = PositionManager.Round(proposed, isLong, filters);
            var tightens = trade.CurrentStop is not { } current || (isLong ? stop > current : stop < current);
            var safe = lastClose <= 0 || (isLong ? stop < lastClose : stop > lastClose);
            if (tightens && safe)
            {
                orders.AddRange(await request.Venue.ReplaceStopAsync(agent, trade, stop, "council", decision.Id, cancellationToken).ConfigureAwait(false));
                notes.Add($"Stop moved to {Sizer.N(stop)}.");
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Pass, $"stop {Sizer.N(stop)}"));
            }
            else
            {
                checks.Add(new LimitCheck(LimitNames.StopTightenOnly, LimitCheckStatus.Clamped, tightens ? $"stop {Sizer.N(stop)} would trigger against the last close {Sizer.N(lastClose)}; the stop stays" : $"stop {Sizer.N(stop)} does not tighten the current stop; it stays"));
                notes.Add("The stop stays.");
            }
        }

        var targets = TradeJson.Targets(decision.Targets);
        if (targets.Count > 0)
        {
            var placed = await request.Venue.ReplaceTargetsAsync(agent, trade, targets, decision.Id, cancellationToken).ConfigureAwait(false);
            orders.AddRange(placed);
            notes.Add(placed.Count > 0 ? $"Targets set to {string.Join(", ", targets.Select(Sizer.N))}." : "Targets unchanged.");
        }

        if (decision.PartialFraction is { } fraction)
        {
            var open = TradeMath.OpenSize(trade);
            var quantity = ProtectionUnits.Floor(open * Math.Clamp(fraction, 0m, 1m), filters?.StepSize ?? 0m);
            var reference = lastClose > 0 ? lastClose : trade.EntryPrice;
            if (quantity >= open)
            {
                orders.Add(await request.Venue.PlaceCloseAsync(agent, trade, decision.Id, cancellationToken).ConfigureAwait(false));
                checks.Add(new LimitCheck(ExecutionLimitNames.PartialFraction, LimitCheckStatus.Clamped, "the fraction takes the whole position; it is closed"));
                notes.Add("The whole position is closed at market.");
            }
            else if (PartialProblem(filters, open, quantity, reference) is { } problem)
            {
                checks.Add(new LimitCheck(ExecutionLimitNames.PartialFraction, LimitCheckStatus.Clamped, problem.Detail));
                notes.Add(problem.Note);
            }
            else
            {
                orders.Add(await request.Venue.PlacePartialCloseAsync(agent, trade, quantity, decision.Id, cancellationToken).ConfigureAwait(false));
                checks.Add(new LimitCheck(ExecutionLimitNames.PartialFraction, LimitCheckStatus.Pass, $"{Sizer.N(quantity)} of {Sizer.N(open)} taken at market"));
                notes.Add($"Partial of {Sizer.N(quantity)} taken at market.");
            }
        }

        if (checks.Count > 0)
        {
            await AppendExecutionCheckAsync(decision.Id, checks, null, cancellationToken).ConfigureAwait(false);
            decision.ExecutionCheck = ToJson(checks);
        }

        return new ExecutionResult(orders, notes.Count == 0 ? "Nothing to modify." : string.Join(" ", notes));
    }

    private async Task<ExecutionResult> CloseAsync(ExecutionRequest request, Decision decision, CancellationToken cancellationToken)
    {
        var trade = request.OpenTrades.FirstOrDefault(t => t.Id == decision.TradeId) ?? request.OpenTrades.FirstOrDefault();
        if (trade is null)
            return ExecutionResult.Nothing("Nothing to close: no position is open.");
        var order = await request.Venue.PlaceCloseAsync(request.Agent, trade, decision.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent {AgentId} decision {DecisionId}: position {TradeId} closing at market", request.Agent.Id, decision.Id, trade.Id);
        return new ExecutionResult([order], "Closing the position at market.");
    }

    /// <summary>A rejected entry is recorded as no-trade with the failed limits in front of the rationale, as the council does, so a shadow outcome follows it (FR-EXP-03).</summary>
    private async Task<ExecutionResult> RejectAsync(Decision decision, List<LimitCheck> checks, CancellationToken cancellationToken)
    {
        var failed = checks.Where(c => c.Status == LimitCheckStatus.Fail).Select(c => c.Limit).ToList();
        var prefix = $"Rejected by execution limits ({string.Join(", ", failed)}): ";
        var rationale = Truncate(prefix + decision.Rationale, 4000);
        var json = ToJson(checks);
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await db.Decisions.Where(d => d.Id == decision.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.FinalAction, DecisionAction.NoTrade)
                    .SetProperty(d => d.Rationale, rationale)
                    .SetProperty(d => d.Size, (decimal?)null)
                    .SetProperty(d => d.ExecutionCheck, json), cancellationToken).ConfigureAwait(false);
        }

        decision.FinalAction = DecisionAction.NoTrade;
        decision.Rationale = rationale;
        decision.Size = null;
        decision.ExecutionCheck = json;
        var detail = string.Join("; ", checks.Where(c => c.Status == LimitCheckStatus.Fail).Select(c => $"{c.Limit}: {c.Detail}"));
        _logger.LogInformation("Decision {DecisionId} rejected by execution limits: {Detail}", decision.Id, detail);
        return ExecutionResult.Nothing($"Rejected by execution limits: {detail}.");
    }

    private async Task AppendExecutionCheckAsync(Guid decisionId, IReadOnlyList<LimitCheck> checks, decimal? size, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Decisions.SingleOrDefaultAsync(d => d.Id == decisionId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return;
        var existing = Checks(row.ExecutionCheck);
        row.ExecutionCheck = ToJson(existing.Concat(checks).ToList());
        if (size is not null)
            row.Size = size;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SymbolFilters?> FiltersAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var environment = request.ExchangeKey?.Environment ?? ExchangeEnvironment.Production;
        try
        {
            var symbol = await _symbols.FindAsync(environment, request.Agent.MarketType, request.Agent.Symbol, cancellationToken).ConfigureAwait(false);
            return symbol?.Filters;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The filters of {Symbol} on {Market} {Environment} could not be read", request.Agent.Symbol, request.Agent.MarketType, environment);
            return null;
        }
    }

    internal static JsonElement ToJson(IReadOnlyList<LimitCheck> checks) =>
        JsonSerializer.SerializeToElement(checks.Select(c => new { limit = c.Limit, status = c.Status.ToString().ToLowerInvariant(), detail = c.Detail }));

    internal static List<LimitCheck> Checks(JsonElement? json)
    {
        var result = new List<LimitCheck>();
        if (json is not { ValueKind: JsonValueKind.Array } array)
            return result;
        foreach (var item in array.EnumerateArray())
        {
            var limit = item.TryGetProperty("limit", out var l) ? l.GetString() ?? "" : "";
            var status = item.TryGetProperty("status", out var s) && Enum.TryParse<LimitCheckStatus>(s.GetString(), true, out var parsed) ? parsed : LimitCheckStatus.Pass;
            var detail = item.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
            result.Add(new LimitCheck(limit, status, detail));
        }

        return result;
    }

    private static void Add(List<LimitCheck> checks, string limit, bool pass, string passDetail, string failDetail) =>
        checks.Add(new LimitCheck(limit, pass ? LimitCheckStatus.Pass : LimitCheckStatus.Fail, pass ? passDetail : failDetail));

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
