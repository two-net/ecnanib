using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// Reads every user's council calls for Experience. The facts come from one query of <c>ai_calls</c> joined to
/// <c>decision_cycles</c>, and the four scored fields from a second that reads them out of the answered calls' output
/// in SQL, so no prompt or answer is ever loaded. See Trading experience in docs/architecture.md.
/// </summary>
internal sealed class CouncilActivity(IDbContextFactory<EcnanibDbContext> contextFactory) : ICouncilActivity
{
    private static readonly Dictionary<string, TradeAction> Actions =
        Enum.GetValues<TradeAction>().ToDictionary(TradeActions.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, VerdictKind> Verdicts =
        Enum.GetValues<VerdictKind>().ToDictionary(verdict => verdict.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    private static readonly Dictionary<string, Trend> Trends =
        Enum.GetValues<Trend>().ToDictionary(trend => trend.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    public async Task<IReadOnlyList<CouncilActivityKey>> ListAsync(DateTimeOffset startedFrom, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var keys = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Where(cycle => cycle.StartedAt >= startedFrom)
            .GroupBy(cycle => new { cycle.UserId, cycle.Timeframe })
            .Select(cycles => new { cycles.Key.UserId, cycles.Key.Timeframe, First = cycles.Min(cycle => cycle.StartedAt) })
            .ToListAsync(cancellationToken);

        return [.. keys.Select(key => new CouncilActivityKey(key.UserId, key.Timeframe, key.First))];
    }

    public async Task<IReadOnlyList<CouncilCallRecord>> ListCallsAsync(
        Guid userId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var calls = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Where(cycle => cycle.UserId == userId && cycle.Timeframe == timeframe && cycle.StartedAt >= from && cycle.StartedAt < to)
            .Join(
                context.Set<AiCallRow>(),
                cycle => cycle.Id,
                call => call.CycleId,
                (cycle, call) => new
                {
                    call.Id,
                    call.CycleId,
                    cycle.AgentId,
                    cycle.Market,
                    cycle.Environment,
                    cycle.Symbol,
                    cycle.Timeframe,
                    cycle.CandleClose,
                    CycleStartedAt = cycle.StartedAt,
                    call.Role,
                    call.Provider,
                    call.ModelId,
                    call.Fallback,
                    call.Attempt,
                    call.Status,
                    call.Failure,
                    call.Truncated,
                    call.Latency,
                    call.InputTokens,
                    call.OutputTokens,
                    call.Cost,
                })
            .OrderBy(call => call.Id)
            .ToListAsync(cancellationToken);

        if (calls.Count == 0)
        {
            return [];
        }

        // The output is in its contract's names (ContractReader.Write): the action as OPEN_LONG, the rest in lower case.
        // A score that isn't a number can't come from a checked output, but the cast must never fail the query.
        var scored = await context.Database.SqlQuery<ScoredOutput>(
                $"""
                SELECT c.id,
                       c.output ->> 'action' AS action,
                       c.output ->> 'verdict' AS verdict,
                       c.output ->> 'trend' AS trend,
                       CASE WHEN jsonb_typeof(c.output -> 'score') = 'number' THEN (c.output ->> 'score')::numeric END AS score
                FROM ai_calls c JOIN decision_cycles d ON d.id = c.cycle_id
                WHERE d.user_id = {userId} AND d.timeframe = {timeframe.ToString()} AND d.started_at >= {from} AND d.started_at < {to}
                  AND c.status = 'Answered' AND c.output IS NOT NULL
                """)
            .ToDictionaryAsync(output => output.Id, cancellationToken);

        return
        [
            .. calls.Select(call =>
            {
                var output = scored.GetValueOrDefault(call.Id);

                return new CouncilCallRecord(
                    call.Id,
                    call.CycleId,
                    call.AgentId,
                    call.Market,
                    call.Environment,
                    call.Symbol,
                    call.Timeframe,
                    call.CandleClose,
                    call.CycleStartedAt,
                    call.Role,
                    call.Provider,
                    call.ModelId,
                    call.Fallback,
                    call.Attempt,
                    call.Status,
                    call.Failure,
                    call.Truncated,
                    call.Latency,
                    call.InputTokens,
                    call.OutputTokens,
                    call.Cost)
                {
                    Action = call.Role == AiRole.Strategist ? Parse(Actions, output?.Action) : null,
                    Verdict = call.Role == AiRole.RiskCritic ? Parse(Verdicts, output?.Verdict) : null,
                    Trend = call.Role == AiRole.TechnicalAnalyst ? Parse(Trends, output?.Trend) : null,
                    Score = call.Role == AiRole.SentimentAnalyst ? output?.Score : null,
                };
            }),
        ];
    }

    private static T? Parse<T>(Dictionary<string, T> names, string? name)
        where T : struct, Enum =>
        name is not null && names.TryGetValue(name, out var value) ? value : null;
}

/// <summary>The scored fields of an answered call. The naming convention reaches this too, so the columns are as above.</summary>
internal sealed class ScoredOutput
{
    public long Id { get; init; }

    public string? Action { get; init; }

    public string? Verdict { get; init; }

    public string? Trend { get; init; }

    public decimal? Score { get; init; }
}
