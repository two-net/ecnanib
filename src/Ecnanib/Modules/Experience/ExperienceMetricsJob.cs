using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// Builds every user's metrics from their councils' calls, every hour (FR-EXP-01, FR-EXP-02). For each user and
/// timeframe with cycles since its days were last built for good, it builds every day from there to today, a month at a
/// time, replacing what an earlier run built, and moves past the days that are final. So today's metrics are at most an
/// hour old, and a final day is never built again, which lets retention purge its traces (#50). One user's failure is
/// logged and leaves their days for the next run (NFR-REL-05). See Trading experience in docs/architecture.md.
/// </summary>
internal sealed class ExperienceMetricsJob(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ICouncilActivity activity,
    IMarketData marketData,
    IAgents agents,
    ExperienceSettings settings,
    TimeProvider time,
    ILogger<ExperienceMetricsJob> logger) : IRecurringJob
{
    public const string JobName = "experience-metrics";

    /// <summary>The most days one query reads, so a long backlog, such as on the first run, is built in parts.</summary>
    public const int ChunkDays = 31;

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromHours(1));

    /// <summary>Room for the first run on a database with a long history.</summary>
    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(30);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        Dictionary<(Guid UserId, Timeframe Timeframe), DateTimeOffset> progress;

        await using (var database = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            progress = await database.Set<ExperienceProgressRow>()
                .AsNoTracking()
                .ToDictionaryAsync(row => (row.UserId, row.Timeframe), row => row.BuiltUntil, cancellationToken);
        }

        // Every pair with a progress row is final up to it, and a new pair has none, so this finds everything to build.
        var since = progress.Count == 0 ? DateTimeOffset.UnixEpoch : progress.Values.Min();
        var keys = await activity.ListAsync(since, cancellationToken);
        var prices = new Dictionary<SeriesKey, IReadOnlyDictionary<DateTimeOffset, decimal>?>();
        var days = 0;
        var failed = 0;

        foreach (var key in keys.OrderBy(key => key.UserId).ThenBy(key => key.Timeframe))
        {
            if (context.Stopping.IsCancellationRequested)
            {
                break;
            }

            try
            {
                days += await BuildAsync(key, progress.TryGetValue((key.UserId, key.Timeframe), out var until) ? until : null, now, prices, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(exception, "A user's {Timeframe} trading experience metrics couldn't be built, so they're tried again next time", key.Timeframe);
            }
        }

        await AdvanceIdleAsync(keys, now, cancellationToken);

        context.Details = $"Users and timeframes: {keys.Count}, days built: {days}, failed: {failed}, price series read: {prices.Count}";
        context.Stopping.ThrowIfCancellationRequested();
    }

    /// <summary>Builds the pair's days from where they were last built for good up to today. Returns how many.</summary>
    private async Task<int> BuildAsync(
        CouncilActivityKey key,
        DateTimeOffset? builtUntil,
        DateTimeOffset now,
        Dictionary<SeriesKey, IReadOnlyDictionary<DateTimeOffset, decimal>?> prices,
        CancellationToken cancellationToken)
    {
        var first = ExperienceDays.Start(key.FirstStartedAt);
        var start = builtUntil is { } until && until > first ? until : first;
        var end = ExperienceDays.Start(now) + ExperienceDays.Day;
        var final = ExperienceDays.FinalUntil(key.Timeframe, settings.AnalystHorizon, now);

        if (builtUntil is null)
        {
            // Before anything else, so a failure keeps the next run's search from moving past this pair's days.
            await StartProgressAsync(key, start, now, cancellationToken);
        }

        var advancing = true;
        var days = 0;

        for (var from = start; from < end; from += ExperienceDays.Day * ChunkDays)
        {
            var to = from + (ExperienceDays.Day * ChunkDays) < end ? from + (ExperienceDays.Day * ChunkDays) : end;
            var calls = await activity.ListCallsAsync(key.UserId, key.Timeframe, from, to, cancellationToken);

            await ReadPricesAsync(calls, now, prices, cancellationToken);

            var built = SnapshotBuilder.Build(key.UserId, calls, series => prices.GetValueOrDefault(series), settings.AnalystHorizon, now);
            var limit = to < final ? to : final;
            DateTimeOffset? reached = null;

            // A part that starts after the last final day has nothing to move past.
            if (advancing && limit > from)
            {
                reached = Reach(from, limit, built.DaysMissingPrices, key.Timeframe, now);
                advancing = reached >= limit;
            }

            await SaveAsync(key, from, to, built, reached, now, cancellationToken);
            days += (int)((to - from) / ExperienceDays.Day);
        }

        return days;
    }

    /// <summary>
    /// How far the days from <paramref name="from"/> are final: up to <paramref name="limit"/>, or the first day that's
    /// missing prices, unless it has been final for longer than <see cref="ExperienceDays.PriceGrace"/>.
    /// </summary>
    private DateTimeOffset Reach(
        DateTimeOffset from, DateTimeOffset limit, IReadOnlySet<DateTimeOffset> missing, Timeframe timeframe, DateTimeOffset now)
    {
        var day = from;

        while (day < limit
            && (!missing.Contains(day) || ExperienceDays.FinalAt(day, timeframe, settings.AnalystHorizon) + ExperienceDays.PriceGrace <= now))
        {
            day += ExperienceDays.Day;
        }

        return day;
    }

    /// <summary>
    /// Reads the candles of each pair whose analysts made a call that can be scored, once a run, through the same
    /// shared series a cycle reads. A pair whose candles can't be read leaves its calls unscored.
    /// </summary>
    private async Task ReadPricesAsync(
        IReadOnlyList<CouncilCallRecord> calls,
        DateTimeOffset now,
        Dictionary<SeriesKey, IReadOnlyDictionary<DateTimeOffset, decimal>?> prices,
        CancellationToken cancellationToken)
    {
        var needed = calls
            .Where(call => call.Status == CouncilCallStatus.Answered && SnapshotBuilder.DirectionOf(call) is not null
                && call.CandleClose + (ExperienceDays.Interval(call.Timeframe) * settings.AnalystHorizon) <= now)
            .Select(call => new SeriesKey(call.Market, call.Environment, call.Symbol, call.Timeframe))
            .Distinct()
            .Where(series => !prices.ContainsKey(series))
            .ToList();

        foreach (var series in needed)
        {
            try
            {
                var interval = ExperienceDays.Interval(series.Timeframe);
                var candles = await marketData.ReadCandlesAsync(
                    new CandleRequest(series.Market, series.Environment, series.Symbol, series.Timeframe, CandleRequest.MaxCount),
                    cancellationToken);

                prices[series] = candles.ToDictionary(candle => candle.OpenTime + interval, candle => candle.Close);
            }
            catch (Exception exception) when (exception is MarketDataException or ArgumentException)
            {
                logger.LogWarning(
                    "Candles for {Market} {Environment} {Symbol} {Timeframe} couldn't be read, so analysts' calls on them stay unscored this time",
                    series.Market,
                    series.Environment,
                    series.Symbol,
                    series.Timeframe);
                prices[series] = null;
            }
        }
    }

    private async Task StartProgressAsync(CouncilActivityKey key, DateTimeOffset start, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        context.Add(new ExperienceProgressRow { UserId = key.UserId, Timeframe = key.Timeframe, BuiltUntil = start, UpdatedAt = now });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the user's snapshots in the timeframe for the days from <paramref name="from"/> to
    /// <paramref name="to"/>, adds the attributions not yet kept, and moves the progress to <paramref name="reached"/>,
    /// all at once. It holds the user's <see cref="ExperienceLock"/>, and keeps nothing of an agent deleted since the
    /// calls were read.
    /// </summary>
    private async Task SaveAsync(
        CouncilActivityKey key, DateTimeOffset from, DateTimeOffset to, BuiltDays built, DateTimeOffset? reached, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await ExperienceLock.TakeAsync(context, key.UserId, cancellationToken);

        var kept = (await agents.ListAsync(key.UserId, includeArchived: true, cancellationToken)).Select(agent => agent.Id).ToHashSet();

        await context.Set<PerformanceSnapshotRow>()
            .Where(row => row.UserId == key.UserId && row.Timeframe == key.Timeframe && row.PeriodStart >= from && row.PeriodStart < to)
            .ExecuteDeleteAsync(cancellationToken);

        context.AddRange(built.Snapshots.Where(row => kept.Contains(row.AgentId)));

        var attributions = built.Attributions.Where(row => kept.Contains(row.AgentId)).ToList();
        var cycles = attributions.Select(row => row.CycleId).Distinct().ToList();
        var existing = await context.Set<CycleAttributionRow>()
            .Where(row => cycles.Contains(row.CycleId))
            .Select(row => new { row.CycleId, row.Role })
            .ToListAsync(cancellationToken);
        var known = existing.Select(row => (row.CycleId, row.Role)).ToHashSet();

        context.AddRange(attributions.Where(row => !known.Contains((row.CycleId, row.Role))));

        if (reached is { } until)
        {
            var progress = await context.Set<ExperienceProgressRow>()
                .SingleAsync(row => row.UserId == key.UserId && row.Timeframe == key.Timeframe, cancellationToken);

            if (until > progress.BuiltUntil)
            {
                progress.BuiltUntil = until;
                progress.UpdatedAt = now;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Moves on every pair with nothing new to build: it had no cycle since its days were last built for good, so the
    /// days up to those final now have nothing in them. Otherwise an idle user would hold back the next run's search.
    /// </summary>
    private async Task AdvanceIdleAsync(IReadOnlyList<CouncilActivityKey> keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        foreach (var timeframe in Enum.GetValues<Timeframe>())
        {
            var final = ExperienceDays.FinalUntil(timeframe, settings.AnalystHorizon, now);
            var busy = keys.Where(key => key.Timeframe == timeframe).Select(key => key.UserId).ToList();

            await context.Set<ExperienceProgressRow>()
                .Where(row => row.Timeframe == timeframe && row.BuiltUntil < final && !busy.Contains(row.UserId))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.BuiltUntil, final).SetProperty(row => row.UpdatedAt, now),
                    cancellationToken);
        }
    }
}
