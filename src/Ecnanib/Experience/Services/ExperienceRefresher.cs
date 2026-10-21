using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Experience.Services;

/// <summary>
/// Recomputes the experience statistics every <see cref="ExperienceOptions.RefreshInterval"/> (FR-EXP-04,
/// FR-EXP-05): every row of <c>experience_stats</c> is replaced in one transaction, so readers see the old set or
/// the new one, never a mix. Also expires shadow outcomes left pending by agents that stopped or moved on (FR-EXP-03).
/// Never stops the host: a failure is logged at Warning and retried next run.
/// </summary>
internal sealed class ExperienceRefresher : BackgroundService
{
    /// <summary>Staggered after the retention sweeper's first minute, the key re-validator's second and the model sync's third.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(4);

    /// <summary>Grace beyond the candles a shadow could still need before an unreachable one is written off.</summary>
    internal static readonly TimeSpan StaleGrace = TimeSpan.FromDays(1);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<ExperienceOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExperienceRefresher> _logger;

    public ExperienceRefresher(IDbContextFactory<EcnanibDbContext> factory, IOptions<ExperienceOptions> options, TimeProvider time, ILogger<ExperienceRefresher> logger)
    {
        _factory = factory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(InitialDelay, stoppingToken).ConfigureAwait(false))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Experience refresh failed; next attempt in {Interval}", _options.Value.RefreshInterval);
            }

            if (!await DelayAsync(_options.Value.RefreshInterval, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>One pass: the statistics, then the stale shadows. Each is independent: a failure in one is logged and the other still runs.</summary>
    internal async Task<ExperienceRefreshResult> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var rows = -1;
        var expired = -1;
        try
        {
            rows = await RefreshStatisticsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _logger.LogWarning(ex, "Experience statistics could not be recomputed; they will be retried next run");
        }

        try
        {
            expired = await ExpireStaleShadowsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _logger.LogWarning(ex, "Stale shadow outcomes could not be expired; they will be retried next run");
        }

        var result = new ExperienceRefreshResult(Math.Max(rows, 0), Math.Max(expired, 0), failures);
        var level = result.Rows == 0 && result.ShadowsExpired == 0 && failures == 0 ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "Experience refresh wrote {Rows} statistic row(s) and expired {Expired} stale shadow outcome(s); {Failures} step(s) failed", result.Rows, result.ShadowsExpired, failures);
        return result;
    }

    private async Task<int> RefreshStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var rows = await ExperienceAggregates.ComputeAsync(db, now, cancellationToken).ConfigureAwait(false);

        // The retrying strategy re-runs the delegate, so it starts from a clean tracker and unsaved rows every time.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            foreach (var row in rows)
                row.Id = 0;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.ExperienceStats.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.ExperienceStats.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>
    /// A shadow still pending after the candles it could need plus a day of grace belongs to an agent that stopped or
    /// trades another symbol or timeframe now: no evaluation will walk it, so it is written off as stale.
    /// </summary>
    private async Task<int> ExpireStaleShadowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var pending = await db.ShadowOutcomes
            .Where(s => s.Result == ShadowResult.Pending)
            .Select(s => new { Shadow = s, s.Decision.Evaluation.Timeframe, s.Decision.Evaluation.CandleOpenTime })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var expired = 0;
        foreach (var item in pending)
        {
            var detail = ShadowDetail.Parse(item.Shadow.Detail);
            ShadowStep step;
            if (detail is null)
            {
                step = ShadowWalk.Expire(null, ShadowDetail.NoDetail);
            }
            else
            {
                var horizon = item.CandleOpenTime + (detail.EntryTimeoutCandles + detail.HorizonCandles + 1) * item.Timeframe.ToTimeSpan() + StaleGrace;
                if (now < horizon)
                    continue;
                step = ShadowWalk.Expire(detail, ShadowDetail.Stale);
            }

            item.Shadow.Result = step.Result;
            item.Shadow.RMultiple = step.RMultiple;
            item.Shadow.ResolvedAt = now;
            item.Shadow.Detail = step.Detail.ToJson();
            expired++;
        }

        if (expired > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired;
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary><see cref="Rows"/> statistic rows written, <see cref="ShadowsExpired"/> pending shadows written off, <see cref="Failures"/> steps that threw (the database).</summary>
internal sealed record ExperienceRefreshResult(int Rows, int ShadowsExpired, int Failures);
