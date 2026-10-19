using Ecnanib.Database;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Periodic re-validation of stored keys (FR-KEY-07). Runs every <see cref="KeysOptions.RevalidationInterval"/> over
/// the keys whose last check is at least that old, one at a time, each in its own try/catch. Never stops the host:
/// a failure is logged at Warning and retried next run. Does not feed the catalog: the scheduled sync has its own
/// cadence (FR-SYNC-02, FR-SYNC-03).
/// </summary>
internal sealed class KeyRevalidator : BackgroundService
{
    /// <summary>Staggered from the retention sweeper's first minute.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly StoredKeyRevalidation _revalidation;
    private readonly IOptions<KeysOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<KeyRevalidator> _logger;

    public KeyRevalidator(IDbContextFactory<EcnanibDbContext> factory, StoredKeyRevalidation revalidation, IOptions<KeysOptions> options, TimeProvider time, ILogger<KeyRevalidator> logger)
    {
        _factory = factory;
        _revalidation = revalidation;
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
                await RevalidateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Key re-validation failed; next attempt in {Interval}", _options.Value.RevalidationInterval);
            }

            if (!await DelayAsync(_options.Value.RevalidationInterval, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>One pass over every due key. Each key is independent: a failure in one is logged and the others still run.</summary>
    internal async Task<KeyRevalidationResult> RevalidateOnceAsync(CancellationToken cancellationToken)
    {
        var due = _time.GetUtcNow() - _options.Value.RevalidationInterval;
        List<Guid> exchangeIds;
        List<Guid> providerIds;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            exchangeIds = await db.ExchangeKeys.AsNoTracking()
                .Where(k => k.LastValidatedAt == null || k.LastValidatedAt <= due)
                .OrderBy(k => k.Id)
                .Select(k => k.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            providerIds = await db.ProviderKeys.AsNoTracking()
                .Where(k => k.LastValidatedAt == null || k.LastValidatedAt <= due)
                .OrderBy(k => k.Id)
                .Select(k => k.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var tally = new Tally();
        foreach (var id in exchangeIds)
            await CheckAsync("exchange", id, token => _revalidation.RevalidateExchangeKeyAsync(id, token), tally, cancellationToken).ConfigureAwait(false);
        foreach (var id in providerIds)
            await CheckAsync("provider", id, token => _revalidation.RevalidateProviderKeyAsync(id, syncCatalog: false, token), tally, cancellationToken).ConfigureAwait(false);

        var result = new KeyRevalidationResult(tally.Checked, tally.Valid, tally.Invalid, tally.Failed, tally.AgentsFailed, tally.Failures);
        var level = result.Invalid == 0 && result.Failed == 0 && result.Failures == 0 ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "Key re-validation checked {Checked} key(s): {Valid} valid, {Invalid} invalid, {Unchecked} not checked, {AgentsFailed} agent(s) entered Error, {Failures} failure(s)",
            result.Checked, result.Valid, result.Invalid, result.Failed, result.AgentsFailed, result.Failures);
        return result;
    }

    private async Task CheckAsync(string kind, Guid id, Func<CancellationToken, Task<KeyValidationResult>> check, Tally tally, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await check(cancellationToken).ConfigureAwait(false);
            tally.Add(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            tally.Failures++;
            _logger.LogWarning(ex, "Re-validation of {Kind} key {KeyId} failed; it will be retried next run", kind, id);
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

    private sealed class Tally
    {
        public int Checked;
        public int Valid;
        public int Invalid;
        public int Failed;
        public int AgentsFailed;
        public int Failures;

        public void Add(KeyValidationResult result)
        {
            switch (result.Outcome)
            {
                case KeyValidationOutcome.Valid:
                    Checked++;
                    Valid++;
                    break;
                case KeyValidationOutcome.Invalid:
                    Checked++;
                    Invalid++;
                    AgentsFailed += result.AgentsFailed;
                    break;
                case KeyValidationOutcome.Failed:
                    Checked++;
                    Failed++;
                    break;
            }
        }
    }
}

/// <summary><see cref="Failed"/> keys could not be checked and keep their status; <see cref="Failures"/> are exceptions outside the validators.</summary>
internal sealed record KeyRevalidationResult(int Checked, int Valid, int Invalid, int Failed, int AgentsFailed, int Failures);
