using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A decision cycle (FR-CYC-02): the loss limits, the AI budget, the market and the account, the analysts at once, the
/// Strategist, the Risk Critic for a trade action, then the Risk Engine, with every step in the trace as it happens.
/// It ends with a sized order for execution (#34) or approval (#38). A failed role uses its fallback model, and a
/// required one that fails holds (FR-ROLE-06). See Decision cycle in docs/architecture.md.
/// </summary>
internal sealed class DecisionCycles(
    IAgents agents,
    IExchangeSymbols symbols,
    IMarketData marketData,
    IAgentAccounts accounts,
    ICouncil council,
    IRiskEngine riskEngine,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    CycleObservers observers,
    INotifications notifications,
    ICorrelationContext correlation,
    DecisionCycleSettings settings,
    TimeProvider time,
    ILogger<DecisionCycles> logger) : IDecisionCycles
{
    /// <summary>How long recording an interrupted or failed cycle may take, when the cycle's own tokens have fired.</summary>
    private static readonly TimeSpan LastWriteLimit = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CycleRequest request, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = await agents.FindAsync(request.UserId, request.AgentId, cancellationToken);

        // The scheduler listed it a moment ago, but a pause since then stops new cycles (FR-AGT-06).
        if (agent?.Status != AgentStatus.Active)
        {
            logger.LogInformation(
                "Skipped the decision cycle for the candle that closed at {CandleClose}, because the agent is no longer Active",
                request.CandleClose);

            return;
        }

        var cycle = new CycleRun(NewRow(request, agent), Roles(agent.Settings), contextFactory, observers, notifications, time);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.CycleId] = cycle.Id,
            [LogProperties.Environment] = agent.Settings.Environment.ToString(),
        });

        if (!await cycle.StartAsync(cancellationToken))
        {
            logger.LogWarning("Skipped the decision cycle for the candle that closed at {CandleClose}, because the agent already has one", request.CandleClose);

            return;
        }

        CycleOutcome outcome;
        CycleFailure? failure;

        try
        {
            (outcome, failure) = await DecideAsync(cycle, request, agent, context, cancellationToken);

            var notice = outcome == CycleOutcome.OverBudget ? await BudgetNoticeAsync(request, agent, cancellationToken) : null;

            await cycle.FinishAsync(outcome, failure, notice, cancellationToken);
        }
        catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested)
        {
            await LastWriteAsync(cycle, CycleOutcome.Interrupted, null);

            throw;
        }
        catch (Exception exception)
        {
            // Broad because whatever went wrong, the trace must say the cycle ended. The run records the failure too.
            logger.LogError(exception, "The decision cycle for the candle that closed at {CandleClose} failed", request.CandleClose);

            if (await LastWriteAsync(cycle, CycleOutcome.Failed, CycleFailure.Unexpected))
            {
                using var limit = new CancellationTokenSource(LastWriteLimit);
                await CountErrorsAsync(request.UserId, agent, limit.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            throw;
        }

        logger.LogInformation(
            "The decision cycle for the candle that closed at {CandleClose} ended as {Outcome}, failure {Failure}",
            request.CandleClose,
            outcome,
            failure);

        // After the cycle's end is on record, so a failure here can't change how it ended.
        if (failure is not null)
        {
            await CountErrorsAsync(request.UserId, agent, cancellationToken);
        }
    }

    private async Task<(CycleOutcome Outcome, CycleFailure? Failure)> DecideAsync(
        CycleRun cycle,
        CycleRequest request,
        AgentRecord agent,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var agentSettings = agent.Settings;
        var capital = agentSettings.CapitalAllocation ?? 0m;
        var effective = riskEngine.Caps.Apply(agentSettings.Risk);

        // FR-RISK-08: every cycle, whatever the council decides, so a stop that filled between cycles pauses at once.
        var limits = riskEngine.CheckLossLimits(agentSettings.Risk, capital, TradingRecord.None, time.GetUtcNow());
        await cycle.SaveLossLimitsAsync(limits.Rules, cancellationToken);

        if (limits.Breach is { } breach)
        {
            await agents.PauseForLossLimitAsync(request.UserId, request.AgentId, breach, cancellationToken);

            return (CycleOutcome.PausedForLossLimit, null);
        }

        // FR-CYC-07: once the AI budget has run out, the cycle asks no role, so it makes no entry. The agent stays
        // Active and each cycle checks again, so a new day, a new month or a higher limit ends it. Its positions and
        // their protective orders stay as they are.
        if (agentSettings.Budget != AiBudget.None)
        {
            var budget = await CheckBudgetAsync(request, agentSettings.Budget, cancellationToken);
            await cycle.SaveBudgetAsync(budget, cancellationToken);

            if (budget.RanOut)
            {
                logger.LogInformation(
                    "The agent's AI budget has run out, its {Limit} limit, until {Until}, so the cycle asks no role", budget.Reached, budget.Until);

                return (CycleOutcome.OverBudget, null);
            }
        }

        // Steps 2 to 4: the pair, the market at the candle's close, and the account.
        var collected = await CollectAsync(request, agent, context, cancellationToken);

        if (collected.Failure is { } failure)
        {
            return (CycleOutcome.Failed, failure);
        }

        var (symbol, snapshot, account) = (collected.Symbol!, collected.Snapshot!, collected.Account!);
        await cycle.SaveSnapshotAsync(CycleInputs.Snapshot(snapshot, account, capital, effective), cancellationToken);

        // Step 5: the analysts at once. An AI call is safe to abandon, so they get Stopping.
        var analysts = new PromptContext { Snapshot = snapshot };
        var technicalTask = AskAsync(cycle, request.UserId, agent, AiRole.TechnicalAnalyst, analysts, null, context.Stopping);
        var sentimentTask = AskAsync(cycle, request.UserId, agent, AiRole.SentimentAnalyst, analysts, null, context.Stopping);

        // Both end before either's failure goes further, so neither writes to the trace after the cycle has ended.
        await ((Task)Task.WhenAll(technicalTask, sentimentTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var technical = await technicalTask;
        var sentiment = await sentimentTask;

        if (technical?.Output is not TechnicalAssessment)
        {
            return (CycleOutcome.Hold, CycleFailure.RoleFailed);
        }

        // Earlier roles' output is untrusted: a model can repeat what it read (FR-PRM-06, NFR-SEC-08).
        List<UntrustedContent> external = [RoleOutput(technical.Output)];

        if (sentiment?.Output is { } sentimentOutput)
        {
            external.Add(RoleOutput(sentimentOutput));
        }

        // Step 6: the Strategist, with the account, the position and the limits that hold.
        var decision = new PromptContext
        {
            Snapshot = snapshot,
            Account = CycleInputs.Account(agentSettings.Market, symbol, capital, account, effective),

            // Reconciliation (#35) gives the position, with when it opened, which the prompt needs. None exists before #34.
            Position = null,
            PendingEntry = null,
            Record = TradingRecord.None,
            Risk = effective,
            External = external,
        };

        var strategist = await AskAsync(cycle, request.UserId, agent, AiRole.Strategist, decision, null, context.Stopping);

        if (strategist?.Output is not TradeProposal proposal)
        {
            return (CycleOutcome.Hold, CycleFailure.RoleFailed);
        }

        if (proposal.Action == TradeAction.Hold)
        {
            return (CycleOutcome.Hold, null);
        }

        // Step 7: the Risk Critic, for a trade action, with the proposal it judges.
        var critique = decision with { External = [.. external, RoleOutput(proposal)] };
        var critic = await AskAsync(cycle, request.UserId, agent, AiRole.RiskCritic, critique, proposal, context.Stopping);

        if (critic?.Output is not CriticVerdict verdict)
        {
            return (CycleOutcome.Hold, CycleFailure.RoleFailed);
        }

        if (verdict.Verdict == VerdictKind.Reject)
        {
            return (CycleOutcome.RejectedByCritic, null);
        }

        // Step 8: the Risk Engine, which trusts none of it (FR-RISK-01, NFR-SEC-08).
        var intent = CycleInputs.Intent(proposal, verdict.Modifications, account.Position?.Side);
        var inputs = CycleInputs.Risk(agentSettings.Market, time.GetUtcNow(), agentSettings.Risk, capital, symbol, snapshot, account);
        var evaluation = riskEngine.Evaluate(intent, inputs);

        await cycle.SaveEvaluationAsync(evaluation, cancellationToken);

        if (evaluation.Breach is { } reached)
        {
            await agents.PauseForLossLimitAsync(request.UserId, request.AgentId, reached, cancellationToken);
        }

        if (!evaluation.Passed)
        {
            return (CycleOutcome.BlockedByRiskEngine, null);
        }

        // FR-CYC-06: no new entry after the deadline. Execution (#34) asks again, right before it sends.
        if (TradeActions.IsEntry(proposal.Action) && !request.AllowsNewEntry(time.GetUtcNow()))
        {
            return (CycleOutcome.PastDeadline, null);
        }

        return (CycleOutcome.ReadyToExecute, null);
    }

    private async Task<Collected> CollectAsync(CycleRequest request, AgentRecord agent, JobContext context, CancellationToken cancellationToken)
    {
        var agentSettings = agent.Settings;
        ExchangeSymbolInfo? symbol;
        MarketSnapshot snapshot;

        try
        {
            symbol = await symbols.FindAsync(agentSettings.Market, agentSettings.Environment, agentSettings.Symbol, cancellationToken);

            if (symbol is null)
            {
                logger.LogWarning("Binance no longer lists the agent's pair {Symbol}", agentSettings.Symbol);

                return Collected.Failed(CycleFailure.MarketData);
            }

            snapshot = await SnapshotAsync(request, agentSettings, context, cancellationToken);
        }
        catch (MarketDataException exception)
        {
            logger.LogWarning("Binance couldn't give the market for the cycle: {Failure}", exception.Failure);

            return Collected.Failed(CycleFailure.MarketData);
        }

        // During maintenance Binance can have no candle for a time, and an analysis of the one before would be stale.
        if (snapshot.Candles.Candles.Count == 0 || snapshot.Candles.Candles[^1].OpenTime != snapshot.Candles.LastOpenTime)
        {
            logger.LogWarning("Binance has no candle for the close at {CandleClose}", request.CandleClose);

            return Collected.Failed(CycleFailure.MarketData);
        }

        if (agentSettings.ExchangeCredentialId is not { } credentialId)
        {
            logger.LogWarning("The agent has no exchange key, so its account can't be read");

            return Collected.Failed(CycleFailure.Account);
        }

        try
        {
            var account = await accounts.ReadAsync(
                request.UserId, credentialId, agentSettings.Market, symbol.BaseAsset, symbol.QuoteAsset, cancellationToken);

            return new Collected(symbol, snapshot, account, null);
        }
        catch (AgentAccountException exception)
        {
            logger.LogWarning("The agent's account couldn't be read: {Failure}", exception.Failure);

            return Collected.Failed(CycleFailure.Account);
        }
    }

    /// <summary>The market at the candle's close. Binance's limit is waited out once, when that fits before the deadline.</summary>
    private async Task<MarketSnapshot> SnapshotAsync(
        CycleRequest request, AgentSettings agentSettings, JobContext context, CancellationToken cancellationToken)
    {
        var marketRequest = new MarketDataRequest(
            agentSettings.Market, agentSettings.Environment, agentSettings.Symbol, agentSettings.Timeframe, request.CandleClose);

        try
        {
            return await marketData.CollectAsync(marketRequest, cancellationToken);
        }
        catch (MarketDataException exception) when (exception is { Failure: MarketDataFailure.Throttled, RetryAfter: { } wait }
            && time.GetUtcNow() + wait < request.Deadline)
        {
            logger.LogInformation("Binance is limiting requests, so the cycle waits {RetryAfterMs:0} ms and tries again", wait.TotalMilliseconds);

            // Waiting is safe to abandon, so it ends when shutdown begins.
            await Task.Delay(wait, time, context.Stopping);

            return await marketData.CollectAsync(marketRequest, cancellationToken);
        }
    }

    /// <summary>Asks a role, with the trace saying it's running and then how it ended. Null when the agent has no such role.</summary>
    private async Task<RoleAnswer?> AskAsync(
        CycleRun cycle,
        Guid userId,
        AgentRecord agent,
        AiRole role,
        PromptContext promptContext,
        TradeProposal? proposal,
        CancellationToken stopping)
    {
        if (agent.Settings.RoleOf(role) is not { } assignment)
        {
            return null;
        }

        await cycle.RunningAsync(role, stopping);

        var question = new RoleQuestion(
            userId,
            new PromptRequest(role, agent.Settings.Market, agent.Settings.Timeframe, promptContext)
            {
                CustomInstructions = agent.Settings.CustomInstructions,
            },
            new CouncilModel(assignment.Model.Provider, assignment.Model.ModelId),
            assignment.Fallback is { } fallback ? new CouncilModel(fallback.Provider, fallback.ModelId) : null)
        {
            Proposal = proposal,
        };

        var answer = await council.AskAsync(question, stopping);

        // An optional role that fails is skipped, and the cycle goes on without it (FR-ROLE-06).
        var state = answer.Answered ? RoleState.Done : AgentRoles.IsRequired(role) ? RoleState.Failed : RoleState.Skipped;

        await cycle.FinishRoleAsync(answer, state, SummaryOf(answer.Output), stopping);

        return answer;
    }

    /// <summary>
    /// Counts the cycles that ended without a decision since the agent last became Active, newest first. When the
    /// last ones all did, the agent goes to Error (FR-NTF-01). An interrupted cycle is the application's, so it
    /// doesn't count either way.
    /// </summary>
    private async Task CountErrorsAsync(Guid userId, AgentRecord agent, CancellationToken cancellationToken)
    {
        var limit = settings.CycleErrorsBeforePause;

        if (limit == 0)
        {
            return;
        }

        List<CycleFailure?> recent;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            recent = await context.Set<DecisionCycleRow>()
                .AsNoTracking()
                .Where(cycle => cycle.UserId == userId && cycle.AgentId == agent.Id
                    && cycle.StartedAt >= agent.StatusChangedAt
                    && cycle.Outcome != null && cycle.Outcome != CycleOutcome.Interrupted)
                .OrderByDescending(cycle => cycle.CandleClose)
                .Take(limit)
                .Select(cycle => cycle.Failure)
                .ToListAsync(cancellationToken);
        }

        if (recent.Count == limit && recent.All(failure => failure is not null))
        {
            await agents.EnterErrorAsync(userId, agent.Id, cancellationToken);
        }
    }

    /// <summary>The agent's spend this UTC day and month against its budget. Nothing of this cycle's has been spent yet.</summary>
    private async Task<BudgetCheck> CheckBudgetAsync(CycleRequest request, AiBudget budget, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var spend = await AiSpendings.FindAsync(context, request.UserId, request.AgentId, time.GetUtcNow(), cancellationToken);

        return spend.Check(budget);
    }

    /// <summary>
    /// The notification that the agent has entered protective-only mode (FR-CYC-07, FR-NTF-01). Null when its last
    /// cycle since it became Active was over budget too, so its user has been told already.
    /// </summary>
    private async Task<Notice?> BudgetNoticeAsync(CycleRequest request, AgentRecord agent, CancellationToken cancellationToken)
    {
        CycleOutcome? previous;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            previous = await context.Set<DecisionCycleRow>()
                .AsNoTracking()
                .Where(cycle => cycle.UserId == request.UserId && cycle.AgentId == agent.Id
                    && cycle.StartedAt >= agent.StatusChangedAt
                    && cycle.CandleClose < request.CandleClose
                    && cycle.Outcome != null && cycle.Outcome != CycleOutcome.Interrupted)
                .OrderByDescending(cycle => cycle.CandleClose)
                .Select(cycle => cycle.Outcome)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return previous == CycleOutcome.OverBudget
            ? null
            : new Notice(NotificationType.AgentPausedBudget, request.UserId, $"/agents/{agent.Id}/settings", agent.Settings.Name, agent.IsDemo);
    }

    /// <summary>Records how the cycle ended when its own tokens may have fired. False when even that failed.</summary>
    private async Task<bool> LastWriteAsync(CycleRun cycle, CycleOutcome outcome, CycleFailure? failure)
    {
        try
        {
            using var limit = new CancellationTokenSource(LastWriteLimit);
            await cycle.FinishAsync(outcome, failure, null, limit.Token).WaitAsync(limit.Token);

            return true;
        }
        catch (Exception exception)
        {
            // Broad because the cycle is ending anyway. The next start marks it interrupted.
            logger.LogError(exception, "Couldn't record how decision cycle {CycleId} ended", cycle.Id);

            return false;
        }
    }

    private DecisionCycleRow NewRow(CycleRequest request, AgentRecord agent) => new()
    {
        Id = Guid.NewGuid(),
        UserId = request.UserId,
        AgentId = request.AgentId,
        Market = agent.Settings.Market,
        Environment = agent.Settings.Environment,
        Symbol = agent.Settings.Symbol,
        Timeframe = agent.Settings.Timeframe,
        CandleClose = request.CandleClose,
        CatchUp = request.CatchUp,
        Deadline = request.Deadline,
        StartedAt = time.GetUtcNow(),
        CorrelationId = correlation.CurrentId,
    };

    /// <summary>The roles this agent's cycle may ask, in order. The Reviewer works after a trade closes, not in a cycle (#45).</summary>
    private static IReadOnlyList<AiRole> Roles(AgentSettings agentSettings) =>
    [
        .. new[] { AiRole.TechnicalAnalyst, AiRole.SentimentAnalyst, AiRole.Strategist, AiRole.RiskCritic }
            .Where(role => agentSettings.RoleOf(role) is not null),
    ];

    private static UntrustedContent RoleOutput(RoleOutput output) =>
        new(UntrustedKind.RoleOutput, output.Role.ToString(), OutputContracts.Write(output));

    /// <summary>What the role concluded, for the council view (FR-ROLE-08). A model wrote it.</summary>
    private static string? SummaryOf(RoleOutput? output) => output switch
    {
        TechnicalAssessment technical => technical.Summary,
        SentimentAssessment sentiment => sentiment.Summary,
        TradeProposal proposal => $"{TradeActions.Name(proposal.Action)}: {proposal.Rationale}",
        CriticVerdict verdict => $"{verdict.Verdict.ToString().ToUpperInvariant()}: {verdict.Rationale}",
        _ => null,
    };

    private sealed record Collected(ExchangeSymbolInfo? Symbol, MarketSnapshot? Snapshot, AgentAccount? Account, CycleFailure? Failure)
    {
        public static Collected Failed(CycleFailure failure) => new(null, null, null, failure);
    }
}
