using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A user's trade approvals (SRS 3.11). Approving one is single-shot: a conditional move from Pending to Approving, which a
/// unique index allows once per agent at a time, then a recorded run that has the Risk Engine look again on fresh
/// prices and the agent's account, and sends what it sizes now. See Approvals in docs/architecture.md.
/// </summary>
internal sealed class Approvals(
    IAgents agents,
    CycleData data,
    IRiskEngine riskEngine,
    IOrderExecution execution,
    IJobRunner jobs,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    INotifications notifications,
    IAuditTrail audit,
    TimeProvider time,
    ILogger<Approvals> logger) : IApprovals
{
    /// <summary>The run that checks an approved order again and sends it. No recurring job has this name.</summary>
    public const string JobName = "approval";

    public const string AuditTarget = "Approval";

    /// <summary>
    /// How long after its approval an entry may still be sent, so the prices the Risk Engine checked are at most this
    /// old when it goes (FR-CYC-06). Counting from the approval rather than the window means an approval made in the
    /// window's last seconds isn't refused for the time its check takes.
    /// </summary>
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(1);

    /// <summary>Reading Binance, checking again and executing, which takes up to 45 seconds once it holds the agent's lock.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long recording how it ended may take, when the run's token may have fired.</summary>
    private static readonly TimeSpan LastWriteLimit = TimeSpan.FromSeconds(5);

    /// <summary>The approval's page, which notifications and email link to (FR-APR-02, FR-NTF-03).</summary>
    public static string Link(Guid id) => $"/approvals/{id}";

    public async Task<ApprovalList> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = await context.Set<ApprovalRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.Status == ApprovalStatus.Pending)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var decided = await context.Set<ApprovalRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.Status != ApprovalStatus.Pending)
            .OrderByDescending(row => row.CreatedAt)
            .Take(IApprovals.RecentCount)
            .ToListAsync(cancellationToken);

        return new ApprovalList([.. pending.Select(RecordOf)], [.. decided.Select(RecordOf)]);
    }

    public async Task<ApprovalRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, id, cancellationToken);

        return row is null ? null : RecordOf(row);
    }

    public async Task<ApprovalResult> ApproveAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, id, cancellationToken);

        if (row is null)
        {
            return ApprovalResult.NotFound;
        }

        if (row.Status != ApprovalStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.NotPending, RecordOf(row));
        }

        var proposal = ProposalOf(row);
        var agent = await agents.FindAsync(userId, row.AgentId, cancellationToken);
        var now = time.GetUtcNow();

        if (EndFor(row, proposal, agent, now) is { } end)
        {
            await ExpireAsync(row, proposal, agent, end, cancellationToken);

            return await ResultAsync(ApprovalOutcome.NotPending, userId, id, cancellationToken);
        }

        switch (await StartApprovingAsync(row, proposal, userId, now, cancellationToken))
        {
            case Start.Busy:
                return new ApprovalResult(ApprovalOutcome.Busy, RecordOf(row));
            case Start.Changed:
                return await ResultAsync(ApprovalOutcome.NotPending, userId, id, cancellationToken);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.CycleId] = row.CycleId,
            [LogProperties.Environment] = proposal.Environment.ToString(),
        });

        // A run of its own: a correlation ID, a job_runs row, a place in the shutdown drain, and a token that's safe to pass
        // to Binance, which a circuit's isn't (NFR-REL-04).
        var status = await jobs.RunAsync(
            JobName, RunTimeout, (context, token) => SendAsync(row, proposal, agent!, now, context, token));

        if (status == JobStatus.NotStarted)
        {
            await LastWriteAsync(row.Id, ApprovalStatus.Failed, ApprovalEnd.ShuttingDown, null);
        }

        return await ResultAsync(ApprovalOutcome.Done, userId, id, cancellationToken);
    }

    public async Task<ApprovalResult> RejectAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, id, cancellationToken);

        if (row is null)
        {
            return ApprovalResult.NotFound;
        }

        if (row.Status != ApprovalStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.NotPending, RecordOf(row));
        }

        var proposal = ProposalOf(row);
        var now = time.GetUtcNow();

        // Rejecting one that has expired would say the user decided something the window decided.
        if (now >= row.ExpiresAt)
        {
            var agent = await agents.FindAsync(userId, row.AgentId, cancellationToken);
            await ExpireAsync(row, proposal, agent, ApprovalEnd.WindowEnded, cancellationToken);

            return await ResultAsync(ApprovalOutcome.NotPending, userId, id, cancellationToken);
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var count = await Pending(context, row.Id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(entry => entry.Status, ApprovalStatus.Rejected)
                    .SetProperty(entry => entry.DecidedBy, userId)
                    .SetProperty(entry => entry.DecidedAt, now),
                cancellationToken);

            if (count == 0)
            {
                return await ResultAsync(ApprovalOutcome.NotPending, userId, id, cancellationToken);
            }

            audit.Add(context, AuditOf(AuditAction.TradeRejected, userId, row, proposal, ApprovalStatus.Pending, ApprovalStatus.Rejected));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("The user rejected approval {ApprovalId}", row.Id);

        return await ResultAsync(ApprovalOutcome.Done, userId, id, cancellationToken);
    }

    /// <summary>
    /// Why a pending approval can no longer be approved, or null when it still can: its window ended, its agent isn't
    /// Active, or its agent now trades something else.
    /// </summary>
    internal static ApprovalEnd? EndFor(ApprovalRow row, ApprovalProposal proposal, AgentRecord? agent, DateTimeOffset now)
    {
        if (now >= row.ExpiresAt)
        {
            return ApprovalEnd.WindowEnded;
        }

        if (agent is not { Status: AgentStatus.Active })
        {
            return ApprovalEnd.AgentNotActive;
        }

        var settings = agent.Settings;

        return settings.Market != proposal.Market || settings.Environment != proposal.Environment
            || settings.Symbol != proposal.Symbol || settings.Timeframe != proposal.Timeframe
                ? ApprovalEnd.AgentChanged
                : null;
    }

    /// <summary>
    /// Expires a pending approval and tells its user, in one transaction (FR-APR-03, FR-NTF-01). False when it was no
    /// longer pending, so nothing changed.
    /// </summary>
    internal async Task<bool> ExpireAsync(
        ApprovalRow row, ApprovalProposal proposal, AgentRecord? agent, ApprovalEnd end, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var count = await Pending(context, row.Id).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(entry => entry.Status, ApprovalStatus.Expired)
                .SetProperty(entry => entry.End, end)
                .SetProperty(entry => entry.DecidedAt, now),
            cancellationToken);

        if (count == 0)
        {
            return false;
        }

        notifications.Add(
            context,
            new Notice(
                NotificationType.ApprovalExpired,
                row.UserId,
                Link(row.Id),
                agent?.Settings.Name,
                proposal.Environment == TradingEnvironment.Demo));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Approval {ApprovalId} expired: {ApprovalEnd}", row.Id, end);

        return true;
    }

    internal static ApprovalProposal ProposalOf(ApprovalRow row) => TraceJson.Read<ApprovalProposal>(row.Proposal);

    internal static ApprovalRecord RecordOf(ApprovalRow row) => new(
        row.Id,
        row.AgentId,
        row.CycleId,
        row.Status,
        row.End,
        row.CreatedAt,
        row.ExpiresAt,
        row.DecidedAt,
        row.DecidedBy is not null,
        ProposalOf(row),
        row.Revalidation is null ? null : RevalidationOf(TraceJson.Read<StoredEvaluation>(row.Revalidation), row.RevalidatedAt),
        row.Execution is null ? null : TraceJson.Read<ExecutionResult>(row.Execution));

    /// <summary>
    /// Checks the approved order again and sends what the Risk Engine sizes now (FR-APR-03). Every outcome is recorded on
    /// the approval; one that throws is too, before the run records the failure.
    /// </summary>
    private async Task SendAsync(
        ApprovalRow row,
        ApprovalProposal proposal,
        AgentRecord agent,
        DateTimeOffset approvedAt,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var sent = false;

        try
        {
            var candleClose = await CandleCloseAsync(row.CycleId, cancellationToken);
            var deadline = approvedAt + SendWindow;

            // The candles the proposal was made on, so the ATR is the one it saw, with prices at most a minute old and the
            // account as reconciliation leaves it now.
            var collected = await data.CollectAsync(row.UserId, agent, candleClose, deadline, context, cancellationToken);

            if (collected.Failure is { } failure)
            {
                await LastWriteAsync(
                    row.Id, ApprovalStatus.Failed, failure == CycleFailure.Account ? ApprovalEnd.Account : ApprovalEnd.MarketData, null);

                return;
            }

            var settings = agent.Settings;
            var record = await data.RecordAsync(row.UserId, agent, candleClose, cancellationToken);
            var inputs = CycleInputs.Risk(
                settings.Market,
                time.GetUtcNow(),
                settings.Risk,
                settings.CapitalAllocation ?? 0m,
                collected.Symbol!,
                collected.Snapshot!,
                collected.Account!,
                record);

            // The engine trusts none of it, the stored intent included (FR-RISK-01, NFR-SEC-08).
            var evaluation = riskEngine.Evaluate(TraceJson.Read<OrderIntent>(row.Intent), inputs);
            await SaveRevalidationAsync(row, proposal, evaluation, cancellationToken);

            if (evaluation.Breach is { } breach)
            {
                await agents.PauseForLossLimitAsync(row.UserId, agent.Id, breach, cancellationToken);
            }

            if (!evaluation.Passed)
            {
                logger.LogInformation("The Risk Engine rejected approval {ApprovalId} when it looked again", row.Id);

                return;
            }

            // Nothing new is sent once shutdown has begun. What's sent is seen through on the run's token (NFR-REL-04).
            if (context.Stopping.IsCancellationRequested)
            {
                await LastWriteAsync(row.Id, ApprovalStatus.Failed, ApprovalEnd.ShuttingDown, null);

                return;
            }

            sent = true;

            var result = await execution.ExecuteAsync(
                new ExecutionRequest(
                    row.UserId,
                    agent.Id,
                    row.CycleId,
                    candleClose,
                    settings.ExchangeCredentialId!.Value,
                    settings.Market,
                    settings.Environment,
                    settings.Symbol,
                    settings.Timeframe,
                    evaluation.Order!,
                    deadline)
                {
                    LimitTtlCandles = row.LimitTtlCandles,
                },
                cancellationToken);

            logger.LogInformation(
                "Execution of approval {ApprovalId} ended as {ExecutionStatus}, reason {ExecutionReason}, code {ErrorCode}",
                row.Id,
                result.Status,
                result.Reason,
                result.ExchangeCode);

            var (status, end) = result switch
            {
                { Executed: true } => (ApprovalStatus.Executed, (ApprovalEnd?)null),

                // Paused, stopped or killed since it was approved, so the executor sent nothing.
                { Reason: ExecutionReason.AgentNotActive } => (ApprovalStatus.Expired, ApprovalEnd.AgentNotActive),
                _ => (ApprovalStatus.Failed, ApprovalEnd.Execution),
            };

            await LastWriteAsync(row.Id, status, end, result);

            if (ApprovalPolicy.AccountNeedsItsUser(result))
            {
                logger.LogWarning("The Futures account needs its user: {FuturesSetup}, so the agent goes to Error", result.FuturesSetup);
                await agents.EnterErrorAsync(row.UserId, agent.Id, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Broad because whatever went wrong, the approval must say it ended. Once an order may have gone, it's
            // Interrupted, because the agent's orders know what was sent and the approval doesn't.
            logger.LogError(exception, "Approval {ApprovalId} failed", row.Id);

            await LastWriteAsync(
                row.Id,
                sent ? ApprovalStatus.Interrupted : ApprovalStatus.Failed,
                sent ? null : ApprovalEnd.Unexpected,
                null);

            throw;
        }
    }

    /// <summary>Pending to Approving, with the user's audit record, on the caller's circuit so the record keeps its client.</summary>
    private async Task<Start> StartApprovingAsync(
        ApprovalRow row, ApprovalProposal proposal, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        int count;

        try
        {
            count = await Pending(context, row.Id)
                .Where(entry => entry.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entry => entry.Status, ApprovalStatus.Approving)
                        .SetProperty(entry => entry.DecidedBy, userId)
                        .SetProperty(entry => entry.DecidedAt, now),
                    cancellationToken);
        }
        catch (Exception exception) when (IsUniqueViolation(exception, ApprovalRow.ApprovingIndex))
        {
            return Start.Busy;
        }

        if (count == 0)
        {
            return Start.Changed;
        }

        audit.Add(context, AuditOf(AuditAction.TradeApproved, userId, row, proposal, ApprovalStatus.Pending, ApprovalStatus.Approving));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("The user approved approval {ApprovalId}", row.Id);

        return Start.Started;
    }

    /// <summary>
    /// Saves the engine's second look. A failure rejects the approval automatically, by the system, in the same
    /// transaction (FR-APR-03, FR-AUD-01).
    /// </summary>
    private async Task SaveRevalidationAsync(
        ApprovalRow row, ApprovalProposal proposal, RiskEvaluation evaluation, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var json = TraceJson.Write(evaluation);
        var status = evaluation.Passed ? ApprovalStatus.Approving : ApprovalStatus.Invalid;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var count = await Approving(context, row.Id).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(entry => entry.Revalidation, json)
                .SetProperty(entry => entry.RevalidatedAt, now)
                .SetProperty(entry => entry.Status, status),
            cancellationToken);

        if (count == 0)
        {
            throw new InvalidOperationException($"Approval {row.Id} is no longer being approved.");
        }

        if (!evaluation.Passed)
        {
            audit.Add(context, AuditOf(AuditAction.TradeRejected, null, row, proposal, ApprovalStatus.Approving, ApprovalStatus.Invalid));
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records how an approval being approved ended. An order may have been sent, so this write has its own time limit
    /// rather than the run's token, which shutdown's drain could fire (NFR-REL-04).
    /// </summary>
    private async Task LastWriteAsync(Guid id, ApprovalStatus status, ApprovalEnd? end, ExecutionResult? result)
    {
        try
        {
            using var limit = new CancellationTokenSource(LastWriteLimit);
            var json = result is null ? null : TraceJson.Write(result);

            await using var context = await contextFactory.CreateDbContextAsync(limit.Token);

            await Approving(context, id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entry => entry.Status, status)
                        .SetProperty(entry => entry.End, end)
                        .SetProperty(entry => entry.Execution, json),
                    limit.Token)
                .WaitAsync(limit.Token);
        }
        catch (Exception exception)
        {
            // Broad because the run is ending anyway. The next start marks it interrupted.
            logger.LogError(exception, "Couldn't record how approval {ApprovalId} ended", id);
        }
    }

    private async Task<DateTimeOffset> CandleCloseAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<DecisionCycleRow>()
            .Where(cycle => cycle.Id == cycleId)
            .Select(cycle => cycle.CandleClose)
            .SingleAsync(cancellationToken);
    }

    private async Task<ApprovalRow?> FindRowAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<ApprovalRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);
    }

    private async Task<ApprovalResult> ResultAsync(ApprovalOutcome outcome, Guid userId, Guid id, CancellationToken cancellationToken) =>
        new(outcome, await FindAsync(userId, id, cancellationToken));

    private static IQueryable<ApprovalRow> Pending(EcnanibDbContext context, Guid id) =>
        context.Set<ApprovalRow>().Where(entry => entry.Id == id && entry.Status == ApprovalStatus.Pending);

    private static IQueryable<ApprovalRow> Approving(EcnanibDbContext context, Guid id) =>
        context.Set<ApprovalRow>().Where(entry => entry.Id == id && entry.Status == ApprovalStatus.Approving);

    /// <param name="actor">The user, or null for the system.</param>
    private static AuditEntry AuditOf(
        AuditAction action, Guid? actor, ApprovalRow row, ApprovalProposal proposal, ApprovalStatus before, ApprovalStatus after) => new()
    {
        Action = action,
        ActorKind = actor is null ? AuditActorKind.System : AuditActorKind.User,
        ActorId = actor,
        UserId = row.UserId,
        TargetType = AuditTarget,
        TargetId = row.Id.ToString(),
        Changes =
        [
            new(ApprovalAuditFields.Action, null, proposal.Action.ToString()),
            new(ApprovalAuditFields.Symbol, null, proposal.Symbol),
            new(ApprovalAuditFields.Status, before.ToString(), after.ToString()),
        ],
    };

    private static ApprovalRevalidation RevalidationOf(StoredEvaluation stored, DateTimeOffset? at) => new(
        stored.Verdict,
        [.. stored.Rules.Where(rule => rule.Outcome == RuleOutcome.Fail)],
        stored.Order?.Quantity,
        stored.Order?.MoneyAtRisk,
        at ?? DateTimeOffset.MinValue);

    private static bool IsUniqueViolation(Exception exception, string constraint) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } violation && violation.ConstraintName == constraint;

    private enum Start
    {
        Started,
        Changed,
        Busy,
    }

    /// <summary>The parts of a stored <see cref="RiskEvaluation"/> the page shows. A <see cref="SizedOrder"/> can't be read back.</summary>
    private sealed record StoredEvaluation(RiskVerdict Verdict, IReadOnlyList<RuleResult> Rules, StoredOrder? Order);

    private sealed record StoredOrder(decimal? Quantity, decimal? MoneyAtRisk);
}
