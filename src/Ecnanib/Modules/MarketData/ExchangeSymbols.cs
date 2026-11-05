using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// Reads through to the adapter's exchange information, which refreshes itself (EXT-BIN-06), and stores
/// each new copy in <c>exchange_symbols</c>. When Binance can't be reached, the stored copy answers.
/// There's no job, because the adapter's copy is already as fresh as the setting asks, and a job at
/// startup would call Binance from every test application. See Market data in docs/architecture.md.
/// </summary>
internal sealed class ExchangeSymbols(
    IExchangeMarketData exchange,
    IDbContextFactory<EcnanibDbContext> contexts,
    ILogger<ExchangeSymbols> logger)
    : IExchangeSymbols
{
    /// <summary>When the stored copy for a market and environment was refreshed, once it's known.</summary>
    private readonly ConcurrentDictionary<(Market, TradingEnvironment), DateTimeOffset> _stored = new();

    private readonly ConcurrentDictionary<(Market, TradingEnvironment), SemaphoreSlim> _gates = new();

    public async Task<ExchangeSymbolInfo?> FindAsync(Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken)
    {
        if (!Failures.IsSymbol(symbol))
        {
            return null;
        }

        if (await ExchangeInfoAsync(market, environment, cancellationToken) is { } info)
        {
            return info.Find(symbol) is { } rules ? ExchangeSymbol.ToInfo(market, environment, rules, info.RefreshedAt) : null;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await Stored(context, market, environment).AsNoTracking().FirstOrDefaultAsync(stored => stored.Symbol == symbol, cancellationToken);

        return row?.ToInfo();
    }

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        if (await ExchangeInfoAsync(market, environment, cancellationToken) is { } info)
        {
            return
            [
                .. info.Symbols.Values
                    .OrderBy(rules => rules.Symbol, StringComparer.Ordinal)
                    .Select(rules => ExchangeSymbol.ToInfo(market, environment, rules, info.RefreshedAt)),
            ];
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await Stored(context, market, environment).AsNoTracking().ToListAsync(cancellationToken);

        return [.. rows.OrderBy(row => row.Symbol, StringComparer.Ordinal).Select(row => row.ToInfo())];
    }

    public async Task RefreshAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        if (!exchange.ExpireExchangeInfo(market, environment))
        {
            return;
        }

        try
        {
            await ExchangeInfoAsync(market, environment, cancellationToken);
        }
        catch (MarketDataException exception)
        {
            // Nothing is stored, and Binance can't be reached: the next read tries again.
            logger.LogWarning(exception, "Refreshing Binance exchange information for {Market} {Environment} failed", market, environment);
        }
    }

    /// <summary>
    /// The adapter's copy, after storing it if it's newer than the stored one. Null when Binance can't
    /// provide it but a copy is stored, which the caller then reads.
    /// </summary>
    /// <exception cref="MarketDataException">Binance can't provide it, and nothing is stored.</exception>
    private async Task<ExchangeInfo?> ExchangeInfoAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        ExchangeInfo info;

        try
        {
            info = await exchange.GetExchangeInfoAsync(market, environment, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var refreshedAt = await Stored(context, market, environment).MaxAsync(row => (DateTimeOffset?)row.RefreshedAt, cancellationToken);

            if (refreshedAt is null)
            {
                throw Failures.From(exception);
            }

            logger.LogWarning(
                exception,
                "Binance exchange information for {Market} {Environment} is unavailable, so the stored copy from {RefreshedAt:O} answers",
                market,
                environment,
                refreshedAt);

            return null;
        }

        await StoreAsync(info, cancellationToken);

        return info;
    }

    /// <summary>
    /// Replaces the stored copy with a newer one, in one transaction, one market and environment at a time.
    /// It's a copy, so failing to store it doesn't fail the caller, who has the data.
    /// </summary>
    private async Task StoreAsync(ExchangeInfo info, CancellationToken cancellationToken)
    {
        var pair = (info.Market, info.Environment);

        if (_stored.TryGetValue(pair, out var stored) && stored >= info.RefreshedAt)
        {
            return;
        }

        var gate = _gates.GetOrAdd(pair, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (_stored.TryGetValue(pair, out stored) && stored >= info.RefreshedAt)
            {
                return;
            }

            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var rows = await Stored(context, info.Market, info.Environment).ToDictionaryAsync(row => row.Symbol, StringComparer.Ordinal, cancellationToken);

            if (rows.Count > 0 && rows.Values.Max(row => row.RefreshedAt) >= info.RefreshedAt)
            {
                _stored[pair] = rows.Values.Max(row => row.RefreshedAt);

                return;
            }

            var added = 0;

            foreach (var rules in info.Symbols.Values)
            {
                if (rows.Remove(rules.Symbol, out var row))
                {
                    row.Update(rules, info.RefreshedAt);
                }
                else
                {
                    context.Add(ExchangeSymbol.From(info.Market, info.Environment, rules, info.RefreshedAt));
                    added++;
                }
            }

            // Whatever is left, Binance no longer lists.
            var removed = rows.Count;
            context.RemoveRange(rows.Values);

            await context.SaveChangesAsync(cancellationToken);
            _stored[pair] = info.RefreshedAt;

            logger.LogInformation(
                "Stored {SymbolCount} Binance symbols for {Market} {Environment} from {RefreshedAt:O}: {Added} added, {Removed} removed",
                info.Symbols.Count,
                info.Market,
                info.Environment,
                info.RefreshedAt,
                added,
                removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Storing Binance symbols for {Market} {Environment} failed, so the next read tries again", info.Market, info.Environment);
        }
        finally
        {
            gate.Release();
        }
    }

    private static IQueryable<ExchangeSymbol> Stored(EcnanibDbContext context, Market market, TradingEnvironment environment) =>
        context.Set<ExchangeSymbol>().Where(row => row.Market == market && row.Environment == environment);
}
