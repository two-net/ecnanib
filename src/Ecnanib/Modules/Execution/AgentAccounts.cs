using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Reads an agent's balances through its own exchange key, for one use, and disposes the key after (FR-KEY-09).
/// Position and pending entry come from Execution's records once orders exist (#34, #35). See Decision cycle in
/// docs/architecture.md.
/// </summary>
internal sealed class AgentAccounts(ICredentialKeys keys, IExchangeAccount accounts, ILogger<AgentAccounts> logger) : IAgentAccounts
{
    public async Task<AgentAccount> ReadAsync(
        Guid userId,
        Guid credentialId,
        Market market,
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken)
    {
        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, market, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            throw new AgentAccountException(
                AgentAccountFailure.KeyUnusable, $"Exchange key {credentialId} can't be used: {exception.Reason}", exception);
        }

        ExchangeAccount account;

        try
        {
            account = await accounts.GetAccountAsync(market, key, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Reading the account behind exchange key {CredentialId} failed: {Failure}", credentialId, exception.Failure);

            throw new AgentAccountException(
                AgentAccountFailure.ExchangeUnavailable, $"Binance couldn't give the account: {exception.Failure}", exception);
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }

        // Nothing places orders before #34, so the agent has no position and no pending entry of its own.
        return market == Market.Spot
            ? new AgentAccount(
                Balance(account, quoteAsset)?.Available ?? 0m,
                Balance(account, baseAsset)?.Total ?? 0m,
                Position: null,
                HasPendingEntry: false)
            : new AgentAccount(account.AvailableBalance ?? 0m, null, Position: null, HasPendingEntry: false);
    }

    /// <summary>Binance leaves out assets with nothing in them, so a missing one holds nothing.</summary>
    private static AssetBalance? Balance(ExchangeAccount account, string asset) =>
        account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, asset, StringComparison.Ordinal));
}
