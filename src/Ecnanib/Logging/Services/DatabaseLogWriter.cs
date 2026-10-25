using Ecnanib.Database.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Ecnanib.Logging.Services;

/// <summary>
/// Drains <see cref="DatabaseLogSink"/> into <c>log_entries</c> in batches (FR-LOG-01). It starts with the host, after
/// the migrations, so nothing is written to a schema that is not there yet, and it is registered first so it stops
/// last and still writes the other services' shutdown logs. A batch the database refuses is kept and retried with a
/// growing delay for up to <see cref="RetryTimeLimit"/>, then given up; the queue meanwhile drops its newest events
/// once full. Nothing here throws or stops the host (FR-LOG-08): every failure is a warning, at most one a minute, and
/// because the writer's whole flow runs under <see cref="DatabaseLogSink.Suppress"/>, its own warnings and the EF
/// logs of its inserts reach the console only.
/// </summary>
internal sealed class DatabaseLogWriter : BackgroundService
{
    internal const int DefaultBatchSize = 100;

    internal static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan RetryTimeLimit = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(5);

    private readonly DatabaseLogSink _sink;
    private readonly ILogEntryStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseLogWriter> _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    public DatabaseLogWriter(DatabaseLogSink sink, ILogEntryStore store, TimeProvider time, ILogger<DatabaseLogWriter> logger)
        : this(sink, store, time, logger, DefaultBatchSize, DefaultFlushInterval)
    {
    }

    internal DatabaseLogWriter(DatabaseLogSink sink, ILogEntryStore store, TimeProvider time, ILogger<DatabaseLogWriter> logger, int batchSize, TimeSpan flushInterval)
    {
        _sink = sink;
        _store = store;
        _time = time;
        _logger = logger;
        _batchSize = batchSize;
        _flushInterval = flushInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var suppressed = DatabaseLogSink.Suppress();
        var reader = _sink.Reader;
        List<LogEntry>? pending = null;
        var attempts = 0;
        DateTimeOffset? failingSince = null;
        DateTimeOffset? lastWarning = null;
        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (pending is null)
                {
                    await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);
                    // A trickle is gathered into one statement; a full batch goes at once.
                    if (reader.Count < _batchSize)
                        await Task.Delay(_flushInterval, _time, stoppingToken).ConfigureAwait(false);
                    pending = Take(_batchSize);
                    if (pending.Count == 0)
                    {
                        pending = null;
                        continue;
                    }
                }

                await _store.WriteAsync(pending, stoppingToken).ConfigureAwait(false);
                if (attempts > 0)
                    _logger.LogInformation("Database log writes resumed after {Attempts} failed attempt(s); {Count} log entries written", attempts, pending.Count);
                pending = null;
                attempts = 0;
                failingSince = null;
                lastWarning = null;
                retryDelay = InitialRetryDelay;
                ReportDropped();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempts++;
                var now = _time.GetUtcNow();
                failingSince ??= now;
                if (lastWarning is null || now - lastWarning.Value >= WarningInterval)
                {
                    _logger.LogWarning(ex, "Could not write {Count} log entries to the database (attempt {Attempts}); the console remains the only sink and the batch is retried in {Delay}",
                        pending?.Count ?? 0, attempts, retryDelay);
                    lastWarning = now;
                }

                if (now - failingSince.Value >= RetryTimeLimit)
                {
                    _logger.LogWarning("Giving up on {Count} log entries after {Minutes:0} minutes of database failures; they are lost", pending?.Count ?? 0, RetryTimeLimit.TotalMinutes);
                    pending = null;
                    failingSince = null;
                }

                if (!await DelayAsync(retryDelay, stoppingToken).ConfigureAwait(false))
                    break;
                retryDelay = retryDelay * 2 > MaxRetryDelay ? MaxRetryDelay : retryDelay * 2;
            }
        }

        await DrainAsync(pending).ConfigureAwait(false);
    }

    /// <summary>On stop, what is pending and queued gets one bounded attempt, so the shutdown logs of the services that stopped first still land.</summary>
    private async Task DrainAsync(List<LogEntry>? pending)
    {
        var entries = pending ?? [];
        entries.AddRange(Take(int.MaxValue));
        if (entries.Count == 0)
            return;

        using var timeout = new CancellationTokenSource(ShutdownDrain, _time);
        var written = 0;
        try
        {
            foreach (var chunk in entries.Chunk(_batchSize))
            {
                await _store.WriteAsync(chunk, timeout.Token).ConfigureAwait(false);
                written += chunk.Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Count} log entries could not be written during shutdown and are lost", entries.Count - written);
        }
    }

    private List<LogEntry> Take(int max)
    {
        var batch = new List<LogEntry>(Math.Min(max, _batchSize));
        while (batch.Count < max && _sink.Reader.TryRead(out var logEvent))
            batch.Add(Map(logEvent));
        return batch;
    }

    private static LogEntry Map(LogEvent logEvent)
    {
        try
        {
            return LogEventMapper.Map(logEvent);
        }
        catch (Exception ex)
        {
            return LogEventMapper.Fallback(logEvent, ex);
        }
    }

    private void ReportDropped()
    {
        var dropped = _sink.TakeDropped();
        if (dropped > 0)
            _logger.LogWarning("{Dropped} log entries were dropped because the database queue was full", dropped);
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
