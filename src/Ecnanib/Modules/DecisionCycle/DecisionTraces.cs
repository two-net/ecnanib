using System.Text.Encodings.Web;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A user's decision traces. Every query filters on the user (NFR-SEC-04). A trace's free text, its prompts, answers,
/// providers' messages and summaries, is masked as the logs are before it leaves, in case a model or a provider echoed
/// something shaped like a secret (FR-KEY-09). Keys never reach a trace in the first place.
/// </summary>
internal sealed class DecisionTraces(IDbContextFactory<EcnanibDbContext> contextFactory, ISecretScrubber scrubber) : IDecisionTraces
{
    /// <summary>
    /// Indented, with what the model wrote kept as written: the default encoder would turn "&lt;" or an accent into a
    /// \u escape. Pages show it only as text, which Razor encodes, so nothing in it can become markup.
    /// </summary>
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<CycleProgress?> FindLatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cycle = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Include(row => row.Roles)
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .OrderByDescending(row => row.CandleClose)
            .FirstOrDefaultAsync(cancellationToken);

        return cycle is null
            ? null
            : new CycleProgress(
                cycle.Id,
                cycle.UserId,
                cycle.AgentId,
                cycle.CandleClose,
                cycle.StartedAt,
                cycle.FinishedAt,
                cycle.Outcome,
                cycle.Failure,
                [
                    .. cycle.Roles
                        .OrderBy(role => role.Role)
                        .Select(role => new RoleProgress(role.Role, role.State, role.Provider, role.ModelId, role.UsedFallback, role.Summary)),
                ]);
    }

    public async Task<IReadOnlyList<CycleMarker>> ListCyclesAsync(Guid userId, Guid agentId, CycleRange range, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The agent and candle close index, newest first, so the cap keeps the cycles nearest now.
        var cycles = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.Environment == range.Environment && row.Symbol == range.Symbol
                && row.Timeframe == range.Timeframe && row.CandleClose >= range.From && row.CandleClose <= range.To)
            .OrderByDescending(row => row.CandleClose)
            .Take(CycleRange.MaxCycles)
            .Select(row => new
            {
                row.Id,
                row.CandleClose,
                row.Environment,
                row.Outcome,
                row.Failure,
                Proposal = row.Roles.Where(role => role.Role == AiRole.Strategist).Select(role => role.Summary).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. cycles
                .OrderBy(cycle => cycle.CandleClose)
                .Select(cycle => new CycleMarker(cycle.Id, cycle.CandleClose, cycle.Environment, cycle.Outcome, cycle.Failure, DecisionCycles.ActionOf(cycle.Proposal))),
        ];
    }

    public async Task<IReadOnlyList<CycleSummary>> ListRecentAsync(Guid userId, Guid agentId, int count, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 0, CycleRange.MaxCycles);

        if (take == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The agent and candle close index, newest first. Usage is summed from the calls, whose index is on the cycle.
        var cycles = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .OrderByDescending(row => row.CandleClose)
            .Take(take)
            .Select(row => new
            {
                row.Id,
                row.CandleClose,
                row.Environment,
                row.StartedAt,
                row.FinishedAt,
                row.Outcome,
                row.Failure,
                Proposal = row.Roles.Where(role => role.Role == AiRole.Strategist).Select(role => role.Summary).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var ids = cycles.Select(cycle => cycle.Id).ToList();
        var usage = await UsageAsync(context, ids, cancellationToken);

        return
        [
            .. cycles.Select(cycle => new CycleSummary(
                cycle.Id,
                cycle.CandleClose,
                cycle.Environment,
                cycle.StartedAt,
                cycle.FinishedAt,
                cycle.Outcome,
                cycle.Failure,
                DecisionCycles.ActionOf(cycle.Proposal),
                usage.GetValueOrDefault(cycle.Id) ?? TraceUsage.None)),
        ];
    }

    public async Task<CycleTrace?> FindAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cycle = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Include(row => row.Roles)
            .FirstOrDefaultAsync(row => row.Id == cycleId && row.UserId == userId && row.AgentId == agentId, cancellationToken);

        if (cycle is null)
        {
            return null;
        }

        // Only after the cycle was found to be the user's: these hang off it by its ID.
        var calls = await context.Set<AiCallRow>()
            .AsNoTracking()
            .Where(call => call.CycleId == cycle.Id)
            .OrderBy(call => call.Id)
            .ToListAsync(cancellationToken);

        var evaluation = await context.Set<RiskEvaluationRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.CycleId == cycle.Id, cancellationToken);

        var approval = await context.Set<ApprovalRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.CycleId == cycle.Id && row.UserId == userId, cancellationToken);

        return new CycleTrace(
            cycle.Id,
            cycle.AgentId,
            cycle.Market,
            cycle.Environment,
            cycle.Symbol,
            cycle.Timeframe,
            cycle.CandleClose,
            cycle.CatchUp,
            cycle.Deadline,
            cycle.StartedAt,
            cycle.FinishedAt,
            cycle.Outcome,
            cycle.Failure,
            cycle.InputSnapshot is null ? null : TraceJson.Read<InputSnapshot>(cycle.InputSnapshot),
            cycle.LossLimits is null ? [] : TraceJson.Read<List<RuleResult>>(cycle.LossLimits),
            cycle.Budget is null ? null : TraceJson.Read<BudgetCheck>(cycle.Budget),
            RolesOf(cycle.Roles, calls),
            evaluation is null ? null : EvaluationOf(evaluation),
            approval is null ? null : ApprovalOf(approval),
            cycle.Execution is null ? null : TraceJson.Read<ExecutionResult>(cycle.Execution),
            UsageOf(calls));
    }

    /// <summary>
    /// Each cycle's calls summed. Only the numbers are read, a handful of calls a cycle, and they're added up here,
    /// where latencies add as they do in <see cref="UsageOf"/>. A cycle with no calls is left out.
    /// </summary>
    private static async Task<Dictionary<Guid, TraceUsage>> UsageAsync(
        EcnanibDbContext context, IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        if (cycleIds.Count == 0)
        {
            return [];
        }

        var calls = await context.Set<AiCallRow>()
            .Where(call => cycleIds.Contains(call.CycleId))
            .Select(call => new CallUsage(call.CycleId, call.InputTokens, call.OutputTokens, call.Cost, call.Latency))
            .ToListAsync(cancellationToken);

        return calls.GroupBy(call => call.CycleId).ToDictionary(group => group.Key, group => UsageOf([.. group]));
    }

    private static TraceUsage UsageOf(IReadOnlyCollection<AiCallRow> calls) =>
        UsageOf([.. calls.Select(call => new CallUsage(call.CycleId, call.InputTokens, call.OutputTokens, call.Cost, call.Latency))]);

    private static TraceUsage UsageOf(IReadOnlyCollection<CallUsage> calls) =>
        calls.Count == 0
            ? TraceUsage.None
            : new TraceUsage(
                calls.Sum(call => call.InputTokens),
                calls.Sum(call => call.OutputTokens),
                calls.Any(call => call.Cost is not null) ? calls.Sum(call => call.Cost ?? 0m) : null,
                calls.Where(call => call.Cost is null).Sum(call => call.InputTokens + call.OutputTokens),
                calls.Aggregate(TimeSpan.Zero, (total, call) => total + call.Latency));

    private List<RoleTrace> RolesOf(IEnumerable<CycleRoleRow> roles, IReadOnlyList<AiCallRow> calls) =>
    [
        .. roles
            .OrderBy(role => role.Role)
            .Select(role => new RoleTrace(
                role.Role,
                role.State,
                role.Provider,
                role.ModelId,
                role.UsedFallback,
                Scrub(role.Summary),
                role.StartedAt,
                role.FinishedAt,
                [.. calls.Where(call => call.Role == role.Role).Select(CallOf)])),
    ];

    private CallTrace CallOf(AiCallRow call) => new(
        call.Provider,
        call.ModelId,
        call.Fallback,
        call.Attempt,
        call.Status,
        call.Failure,
        Scrub(call.ProviderMessage),
        Scrub(call.Instructions),
        Scrub(call.Message),
        Scrub(call.Answer),
        Scrub(call.Output is null ? null : Indent(call.Output)),
        call.Violations is null
            ? []
            : [.. TraceJson.Read<List<ContractViolation>>(call.Violations).Select(violation => violation with { Problem = scrubber.Scrub(violation.Problem) })],
        TraceJson.Read<List<PromptTemplateVersion>>(call.Templates),
        call.InputTokens,
        call.OutputTokens,
        call.Cost,
        call.Latency,
        call.ProviderAttempts,
        call.Truncated,
        call.StartedAt);

    /// <summary>The approval as its page has it, with what the models wrote masked like the rest of the trace.</summary>
    private ApprovalTrace ApprovalOf(ApprovalRow row)
    {
        var record = Approvals.RecordOf(row);
        var proposal = record.Proposal with
        {
            StrategistRationale = scrubber.Scrub(record.Proposal.StrategistRationale),
            CriticRationale = scrubber.Scrub(record.Proposal.CriticRationale),
            Concerns = [.. record.Proposal.Concerns.Select(scrubber.Scrub)],
        };

        return new ApprovalTrace(record with { Proposal = proposal }, Approvals.RevalidationRulesOf(row));
    }

    private static EvaluationTrace EvaluationOf(RiskEvaluationRow row) => new(
        row.Verdict,
        row.EvaluatedAt,
        TraceJson.Read<OrderIntent>(row.Intent),
        TraceJson.Read<RiskCaps>(row.Caps),
        TraceJson.Read<RiskSettings>(row.Effective),
        TraceJson.Read<List<RuleResult>>(row.Rules),
        row.SizedOrder is null ? null : TraceJson.Read<TracedOrder>(row.SizedOrder),
        row.Breach is null ? null : TraceJson.Read<LossLimitBreach>(row.Breach));

    /// <summary>
    /// The output as the contract wrote it, in its own names, only indented to read. PostgreSQL's jsonb doesn't keep the
    /// order of keys, so it's the order the database gives.
    /// </summary>
    private static string Indent(string json)
    {
        using var document = JsonDocument.Parse(json);

        return JsonSerializer.Serialize(document.RootElement, Indented);
    }

    private string? Scrub(string? text) => text is null ? null : scrubber.Scrub(text);

    private sealed record CallUsage(Guid CycleId, long InputTokens, long OutputTokens, decimal? Cost, TimeSpan Latency);
}
