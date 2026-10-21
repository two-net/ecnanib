using System.Collections.Concurrent;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.MarketData.Services;

/// <summary>
/// The symbol listing per environment and market (FR-DATA-01): loaded from the exchange on first use, with one read
/// shared by concurrent callers, and replaced by <see cref="RefreshAsync"/> on the refresher's schedule. A read that
/// fails is not cached, so the next caller tries again; a refresh that fails keeps the listing it has.
/// </summary>
internal sealed class SymbolCatalog : ISymbolCatalog
{
    private readonly IExchangeClientFactory _exchange;
    private readonly TimeProvider _time;
    private readonly ILogger<SymbolCatalog> _logger;
    private readonly ConcurrentDictionary<(ExchangeEnvironment Environment, MarketType Market), Entry> _entries = new();

    public SymbolCatalog(IExchangeClientFactory exchange, TimeProvider time, ILogger<SymbolCatalog> logger)
    {
        _exchange = exchange;
        _time = time;
        _logger = logger;
    }

    public async Task<SymbolInfo?> FindAsync(ExchangeEnvironment environment, MarketType market, string symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var listing = await GetListingAsync(environment, market, cancellationToken).ConfigureAwait(false);
        return listing.BySymbol.TryGetValue(symbol.Trim(), out var info) ? info : null;
    }

    public async Task<IReadOnlyList<SymbolInfo>> ListAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken)
    {
        var listing = await GetListingAsync(environment, market, cancellationToken).ConfigureAwait(false);
        return listing.Symbols;
    }

    /// <summary>The listings loaded so far, oldest first.</summary>
    internal IReadOnlyList<SymbolListingState> States =>
        _entries.Select(e => (e.Key, Listing: e.Value.Listing)).Where(e => e.Listing is not null)
            .Select(e => new SymbolListingState(e.Key.Environment, e.Key.Market, e.Listing!.Symbols.Count, e.Listing.LoadedAt))
            .OrderBy(s => s.LoadedAt).ThenBy(s => s.Environment).ThenBy(s => s.Market).ToList();

    /// <summary>Re-reads one loaded listing; throws and keeps the previous listing when the exchange does not answer.</summary>
    internal async Task<SymbolListingState> RefreshAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd((environment, market), _ => new Entry());
        var listing = await ReadAsync(environment, market, cancellationToken).ConfigureAwait(false);
        entry.Replace(listing);
        return new SymbolListingState(environment, market, listing.Symbols.Count, listing.LoadedAt);
    }

    private async Task<SymbolListing> GetListingAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd((environment, market), _ => new Entry());
        if (entry.Listing is { } loaded)
            return loaded;
        // One read serves every caller waiting for it; each caller stops waiting on its own token.
        return await entry.LoadAsync(() => ReadAsync(environment, market, CancellationToken.None)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SymbolListing> ReadAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken)
    {
        var info = await _exchange.GetPublicData(environment, market).GetExchangeInfoAsync(cancellationToken).ConfigureAwait(false);
        var bySymbol = new Dictionary<string, SymbolInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in info.Symbols)
            bySymbol.TryAdd(symbol.Symbol, symbol);
        var listing = new SymbolListing(info.Symbols, bySymbol, _time.GetUtcNow());
        _logger.LogInformation("Symbol listing of {Environment} {Market} loaded: {Symbols} symbol(s), {Trading} trading", environment, market, info.Symbols.Count, info.Symbols.Count(s => s.IsTrading));
        return listing;
    }

    private sealed class Entry
    {
        private readonly object _lock = new();
        private Task<SymbolListing>? _loading;

        public volatile SymbolListing? Listing;

        public Task<SymbolListing> LoadAsync(Func<Task<SymbolListing>> read)
        {
            lock (_lock)
            {
                if (Listing is { } loaded)
                    return Task.FromResult(loaded);
                // A read in flight is shared; a finished one here has failed (a success set the listing), so it is tried again.
                if (_loading is null || _loading.IsCompleted)
                    _loading = RunAsync(read);
                return _loading;
            }
        }

        public void Replace(SymbolListing listing)
        {
            lock (_lock)
                Listing = listing;
        }

        private async Task<SymbolListing> RunAsync(Func<Task<SymbolListing>> read)
        {
            var listing = await read().ConfigureAwait(false);
            Replace(listing);
            return listing;
        }
    }
}

internal sealed record SymbolListing(IReadOnlyList<SymbolInfo> Symbols, IReadOnlyDictionary<string, SymbolInfo> BySymbol, DateTimeOffset LoadedAt);

/// <summary>One loaded listing: where it is from, how many symbols it has and when it was read.</summary>
internal sealed record SymbolListingState(ExchangeEnvironment Environment, MarketType Market, int Symbols, DateTimeOffset LoadedAt);
