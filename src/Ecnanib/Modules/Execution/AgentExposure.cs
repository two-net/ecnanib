using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// What an agent has on the exchange, for Agents (see Inversions in docs/architecture.md). Nothing places
/// orders yet, so no agent has a position or an order: order execution and reconciliation (#34, #35) answer
/// from what they record. The balance behind FR-AGT-13's warning is read from Binance, at most once a minute
/// per key and market, because the weight is shared by every user and a page's clicks never reach the rate
/// limiter. The cache is in memory, which is enough for one instance (ASM-06).
/// </summary>
internal sealed class AgentExposure(
    ICredentialKeys keys,
    IExchangeAccount accounts,
    TimeProvider time,
    ILogger<AgentExposure> logger) : IAgentExposure
{
    public static readonly TimeSpan BalanceLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid UserId, Guid CredentialId, Market Market), (DateTimeOffset ReadAt, ExchangeAccount? Account)> _balances = new();

    // Nothing places orders before #34, so there's nothing to find. #34 and #35 answer from what they record,
    // without calling Binance, because Agents asks inside its lock.
    public Task<bool> HasOpenPositionsOrOrdersAsync(Guid userId, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public async Task<decimal?> GetAvailableBalanceAsync(
        Guid userId, Guid credentialId, Market market, string asset, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cacheKey = (userId, credentialId, market);

        if (!_balances.TryGetValue(cacheKey, out var cached) || now - cached.ReadAt >= BalanceLifetime)
        {
            cached = (now, await ReadAccountAsync(userId, credentialId, market, cancellationToken));
            _balances[cacheKey] = cached;
        }

        // Spot leaves out assets with nothing in them, and so does Futures.
        return cached.Account?.Balances.FirstOrDefault(balance => balance.Asset == asset)?.Available ?? (cached.Account is null ? null : 0m);
    }

    /// <summary>The account, or null when it can't be read, which leaves the warning out rather than guessing.</summary>
    private async Task<ExchangeAccount?> ReadAccountAsync(Guid userId, Guid credentialId, Market market, CancellationToken cancellationToken)
    {
        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, market, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            logger.LogInformation("Can't read the balance behind credential {CredentialId}: {Reason}", credentialId, exception.Reason);
            return null;
        }

        try
        {
            return await accounts.GetAccountAsync(market, key, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Reading the balance behind credential {CredentialId} failed: {Failure}", credentialId, exception.Failure);
            return null;
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }
    }
}
