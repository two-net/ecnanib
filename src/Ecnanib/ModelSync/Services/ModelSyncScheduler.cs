using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.ModelSync.Services;

/// <summary>
/// Periodic catalog sync (FR-SYNC-02, FR-SYNC-03). Every <see cref="AiOptions.ModelSyncInterval"/> each provider that
/// has a valid stored key is synced with trigger Scheduled, one provider at a time, each in its own try/catch. Never
/// stops the host: a failure is logged at Warning and retried next run.
/// </summary>
internal sealed class ModelSyncScheduler : BackgroundService
{
    /// <summary>Staggered after the retention sweeper's first minute and the key re-validator's second.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    private readonly ModelSyncRunner _runner;
    private readonly IOptions<AiOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ModelSyncScheduler> _logger;

    public ModelSyncScheduler(ModelSyncRunner runner, IOptions<AiOptions> options, TimeProvider time, ILogger<ModelSyncScheduler> logger)
    {
        _runner = runner;
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
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled model sync failed; next attempt in {Interval}", _options.Value.ModelSyncInterval);
            }

            if (!await DelayAsync(_options.Value.ModelSyncInterval, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>One pass over every provider. Each is independent: a failure in one is logged and the others still run.</summary>
    internal async Task<ModelSyncRunResult> SyncOnceAsync(CancellationToken cancellationToken)
    {
        var synced = 0;
        var noKey = 0;
        var failed = 0;
        var failures = 0;
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await _runner.SyncAsync(provider, SyncTrigger.Scheduled, null, cancellationToken).ConfigureAwait(false);
                switch (outcome.Status)
                {
                    case ModelSyncStatus.Synced:
                        synced++;
                        break;
                    case ModelSyncStatus.NoValidKey:
                        noKey++;
                        break;
                    case ModelSyncStatus.Failed:
                        failed++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "Scheduled model sync for {Provider} failed; it will be retried next run", provider);
            }
        }

        var result = new ModelSyncRunResult(synced, noKey, failed, failures);
        var level = failed == 0 && failures == 0 ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "Scheduled model sync checked {Providers} provider(s): {Synced} synced, {NoValidKey} without a valid key, {Failed} failed, {Failures} failure(s)",
            synced + noKey + failed + failures, synced, noKey, failed, failures);
        return result;
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

/// <summary><see cref="Failed"/> providers answered with an error (recorded as their last result); <see cref="Failures"/> are exceptions outside the provider call, such as the database.</summary>
internal sealed record ModelSyncRunResult(int Synced, int NoValidKey, int Failed, int Failures);
