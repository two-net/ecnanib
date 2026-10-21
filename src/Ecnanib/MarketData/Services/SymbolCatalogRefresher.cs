using Ecnanib.MarketData.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.MarketData.Services;

/// <summary>
/// Refreshes every loaded symbol listing every <see cref="MarketDataOptions.SymbolRefreshInterval"/>, at most a day
/// apart (FR-DATA-01), one listing at a time, each in its own try/catch. Never stops the host: a failure is logged at
/// Warning, the listing it has stays, and the next run tries again.
/// </summary>
internal sealed class SymbolCatalogRefresher : BackgroundService
{
    /// <summary>Staggered after the retention sweeper, the key re-validator, the model sync and the experience refresher.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    private readonly SymbolCatalog _catalog;
    private readonly IOptions<MarketDataOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SymbolCatalogRefresher> _logger;

    public SymbolCatalogRefresher(SymbolCatalog catalog, IOptions<MarketDataOptions> options, TimeProvider time, ILogger<SymbolCatalogRefresher> logger)
    {
        _catalog = catalog;
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
                _logger.LogWarning(ex, "The symbol listing refresh failed; next attempt in {Interval}", _options.Value.SymbolRefreshInterval);
            }

            if (!await DelayAsync(_options.Value.SymbolRefreshInterval, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>One pass over every loaded listing. Each is independent: a failure in one is logged and the others still run.</summary>
    internal async Task<SymbolRefreshResult> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var refreshed = 0;
        var failed = 0;
        foreach (var state in _catalog.States)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _catalog.RefreshAsync(state.Environment, state.Market, cancellationToken).ConfigureAwait(false);
                refreshed++;
                _logger.LogInformation("Symbol listing of {Environment} {Market} refreshed: {Symbols} symbol(s)", result.Environment, result.Market, result.Symbols);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Symbol listing of {Environment} {Market} could not be refreshed; the listing of {LoadedAt:O} stays until the next attempt", state.Environment, state.Market, state.LoadedAt);
            }
        }

        var summary = new SymbolRefreshResult(refreshed, failed);
        _logger.Log(failed == 0 ? LogLevel.Debug : LogLevel.Information, "Symbol listing refresh checked {Listings} listing(s): {Refreshed} refreshed, {Failed} failed", refreshed + failed, refreshed, failed);
        return summary;
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

internal sealed record SymbolRefreshResult(int Refreshed, int Failed);
