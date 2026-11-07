using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Experience.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// Reads a user's metrics: the calls and decisions from the snapshots, summed over the days, and the trades from the
/// trade journal, each given to the models of the cycle that proposed its entry. See Trading experience in
/// docs/architecture.md.
/// </summary>
internal sealed class ExperienceMetrics(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ITradeJournal journal,
    ExperienceSettings settings,
    TimeProvider time) : IExperienceMetrics
{
    public async Task<ExperienceReport> ReadAsync(Guid userId, ExperienceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.To < query.From)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(query));
        }

        var from = ExperienceDays.Start(query.From);
        var to = ExperienceDays.Start(query.To) + ExperienceDays.Day;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var snapshots = context.Set<PerformanceSnapshotRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.Environment == query.Environment && row.PeriodStart >= from && row.PeriodStart < to);

        if (query.Market is { } market)
        {
            snapshots = snapshots.Where(row => row.Market == market);
        }

        if (query.Timeframe is { } timeframe)
        {
            snapshots = snapshots.Where(row => row.Timeframe == timeframe);
        }

        if (query.Symbol is { } symbol)
        {
            snapshots = snapshots.Where(row => row.Symbol == symbol);
        }

        var rows = await snapshots.ToListAsync(cancellationToken);
        var trades = await journal.ListSettledAsync(
            userId, new SettledTradeQuery(query.Environment, from, to, query.Market, query.Symbol), cancellationToken);
        var cycles = trades.Where(trade => trade.EntryCycleId is not null).Select(trade => trade.EntryCycleId!.Value).Distinct().ToList();
        var attributions = await context.Set<CycleAttributionRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && cycles.Contains(row.CycleId))
            .ToListAsync(cancellationToken);
        var byCycle = attributions.ToLookup(row => row.CycleId);

        // The trade's environment, market and pair are its own, and its timeframe its entry cycle's.
        var attributed = trades
            .Where(trade => trade.EntryCycleId is { } cycle && byCycle[cycle].Any())
            .SelectMany(trade => byCycle[trade.EntryCycleId!.Value]
                .Where(row => query.Timeframe is null || row.Timeframe == query.Timeframe)
                .Select(row => (Key: new ModelKey(row.Role, row.Provider, row.ModelId), Trade: trade)))
            .ToLookup(pair => pair.Key, pair => pair.Trade);

        var calls = rows.ToLookup(row => new ModelKey(row.Role, row.Provider, row.ModelId));
        var models = calls.Select(group => group.Key).Union(attributed.Select(group => group.Key))
            .OrderBy(key => key.Role)
            .ThenBy(key => key.Provider, StringComparer.Ordinal)
            .ThenBy(key => key.ModelId, StringComparer.Ordinal)
            .Select(key => Metrics(key, calls[key].ToList(), [.. attributed[key]]))
            .ToList();

        return new ExperienceReport(
            query,
            models,
            trades.Count(trade => trade.EntryCycleId is not { } cycle || !byCycle[cycle].Any()),
            Provisional(query, to));
    }

    /// <summary>Whether any day in the range isn't final yet, in the query's timeframe, or the slowest when it has none.</summary>
    private bool Provisional(ExperienceQuery query, DateTimeOffset to) =>
        ExperienceDays.FinalAt(to - ExperienceDays.Day, query.Timeframe ?? Timeframe.OneDay, settings.AnalystHorizon) > time.GetUtcNow();

    private RoleModelMetrics Metrics(ModelKey key, List<PerformanceSnapshotRow> rows, List<SettledTrade> trades)
    {
        var latency = LatencyBands.Empty();

        foreach (var row in rows)
        {
            LatencyBands.Add(latency, row.LatencyBuckets);
        }

        var cost = rows.Sum(row => row.Cost);
        var unpriced = rows.Sum(row => row.UnpricedTokens);

        var calls = new CallMetrics(
            rows.Sum(row => row.Cycles),
            rows.Sum(row => row.AnsweredCycles),
            rows.Sum(row => row.FallbackCycles),
            rows.Sum(row => row.Sent),
            rows.Sum(row => row.NotSent),
            rows.Sum(row => row.Answered),
            rows.Sum(row => row.ContractViolations),
            rows.Sum(row => row.Timeouts),
            rows.Sum(row => row.Refusals),
            rows.Sum(row => row.Errors),
            rows.Sum(row => row.Truncated),
            rows.Sum(row => row.InputTokens),
            rows.Sum(row => row.OutputTokens),
            cost,
            unpriced,
            LatencyBands.Percentile(latency, 0.5m),
            LatencyBands.Percentile(latency, 0.95m));

        return new RoleModelMetrics(
            key.Role,
            key.Provider,
            key.ModelId,
            calls,
            TradeStats.Compute(trades, cost, unpriced, settings.UsdQuoteAssets),
            key.Role == AiRole.Strategist
                ? new StrategistMetrics(rows.Sum(row => row.Holds), rows.Sum(row => row.Entries), rows.Sum(row => row.Exits), rows.Sum(row => row.Adjustments))
                : null,
            key.Role == AiRole.RiskCritic
                ? new CriticMetrics(rows.Sum(row => row.Approvals), rows.Sum(row => row.Modifications), rows.Sum(row => row.Rejections))
                : null,
            key.Role is AiRole.TechnicalAnalyst or AiRole.SentimentAnalyst
                ? new AnalystMetrics(rows.Sum(row => row.BullishCalls), rows.Sum(row => row.BearishCalls), rows.Sum(row => row.Hits), rows.Sum(row => row.Misses))
                : null);
    }

    private sealed record ModelKey(AiRole Role, string Provider, string ModelId);
}
