using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// What an agent has on the exchange, for Agents (see Inversions in docs/architecture.md). Nothing places
/// orders yet, so no agent has a position or an order: order execution and reconciliation (#34, #35) answer
/// from what they record. The balance behind FR-AGT-13's warning, and the Futures account EXT-BIN-07 checks, are
/// read from Binance at most once a minute per key, because the weight is shared by every user and a page's
/// clicks never reach the rate limiter. The cache is in memory, which is enough for one instance (ASM-06).
/// </summary>
internal sealed class AgentExposure(
    ICredentialKeys keys,
    IExchangeAccount accounts,
    TimeProvider time,
    ILogger<AgentExposure> logger) : IAgentExposure
{
    public static readonly TimeSpan BalanceLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid UserId, Guid CredentialId, Market Market), Read<ExchangeAccount>> _balances = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid CredentialId), Read<FuturesAccountConfig>> _futuresConfigs = new();

    // Nothing places orders before #34, so there's nothing to find. #34 and #35 answer from what they record,
    // without calling Binance, because Agents asks inside its lock.
    public Task<bool> HasOpenPositionsOrOrdersAsync(Guid userId, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public async Task<decimal?> GetAvailableBalanceAsync(
        Guid userId, Guid credentialId, Market market, string asset, CancellationToken cancellationToken)
    {
        var account = await AccountAsync(userId, credentialId, market, cancellationToken);

        return account.Value is { } found ? Available(found, asset) : null;
    }

    public async Task<FuturesAccountRead> ReadFuturesAccountAsync(
        Guid userId, Guid credentialId, string marginAsset, CancellationToken cancellationToken)
    {
        var config = await CachedAsync(
            _futuresConfigs,
            (userId, credentialId),
            () => ReadAsync(userId, credentialId, Market.Futures, key => accounts.GetFuturesAccountConfigAsync(key, cancellationToken), cancellationToken));

        if (config.Value is null)
        {
            return FuturesAccountRead.Failed(config.Failure ?? FuturesAccountFailure.Unavailable);
        }

        var account = await AccountAsync(userId, credentialId, Market.Futures, cancellationToken);

        if (account.Value is null)
        {
            return FuturesAccountRead.Failed(account.Failure ?? FuturesAccountFailure.Unavailable);
        }

        return FuturesAccountRead.Of(new FuturesAccountState(
            config.Value.CanTrade, config.Value.HedgeMode, config.Value.MultiAssetsMode, Available(account.Value, marginAsset)));
    }

    /// <summary>
    /// Binance leaves out assets with nothing in them, on Spot and Futures, so a missing one has nothing available.
    /// On Futures it's the asset's own available balance, which is what an isolated position in its pairs can use.
    /// </summary>
    private static decimal Available(ExchangeAccount account, string asset) =>
        account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, asset, StringComparison.Ordinal))?.Available ?? 0m;

    private Task<Read<ExchangeAccount>> AccountAsync(Guid userId, Guid credentialId, Market market, CancellationToken cancellationToken) =>
        CachedAsync(
            _balances,
            (userId, credentialId, market),
            () => ReadAsync(userId, credentialId, market, key => accounts.GetAccountAsync(market, key, cancellationToken), cancellationToken));

    private async Task<Read<T>> CachedAsync<TKey, T>(ConcurrentDictionary<TKey, Read<T>> cache, TKey cacheKey, Func<Task<Read<T>>> read)
        where TKey : notnull
        where T : class
    {
        var now = time.GetUtcNow();

        if (cache.TryGetValue(cacheKey, out var cached) && now - cached.ReadAt < BalanceLifetime)
        {
            return cached;
        }

        cached = await read() with { ReadAt = now };
        cache[cacheKey] = cached;

        return cached;
    }

    /// <summary>
    /// One signed read through the user's key, or why it failed, which leaves the caller to decide rather than
    /// guess. The key's secrets are disposed after (FR-KEY-09).
    /// </summary>
    private async Task<Read<T>> ReadAsync<T>(
        Guid userId, Guid credentialId, Market market, Func<ExchangeKey, Task<T>> read, CancellationToken cancellationToken)
        where T : class
    {
        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, market, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            logger.LogInformation("Can't read the {Market} account behind credential {CredentialId}: {Reason}", market, credentialId, exception.Reason);
            return Read<T>.Failed(FuturesAccountFailure.KeyRefused);
        }

        try
        {
            return new Read<T>(default, await read(key), null);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Reading the {Market} account behind credential {CredentialId} failed: {Failure}", market, credentialId, exception.Failure);

            // As Credentials judges a key: a rejection is Binance refusing it on this market, and anything else
            // says nothing about the key.
            return Read<T>.Failed(exception.Failure == ExchangeFailure.Rejected ? FuturesAccountFailure.KeyRefused : FuturesAccountFailure.Unavailable);
        }
        catch (ArgumentException)
        {
            // The signer couldn't import the key, so nothing was sent.
            logger.LogWarning("The key behind credential {CredentialId} couldn't sign a request", credentialId);
            return Read<T>.Failed(FuturesAccountFailure.KeyRefused);
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }
    }

    /// <summary>What a read found, or why it found nothing.</summary>
    private sealed record Read<T>(DateTimeOffset ReadAt, T? Value, FuturesAccountFailure? Failure)
        where T : class
    {
        public static Read<T> Failed(FuturesAccountFailure failure) => new(default, null, failure);
    }
}
