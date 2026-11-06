using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// One cycle's trace as it's written: each step saves on a context of its own as soon as it's done, so a crash loses
/// only the step under way, and every billed AI call is on record (#32). Each save is then published to the observers
/// (FR-ROLE-08). The analysts run at once, so the roles' state is kept under a lock.
/// </summary>
internal sealed class CycleRun(
    DecisionCycleRow row,
    IReadOnlyList<AiRole> roles,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    CycleObservers observers,
    INotifications notifications,
    TimeProvider time)
{
    /// <summary>A summary's length, as Appendix B allows the analysts.</summary>
    public const int SummaryMaxLength = OutputContracts.SummaryMaxLength;

    private readonly Lock _lock = new();
    private readonly Dictionary<AiRole, RoleProgress> _roles = roles.ToDictionary(
        role => role, role => new RoleProgress(role, RoleState.Queued, null, null, false, null));

    private (ApprovalRow Row, Notice Notice)? _approval;

    public Guid Id => row.Id;

    /// <summary>Inserts the cycle with every role queued. False when the agent already has a cycle for this candle.</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        row.Roles.AddRange(roles.Select(role => new CycleRoleRow { CycleId = row.Id, Role = role, State = RoleState.Queued }));
        context.Add(row);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DecisionCycleRow.CandleIndex,
        })
        {
            return false;
        }

        Publish();

        return true;
    }

    public Task SaveLossLimitsAsync(IReadOnlyList<RuleResult> rules, CancellationToken cancellationToken) =>
        UpdateCycleAsync(setters => setters.SetProperty(cycle => cycle.LossLimits, TraceJson.Write(rules)), cancellationToken);

    public Task SaveSnapshotAsync(InputSnapshot snapshot, CancellationToken cancellationToken) =>
        UpdateCycleAsync(setters => setters.SetProperty(cycle => cycle.InputSnapshot, TraceJson.Write(snapshot)), cancellationToken);

    public async Task RunningAsync(AiRole role, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await RoleRow(context, role).ExecuteUpdateAsync(
                setters => setters.SetProperty(entry => entry.State, RoleState.Running).SetProperty(entry => entry.StartedAt, now),
                cancellationToken);
        }

        Change(role, progress => progress with { State = RoleState.Running });
    }

    /// <summary>Saves every call the role made with its state, in one transaction, so neither is on record without the other.</summary>
    public async Task FinishRoleAsync(RoleAnswer answer, RoleState state, string? summary, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var model = answer.Model;
        var cut = Cut(summary);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            context.AddRange(answer.Calls.Select(call => RowOf(call)));
            await context.SaveChangesAsync(cancellationToken);

            await RoleRow(context, answer.Role).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(entry => entry.State, state)
                    .SetProperty(entry => entry.Provider, model == null ? null : model.Provider)
                    .SetProperty(entry => entry.ModelId, model == null ? null : model.ModelId)
                    .SetProperty(entry => entry.UsedFallback, answer.UsedFallback)
                    .SetProperty(entry => entry.Summary, cut)
                    .SetProperty(entry => entry.FinishedAt, now),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        lock (_lock)
        {
            row.InputTokens += answer.InputTokens;
            row.OutputTokens += answer.OutputTokens;

            if (answer.Cost is { } cost)
            {
                row.Cost = (row.Cost ?? 0m) + cost;
            }
        }

        Change(answer.Role, _ => new RoleProgress(answer.Role, state, model?.Provider, model?.ModelId, answer.UsedFallback, cut));
    }

    /// <summary>
    /// Saves what execution did. An order may have been sent, so this write has its own time limit rather than the
    /// cycle's token, which shutdown's drain could fire (NFR-REL-04).
    /// </summary>
    public async Task SaveExecutionAsync(ExecutionResult result)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await UpdateCycleAsync(setters => setters.SetProperty(cycle => cycle.Execution, TraceJson.Write(result)), limit.Token).WaitAsync(limit.Token);
    }

    public Task SaveBudgetAsync(BudgetCheck check, CancellationToken cancellationToken) =>
        UpdateCycleAsync(setters => setters.SetProperty(cycle => cycle.Budget, TraceJson.Write(check)), cancellationToken);

    public async Task SaveEvaluationAsync(RiskEvaluation evaluation, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        context.Add(new RiskEvaluationRow
        {
            CycleId = row.Id,
            Verdict = evaluation.Verdict,
            Intent = TraceJson.Write(evaluation.Intent),
            Caps = TraceJson.Write(evaluation.Caps),
            Effective = TraceJson.Write(evaluation.Effective),
            Rules = TraceJson.Write(evaluation.Rules),
            SizedOrder = evaluation.Order is null ? null : TraceJson.Write(evaluation.Order),
            Breach = evaluation.Breach is null ? null : TraceJson.Write(evaluation.Breach),
            EvaluatedAt = time.GetUtcNow(),
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The approval the cycle ends with, when it ends <see cref="CycleOutcome.AwaitingApproval"/>, and the notice that tells
    /// its user (FR-APR-01, FR-NTF-01). Both commit with the cycle's end.
    /// </summary>
    public void Await(ApprovalRow approval, Notice notice) => _approval = (approval, notice);

    /// <summary>
    /// Ends the cycle, and marks every role it never reached as skipped. A notification about how it ended, and the
    /// approval it waits for, commit with it, or none does.
    /// </summary>
    public async Task FinishAsync(CycleOutcome outcome, CycleFailure? failure, Notice? notice, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        long inputTokens, outputTokens;
        decimal? cost;

        lock (_lock)
        {
            (inputTokens, outputTokens, cost) = (row.InputTokens, row.OutputTokens, row.Cost);
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await context.Set<CycleRoleRow>()
                .Where(entry => entry.CycleId == row.Id && (entry.State == RoleState.Queued || entry.State == RoleState.Running))
                .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.State, RoleState.Skipped), cancellationToken);

            await UpdateCycle(context).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(cycle => cycle.FinishedAt, now)
                    .SetProperty(cycle => cycle.Outcome, outcome)
                    .SetProperty(cycle => cycle.Failure, failure)
                    .SetProperty(cycle => cycle.InputTokens, inputTokens)
                    .SetProperty(cycle => cycle.OutputTokens, outputTokens)
                    .SetProperty(cycle => cycle.Cost, cost),
                cancellationToken);

            if (outcome == CycleOutcome.AwaitingApproval && _approval is var (approval, approvalNotice))
            {
                // One pending approval per agent: a newer proposal replaces the older one, which can no longer be
                // approved. Its user hears of the new one, which is enough.
                await context.Set<ApprovalRow>()
                    .Where(entry => entry.AgentId == row.AgentId && entry.Status == ApprovalStatus.Pending)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(entry => entry.Status, ApprovalStatus.Expired)
                            .SetProperty(entry => entry.End, ApprovalEnd.Superseded)
                            .SetProperty(entry => entry.DecidedAt, now),
                        cancellationToken);

                context.Add(approval);
                notifications.Add(context, approvalNotice);
            }

            if (notice is not null)
            {
                notifications.Add(context, notice);
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        lock (_lock)
        {
            row.FinishedAt = now;
            row.Outcome = outcome;
            row.Failure = failure;

            foreach (var role in _roles.Keys.ToList())
            {
                if (_roles[role].State is RoleState.Queued or RoleState.Running)
                {
                    _roles[role] = _roles[role] with { State = RoleState.Skipped };
                }
            }
        }

        Publish();
    }

    public CycleProgress Progress()
    {
        lock (_lock)
        {
            return new CycleProgress(
                row.Id,
                row.UserId,
                row.AgentId,
                row.CandleClose,
                row.StartedAt,
                row.FinishedAt,
                row.Outcome,
                row.Failure,
                [.. roles.Select(role => _roles[role])]);
        }
    }

    /// <summary>At most <see cref="SummaryMaxLength"/> characters, the last of them an ellipsis when it was cut.</summary>
    public static string? Cut(string? summary) =>
        summary is null || summary.Length <= SummaryMaxLength ? summary : string.Concat(summary.AsSpan(0, SummaryMaxLength - 1), "…");

    private AiCallRow RowOf(CouncilCall call) => new()
    {
        CycleId = row.Id,
        Role = call.Role,
        Provider = call.Model.Provider,
        ModelId = call.Model.ModelId,
        Fallback = call.Fallback,
        Attempt = call.Attempt,
        Status = call.Status,
        Failure = call.Failure,
        ProviderMessage = call.ProviderMessage,
        Instructions = call.Instructions,
        Message = call.Message,
        Answer = call.Answer,
        Output = call.Output is null ? null : OutputContracts.Write(call.Output),
        Violations = call.Violations.Count == 0 ? null : TraceJson.Write(call.Violations),
        Templates = TraceJson.Write(call.Templates),
        InputTokens = call.InputTokens,
        OutputTokens = call.OutputTokens,
        Cost = call.Cost,
        Latency = call.Latency,
        ProviderAttempts = call.ProviderAttempts,
        Truncated = call.Truncated,
        StartedAt = call.StartedAt,
    };

    private async Task UpdateCycleAsync(
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<DecisionCycleRow>> set,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await UpdateCycle(context).ExecuteUpdateAsync(set, cancellationToken);
    }

    private IQueryable<DecisionCycleRow> UpdateCycle(EcnanibDbContext context) =>
        context.Set<DecisionCycleRow>().Where(cycle => cycle.Id == row.Id);

    private IQueryable<CycleRoleRow> RoleRow(EcnanibDbContext context, AiRole role) =>
        context.Set<CycleRoleRow>().Where(entry => entry.CycleId == row.Id && entry.Role == role);

    private void Change(AiRole role, Func<RoleProgress, RoleProgress> change)
    {
        lock (_lock)
        {
            _roles[role] = change(_roles[role]);
        }

        Publish();
    }

    private void Publish() => observers.Notify(Progress());
}
