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
/// An Autonomous agent's sized order is then executed (#34), and a Confirm agent's waits for its user's approval, unless
/// it reduces risk and the agent lets those through (FR-APR-01, FR-APR-04). A failed
/// role uses its fallback model, and a required one that fails holds (FR-ROLE-06). See Decision cycle in
/// docs/architecture.md.
/// </summary>
internal sealed class DecisionCycles(
    IAgents agents,
    CycleData data,
    IOrderExecution execution,
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
        var record = await data.RecordAsync(request.UserId, agent, request.CandleClose, cancellationToken);
        var limits = riskEngine.CheckLossLimits(agentSettings.Risk, capital, record, time.GetUtcNow());
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
        var collected = await data.CollectAsync(request.UserId, agent, request.CandleClose, request.Deadline, context, cancellationToken);

        if (collected.Failure is { } failure)
        {
            return (CycleOutcome.Failed, failure);
        }

        var (symbol, snapshot, account) = (collected.Symbol!, collected.Snapshot!, collected.Account!);
        await cycle.SaveSnapshotAsync(CycleInputs.Snapshot(snapshot, account, capital, effective), cancellationToken);

        // Reading the account reconciled it, which books a trade whose stop executed since the last pass. Its loss
        // counts now, so this cycle neither enters on it nor skips its cooldown.
        var current = await data.RecordAsync(request.UserId, agent, request.CandleClose, cancellationToken);

        if (current != record)
        {
            record = current;
            limits = riskEngine.CheckLossLimits(agentSettings.Risk, capital, record, time.GetUtcNow());
            await cycle.SaveLossLimitsAsync(limits.Rules, cancellationToken);

            if (limits.Breach is { } late)
            {
                await agents.PauseForLossLimitAsync(request.UserId, request.AgentId, late, cancellationToken);

                return (CycleOutcome.PausedForLossLimit, null);
            }
        }

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

            // As reconciliation left them at step 2, and the Risk Critic sees the same.
            Position = CycleInputs.Position(agentSettings.Market, snapshot, account),
            PendingEntry = CycleInputs.PendingEntry(account),
            Record = record,
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
        var inputs = CycleInputs.Risk(agentSettings.Market, time.GetUtcNow(), agentSettings.Risk, capital, symbol, snapshot, account, record);
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

        // FR-CYC-06: no new entry after the deadline. Execution asks again, right before it sends.
        if (TradeActions.IsEntry(proposal.Action) && !request.AllowsNewEntry(time.GetUtcNow()))
        {
            return (CycleOutcome.PastDeadline, null);
        }

        // Step 9. Confirm mode waits for the user's approval, unless the action reduces risk and the agent lets those
        // through (FR-APR-01, FR-APR-04). The approval commits with the cycle's end.
        if (ApprovalPolicy.NeedsApproval(agentSettings, intent.Action))
        {
            var approval = NewApproval(cycle, request, agent, symbol, snapshot, intent, evaluation.Order!, proposal, verdict);
            cycle.Await(
                approval,
                new Notice(NotificationType.ApprovalPending, request.UserId, Approvals.Link(approval.Id), agentSettings.Name, agent.IsDemo));

            return (CycleOutcome.AwaitingApproval, null);
        }

        // Nothing new is sent once shutdown has begun. What's sent is seen through on the run's token (NFR-REL-04).
        context.Stopping.ThrowIfCancellationRequested();

        return await ExecuteAsync(cycle, request, agent, symbol, evaluation.Order!, proposal, cancellationToken);
    }

    /// <summary>
    /// Sends the sized order and protects what fills (FR-EXE-01, FR-EXE-02), then records what happened on the cycle.
    /// A Futures account only its user can fix puts the agent in Error at once, because every later cycle would spend AI
    /// on an entry that can't be sent (FR-RISK-09).
    /// </summary>
    private async Task<(CycleOutcome Outcome, CycleFailure? Failure)> ExecuteAsync(
        CycleRun cycle,
        CycleRequest request,
        AgentRecord agent,
        ExchangeSymbolInfo symbol,
        SizedOrder order,
        TradeProposal proposal,
        CancellationToken cancellationToken)
    {
        var agentSettings = agent.Settings;
        var result = await execution.ExecuteAsync(
            new ExecutionRequest(
                request.UserId,
                request.AgentId,
                cycle.Id,
                request.CandleClose,
                agentSettings.ExchangeCredentialId!.Value,
                agentSettings.Market,
                agentSettings.Environment,
                symbol.Symbol,
                agentSettings.Timeframe,
                order,
                request.Deadline)
            {
                LimitTtlCandles = proposal.LimitTtlCandles,
            },
            cancellationToken);

        await cycle.SaveExecutionAsync(result);

        logger.LogInformation(
            "Execution ended as {ExecutionStatus}, reason {ExecutionReason}, code {ErrorCode}", result.Status, result.Reason, result.ExchangeCode);

        if (result.Executed)
        {
            return (CycleOutcome.Executed, null);
        }

        switch (result.Reason)
        {
            case ExecutionReason.PastDeadline:
                return (CycleOutcome.PastDeadline, null);

            // Not errors: the entry filled or the position closed meanwhile, or the user holds a position in the pair.
            case ExecutionReason.NothingToCancel or ExecutionReason.NoPosition or ExecutionReason.ForeignPosition:
                return (CycleOutcome.ExecutionFailed, null);

            case ExecutionReason.FuturesSetup when ApprovalPolicy.AccountNeedsItsUser(result):
                logger.LogWarning("The Futures account needs its user: {FuturesSetup}, so the agent goes to Error", result.FuturesSetup);
                await agents.EnterErrorAsync(request.UserId, request.AgentId, cancellationToken);
                return (CycleOutcome.ExecutionFailed, CycleFailure.Execution);

            default:
                return (CycleOutcome.ExecutionFailed, CycleFailure.Execution);
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

    /// <summary>
    /// The approval a Confirm agent's sized order waits as (FR-APR-01): the intent, which approving evaluates again, and
    /// what the user decides on, which is what the Risk Engine sized now, the Strategist's rationale and the Risk Critic's
    /// verdict. It expires after its timeframe's window (FR-APR-03).
    /// </summary>
    private ApprovalRow NewApproval(
        CycleRun cycle,
        CycleRequest request,
        AgentRecord agent,
        ExchangeSymbolInfo symbol,
        MarketSnapshot snapshot,
        OrderIntent intent,
        SizedOrder order,
        TradeProposal proposal,
        CriticVerdict verdict)
    {
        var agentSettings = agent.Settings;
        var now = time.GetUtcNow();

        // A market entry or an exit goes at the price on the other side of the book: the ask to buy and the bid to sell.
        var reference = order.Kind is SizedOrderKind.Entry or SizedOrderKind.Exit && order.EntryType != EntryType.Limit
            ? order.Side switch
            {
                OrderSide.Buy => snapshot.Quote.AskPrice,
                OrderSide.Sell => snapshot.Quote.BidPrice,
                _ => (decimal?)null,
            }
            : null;

        var display = new ApprovalProposal(
            agentSettings.Market,
            agentSettings.Environment,
            symbol.Symbol,
            symbol.BaseAsset,
            symbol.QuoteAsset,
            agentSettings.Timeframe,
            order.Action,
            order.Side,
            order.EntryType,
            order.Quantity,
            order.LimitPrice,
            reference,
            order.StopPrice,
            order.TargetPrice,
            order.Notional,
            order.MoneyAtRisk,
            order.RewardToRisk,
            order.Leverage,
            proposal.Rationale,
            verdict.Verdict,
            verdict.Rationale,
            verdict.Concerns);

        return new ApprovalRow
        {
            Id = Guid.NewGuid(),
            CycleId = cycle.Id,
            UserId = request.UserId,
            AgentId = request.AgentId,
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + settings.ApprovalWindow(agentSettings.Timeframe),
            Intent = TraceJson.Write(intent),
            LimitTtlCandles = proposal.LimitTtlCandles,
            Proposal = TraceJson.Write(display),
        };
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

    /// <summary>
    /// What the role concluded, for the council view (FR-ROLE-08). A model wrote it, apart from the Strategist's action
    /// and the Risk Critic's verdict before the colon, which <see cref="ActionOf"/> reads back.
    /// </summary>
    private static string? SummaryOf(RoleOutput? output) => output switch
    {
        TechnicalAssessment technical => technical.Summary,
        SentimentAssessment sentiment => sentiment.Summary,
        TradeProposal proposal => $"{TradeActions.Name(proposal.Action)}: {proposal.Rationale}",
        CriticVerdict verdict => $"{verdict.Verdict.ToString().ToUpperInvariant()}: {verdict.Rationale}",
        _ => null,
    };

    /// <summary>
    /// The action the Strategist's summary starts with, as <see cref="SummaryOf"/> wrote it, or null. Only that prefix is
    /// read: the rationale after it is a model's, so whatever it says can't pass for an action.
    /// </summary>
    public static TradeAction? ActionOf(string? strategistSummary)
    {
        if (strategistSummary is null || strategistSummary.IndexOf(':', StringComparison.Ordinal) is not (> 0 and var colon))
        {
            return null;
        }

        var name = strategistSummary[..colon];

        foreach (var action in Enum.GetValues<TradeAction>())
        {
            if (string.Equals(TradeActions.Name(action), name, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return null;
    }
}
