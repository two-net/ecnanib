using Ecnanib.Database.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Database.Retention;

/// <summary>
/// Scheduled deletion of expired log entries, candles and deliberation transcripts (NFR-DATA-02, FR-LOG-06,
/// FR-DATA-05, NFR-DATA-03). Runs every <see cref="RetentionOptions.SweepInterval"/>, deleting in batches of
/// <see cref="RetentionOptions.DeleteBatchSize"/> through the ORM (DB-01). Never touches decisions, trades or
/// experience statistics, and never stops the host: every failure is logged at Warning and retried next sweep.
/// </summary>
internal sealed class RetentionSweeper : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<RetentionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionSweeper> _logger;

    public RetentionSweeper(IDbContextFactory<EcnanibDbContext> factory, IOptions<RetentionOptions> options, TimeProvider time, ILogger<RetentionSweeper> logger)
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
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retention sweep failed; next attempt in {Interval}", _options.Value.SweepInterval);
            }

            if (!await DelayAsync(_options.Value.SweepInterval, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>One sweep over the three retained-for-a-period tables. Each target is independent: a failure in one is logged and the others still run.</summary>
    internal async Task<RetentionSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var now = _time.GetUtcNow();
        var failures = 0;

        var logCutoff = now - options.LogRetention;
        var logs = await SweepTargetAsync("log entries", logCutoff,
            db => db.LogEntries.Where(l => l.Timestamp < logCutoff).OrderBy(l => l.Id), options.DeleteBatchSize, cancellationToken).ConfigureAwait(false);
        failures += logs < 0 ? 1 : 0;

        var candleCutoff = now - options.CandleRetention;
        var candles = await SweepTargetAsync("candles", candleCutoff,
            db => db.Candles.Where(c => c.OpenTime < candleCutoff).OrderBy(c => c.OpenTime), options.DeleteBatchSize, cancellationToken).ConfigureAwait(false);
        failures += candles < 0 ? 1 : 0;

        var transcriptCutoff = now - options.DeliberationTranscriptRetention;
        var transcripts = await SweepTargetAsync("deliberation transcripts", transcriptCutoff,
            db => db.DeliberationTranscripts.Where(t => t.CreatedAt < transcriptCutoff).OrderBy(t => t.CreatedAt), options.DeleteBatchSize, cancellationToken).ConfigureAwait(false);
        failures += transcripts < 0 ? 1 : 0;

        var result = new RetentionSweepResult(Math.Max(logs, 0), Math.Max(candles, 0), Math.Max(transcripts, 0), failures);
        var level = result.TotalDeleted == 0 && result.Failures == 0 ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "Retention sweep deleted {LogEntries} log entries (older than {LogDays} d), {Candles} candles (older than {CandleDays} d) and {Transcripts} deliberation transcripts (older than {TranscriptDays} d); {Failures} target(s) failed",
            result.LogEntries, options.LogDays, result.Candles, options.CandleDays, result.DeliberationTranscripts, options.DeliberationTranscriptDays, result.Failures);
        return result;
    }

    /// <summary>Returns rows deleted, or -1 when the target failed.</summary>
    private async Task<int> SweepTargetAsync<TEntity>(string target, DateTimeOffset cutoff, Func<EcnanibDbContext, IOrderedQueryable<TEntity>> expired, int batchSize, CancellationToken cancellationToken)
        where TEntity : class
    {
        try
        {
            var total = 0;
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // PostgreSQL has no DELETE ... LIMIT; EF rewrites Take into a key subquery, which also works for composite keys.
                var deleted = await expired(db).Take(batchSize).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                total += deleted;
                if (deleted < batchSize)
                    return total;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention sweep of {Target} older than {Cutoff:O} failed; it will be retried next sweep", target, cutoff);
            return -1;
        }
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
